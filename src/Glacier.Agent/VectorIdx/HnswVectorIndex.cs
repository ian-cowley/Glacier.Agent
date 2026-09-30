namespace Glacier.Agent.VectorIdx;

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;

/// <summary>
/// Hierarchical Navigable Small World (HNSW) multi-layer graph vector index.
/// High recall, sub-millisecond nearest neighbor search, lock-free queries, SIMD distance acceleration.
/// </summary>
public sealed class HnswVectorIndex : IVectorIndex
{
    private const int MagicHeader = 0x484E5357; // "HNSW"

    private readonly int _dimension;
    private readonly DistanceMetric _metric;
    private readonly int _m;
    private readonly int _m0;
    private readonly int _efConstruction;
    private int _efSearch;
    private readonly double _mL;

    private readonly List<float[]> _vectors = new();
    private readonly List<long> _ids = new();
    private readonly Dictionary<long, int> _idToIndex = new();
    private readonly List<List<int>[]> _graph = new(); // nodeIndex -> array of neighbor lists per layer
    private readonly List<object> _nodeLocks = new();
    private readonly Lock _globalLock = new();

    private int _entryPoint = -1;
    private int _maxLayer = -1;
    private readonly Random _random = new(42);

    public int Count => _vectors.Count;
    public int Dimension => _dimension;
    public DistanceMetric Metric => _metric;
    public int EfSearch
    {
        get => _efSearch;
        set => _efSearch = Math.Max(1, value);
    }

    public HnswVectorIndex(
        int dimension,
        DistanceMetric metric = DistanceMetric.SquaredL2,
        int m = 16,
        int efConstruction = 64,
        int efSearch = 32)
    {
        if (dimension <= 0) throw new ArgumentOutOfRangeException(nameof(dimension));
        _dimension = dimension;
        _metric = metric;
        _m = Math.Max(2, m);
        _m0 = 2 * _m;
        _efConstruction = Math.Max(_m, efConstruction);
        _efSearch = Math.Max(1, efSearch);
        _mL = 1.0 / Math.Log(_m);
    }

    /// <inheritdoc />
    public void Add(long id, ReadOnlySpan<float> vector)
    {
        if (vector.Length != _dimension)
        {
            throw new ArgumentException($"Vector length ({vector.Length}) does not match index dimension ({_dimension}).", nameof(vector));
        }

        float[] storedVector = vector.ToArray();
        int nodeLevel = GenerateRandomLevel();

        int nodeIndex;
        object nodeLock = new();

        lock (_globalLock)
        {
            if (_idToIndex.TryGetValue(id, out int existingIndex))
            {
                // Update existing vector
                _vectors[existingIndex] = storedVector;
                return;
            }

            nodeIndex = _vectors.Count;
            _vectors.Add(storedVector);
            _ids.Add(id);
            _idToIndex[id] = nodeIndex;
            _nodeLocks.Add(nodeLock);

            var layers = new List<int>[nodeLevel + 1];
            for (int l = 0; l <= nodeLevel; l++)
            {
                int capacity = (l == 0) ? _m0 : _m;
                layers[l] = new List<int>(capacity);
            }
            _graph.Add(layers);

            if (_entryPoint == -1)
            {
                _entryPoint = nodeIndex;
                _maxLayer = nodeLevel;
                return;
            }
        }

        int currObj = _entryPoint;
        int currMaxLayer = _maxLayer;
        float curDist = SimdDistance.Calculate(storedVector, _vectors[currObj], _metric);

        // Greedy search down from top layer to nodeLevel + 1
        for (int l = currMaxLayer; l > nodeLevel; l--)
        {
            bool changed = true;
            while (changed)
            {
                changed = false;
                List<int> neighbors;
                lock (_nodeLocks[currObj])
                {
                    neighbors = _graph[currObj].Length > l ? new List<int>(_graph[currObj][l]) : new List<int>();
                }

                foreach (int neighbor in neighbors)
                {
                    float d = SimdDistance.Calculate(storedVector, _vectors[neighbor], _metric);
                    if (d < curDist)
                    {
                        curDist = d;
                        currObj = neighbor;
                        changed = true;
                    }
                }
            }
        }

        // Connect at layers min(nodeLevel, currMaxLayer) down to 0
        int topConnectLayer = Math.Min(nodeLevel, currMaxLayer);
        for (int l = topConnectLayer; l >= 0; l--)
        {
            var candidates = SearchLayer(storedVector, currObj, _efConstruction, l);
            int maxM = (l == 0) ? _m0 : _m;
            var selectedNeighbors = SelectNeighborsHeuristic(storedVector, candidates, maxM);

            // Connect bidirectional links
            lock (_nodeLocks[nodeIndex])
            {
                _graph[nodeIndex][l].AddRange(selectedNeighbors);
            }

            foreach (int neighbor in selectedNeighbors)
            {
                lock (_nodeLocks[neighbor])
                {
                    if (_graph[neighbor].Length > l)
                    {
                        var nList = _graph[neighbor][l];
                        nList.Add(nodeIndex);
                        if (nList.Count > maxM)
                        {
                            // Shrink connections
                            var pruned = PruneNeighbors(_vectors[neighbor], nList, maxM);
                            nList.Clear();
                            nList.AddRange(pruned);
                        }
                    }
                }
            }

            if (candidates.Count > 0)
            {
                currObj = candidates[0].NodeIndex;
            }
        }

        lock (_globalLock)
        {
            if (nodeLevel > _maxLayer)
            {
                _maxLayer = nodeLevel;
                _entryPoint = nodeIndex;
            }
        }
    }

    /// <inheritdoc />
    public int Search(ReadOnlySpan<float> query, Span<long> outIds, Span<float> outDistances, int topK)
    {
        if (query.Length != _dimension)
        {
            throw new ArgumentException($"Query vector length ({query.Length}) does not match index dimension ({_dimension}).", nameof(query));
        }

        if (_entryPoint == -1 || _vectors.Count == 0 || topK <= 0)
        {
            return 0;
        }

        int currObj = _entryPoint;
        int currMaxLayer = _maxLayer;
        float curDist = SimdDistance.Calculate(query, _vectors[currObj], _metric);

        // 1. Greedy descent down to layer 1
        for (int l = currMaxLayer; l > 0; l--)
        {
            bool changed = true;
            while (changed)
            {
                changed = false;
                List<int> neighbors = _graph[currObj].Length > l ? _graph[currObj][l] : null!;
                if (neighbors == null) break;

                for (int i = 0; i < neighbors.Count; i++)
                {
                    int neighbor = neighbors[i];
                    float d = SimdDistance.Calculate(query, _vectors[neighbor], _metric);
                    if (d < curDist)
                    {
                        curDist = d;
                        currObj = neighbor;
                        changed = true;
                    }
                }
            }
        }

        // 2. Beam search at layer 0
        int ef = Math.Max(_efSearch, topK);
        var results = SearchLayer(query, currObj, ef, 0);

        int count = Math.Min(topK, Math.Min(results.Count, Math.Min(outIds.Length, outDistances.Length)));
        for (int i = 0; i < count; i++)
        {
            outIds[i] = _ids[results[i].NodeIndex];
            outDistances[i] = results[i].Distance;
        }

        return count;
    }

    private readonly record struct Candidate(int NodeIndex, float Distance);

    private List<Candidate> SearchLayer(ReadOnlySpan<float> query, int entryPoint, int ef, int layer)
    {
        var visited = new HashSet<int>();
        var candidates = new PriorityQueue<int, float>(); // Min-heap for exploration
        var nearest = new List<Candidate>(ef + 1);

        float dEntry = SimdDistance.Calculate(query, _vectors[entryPoint], _metric);
        candidates.Enqueue(entryPoint, dEntry);
        nearest.Add(new Candidate(entryPoint, dEntry));
        visited.Add(entryPoint);

        while (candidates.Count > 0)
        {
            candidates.TryDequeue(out int currNode, out float currDist);

            float worstNearestDist = nearest[^1].Distance;
            if (currDist > worstNearestDist && nearest.Count >= ef)
            {
                break;
            }

            List<int> neighbors = _graph[currNode].Length > layer ? _graph[currNode][layer] : null!;
            if (neighbors == null) continue;

            int nSize = neighbors.Count;
            for (int i = 0; i < nSize; i++)
            {
                int neighbor = neighbors[i];
                if (visited.Add(neighbor))
                {
                    float d = SimdDistance.Calculate(query, _vectors[neighbor], _metric);
                    if (d < worstNearestDist || nearest.Count < ef)
                    {
                        candidates.Enqueue(neighbor, d);
                        InsertSorted(nearest, new Candidate(neighbor, d));
                        if (nearest.Count > ef)
                        {
                            nearest.RemoveAt(nearest.Count - 1);
                        }
                        worstNearestDist = nearest[^1].Distance;
                    }
                }
            }
        }

        return nearest;
    }

    private static void InsertSorted(List<Candidate> list, Candidate item)
    {
        int idx = list.BinarySearch(item, CandidateComparer.Instance);
        if (idx < 0) idx = ~idx;
        list.Insert(idx, item);
    }

    private sealed class CandidateComparer : IComparer<Candidate>
    {
        public static readonly CandidateComparer Instance = new();
        public int Compare(Candidate x, Candidate y) => x.Distance.CompareTo(y.Distance);
    }

    private List<int> SelectNeighborsHeuristic(ReadOnlySpan<float> targetVec, List<Candidate> candidates, int maxM)
    {
        if (candidates.Count <= maxM)
        {
            var res = new List<int>(candidates.Count);
            foreach (var c in candidates) res.Add(c.NodeIndex);
            return res;
        }

        var result = new List<int>(maxM);
        foreach (var candidate in candidates)
        {
            if (result.Count >= maxM) break;

            bool isDiverse = true;
            ReadOnlySpan<float> candVec = _vectors[candidate.NodeIndex];

            foreach (int selected in result)
            {
                float distBetweenNeighbors = SimdDistance.Calculate(candVec, _vectors[selected], _metric);
                if (distBetweenNeighbors < candidate.Distance)
                {
                    isDiverse = false;
                    break;
                }
            }

            if (isDiverse)
            {
                result.Add(candidate.NodeIndex);
            }
        }

        // Fill up to maxM if heuristic was too strict
        if (result.Count < maxM)
        {
            foreach (var c in candidates)
            {
                if (!result.Contains(c.NodeIndex))
                {
                    result.Add(c.NodeIndex);
                    if (result.Count >= maxM) break;
                }
            }
        }

        return result;
    }

    private List<int> PruneNeighbors(ReadOnlySpan<float> baseVec, List<int> neighbors, int maxM)
    {
        var candidates = new List<Candidate>(neighbors.Count);
        foreach (int n in neighbors)
        {
            float d = SimdDistance.Calculate(baseVec, _vectors[n], _metric);
            candidates.Add(new Candidate(n, d));
        }
        candidates.Sort(CandidateComparer.Instance);
        return SelectNeighborsHeuristic(baseVec, candidates, maxM);
    }

    private int GenerateRandomLevel()
    {
        double r = _random.NextDouble();
        if (r == 0.0) r = 0.0000001;
        int level = (int)(-Math.Log(r) * _mL);
        return Math.Min(level, 16);
    }

    /// <inheritdoc />
    public void Save(Stream stream)
    {
        using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        writer.Write(MagicHeader);
        writer.Write(1); // Version
        writer.Write(_dimension);
        writer.Write((int)_metric);
        writer.Write(_m);
        writer.Write(_m0);
        writer.Write(_efConstruction);
        writer.Write(_efSearch);
        writer.Write(_entryPoint);
        writer.Write(_maxLayer);

        int count = _vectors.Count;
        writer.Write(count);

        for (int i = 0; i < count; i++)
        {
            writer.Write(_ids[i]);
            float[] vec = _vectors[i];
            for (int d = 0; d < _dimension; d++)
            {
                writer.Write(vec[d]);
            }

            var layers = _graph[i];
            writer.Write(layers.Length);
            for (int l = 0; l < layers.Length; l++)
            {
                var nList = layers[l];
                writer.Write(nList.Count);
                for (int n = 0; n < nList.Count; n++)
                {
                    writer.Write(nList[n]);
                }
            }
        }
    }

    /// <inheritdoc />
    public void Load(Stream stream)
    {
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        int magic = reader.ReadInt32();
        if (magic != MagicHeader)
        {
            throw new InvalidDataException("Invalid HNSW file header magic.");
        }

        int version = reader.ReadInt32();
        int dim = reader.ReadInt32();
        if (dim != _dimension)
        {
            throw new InvalidDataException($"Dimension mismatch: index is {_dimension}, stream is {dim}.");
        }

        DistanceMetric metric = (DistanceMetric)reader.ReadInt32();
        int m = reader.ReadInt32();
        int m0 = reader.ReadInt32();
        int efConstruction = reader.ReadInt32();
        _efSearch = reader.ReadInt32();
        _entryPoint = reader.ReadInt32();
        _maxLayer = reader.ReadInt32();

        int count = reader.ReadInt32();

        lock (_globalLock)
        {
            _vectors.Clear();
            _ids.Clear();
            _idToIndex.Clear();
            _graph.Clear();
            _nodeLocks.Clear();

            for (int i = 0; i < count; i++)
            {
                long id = reader.ReadInt64();
                float[] vec = new float[_dimension];
                for (int d = 0; d < _dimension; d++)
                {
                    vec[d] = reader.ReadSingle();
                }

                int layerCount = reader.ReadInt32();
                var layers = new List<int>[layerCount];
                for (int l = 0; l < layerCount; l++)
                {
                    int nSize = reader.ReadInt32();
                    var nList = new List<int>(nSize);
                    for (int n = 0; n < nSize; n++)
                    {
                        nList.Add(reader.ReadInt32());
                    }
                    layers[l] = nList;
                }

                _vectors.Add(vec);
                _ids.Add(id);
                _idToIndex[id] = i;
                _graph.Add(layers);
                _nodeLocks.Add(new object());
            }
        }
    }

    public void Dispose()
    {
        // No unmanaged allocations requiring finalization
    }
}
