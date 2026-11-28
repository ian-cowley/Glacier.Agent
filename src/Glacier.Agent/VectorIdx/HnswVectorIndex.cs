namespace Glacier.Agent.VectorIdx;

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

/// <summary>
/// Hierarchical Navigable Small World (HNSW) multi-layer graph vector index.
/// High recall, sub-millisecond nearest neighbor search, concurrent read queries via ReaderWriterLockSlim,
/// zero-allocation steady-state search scratch, and SIMD distance acceleration.
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
    private readonly ReaderWriterLockSlim _rwLock = new(LockRecursionPolicy.NoRecursion);

    private int _entryPoint = -1;
    private int _maxLayer = -1;

    public int Count
    {
        get
        {
            _rwLock.EnterReadLock();
            try
            {
                return _vectors.Count;
            }
            finally
            {
                _rwLock.ExitReadLock();
            }
        }
    }

    public int Dimension => _dimension;
    public DistanceMetric Metric => _metric;
    public int EfSearch
    {
        get => Volatile.Read(ref _efSearch);
        set => Volatile.Write(ref _efSearch, Math.Max(1, value));
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
        int newLevel = GenerateRandomLevel();

        _rwLock.EnterWriteLock();
        try
        {
            int nodeIndex;
            int nodeLevel;

            if (_idToIndex.TryGetValue(id, out int existingIndex))
            {
                // Unlink existing node (both outgoing and asymmetric incoming edges from prior pruning) before relinking.
                List<int>[] existingLayers = _graph[existingIndex];
                for (int l = 0; l < existingLayers.Length; l++)
                {
                    existingLayers[l].Clear();
                }

                for (int i = 0; i < _graph.Count; i++)
                {
                    if (i == existingIndex) continue;
                    List<int>[] otherLayers = _graph[i];
                    int sharedLayers = Math.Min(existingLayers.Length, otherLayers.Length);
                    for (int l = 0; l < sharedLayers; l++)
                    {
                        List<int> nList = otherLayers[l];
                        while (nList.Remove(existingIndex))
                        {
                        }
                    }
                }

                _vectors[existingIndex] = storedVector;

                if (_vectors.Count == 1)
                {
                    _entryPoint = existingIndex;
                    _maxLayer = existingLayers.Length - 1;
                    return;
                }

                if (_entryPoint == existingIndex)
                {
                    int bestEntry = -1;
                    int bestLayer = -1;
                    for (int i = 0; i < _vectors.Count; i++)
                    {
                        if (i == existingIndex) continue;
                        int maxL = _graph[i].Length - 1;
                        if (maxL > bestLayer)
                        {
                            bestLayer = maxL;
                            bestEntry = i;
                        }
                    }
                    _entryPoint = bestEntry;
                    _maxLayer = bestLayer;
                }

                nodeIndex = existingIndex;
                nodeLevel = existingLayers.Length - 1;
            }
            else
            {
                nodeIndex = _vectors.Count;
                nodeLevel = newLevel;
                _vectors.Add(storedVector);
                _ids.Add(id);
                _idToIndex[id] = nodeIndex;

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
                    List<int>[] currLayers = _graph[currObj];
                    if (l >= currLayers.Length) break;
                    ReadOnlySpan<int> neighbors = CollectionsMarshal.AsSpan(currLayers[l]);
                    for (int i = 0; i < neighbors.Length; i++)
                    {
                        int neighbor = neighbors[i];
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
            var selectedScratch = new List<int>(_m0);
            var pruneScratch = new List<Candidate>(_m0 + 2);

            for (int l = topConnectLayer; l >= 0; l--)
            {
                ReadOnlySpan<Candidate> candidates = SearchLayerCore(storedVector, currObj, _efConstruction, l);
                if (candidates.Length > 0)
                {
                    currObj = candidates[0].NodeIndex;
                }

                int maxM = (l == 0) ? _m0 : _m;
                selectedScratch.Clear();
                SelectNeighborsHeuristic(candidates, maxM, selectedScratch);

                List<int> nodeLayerList = _graph[nodeIndex][l];
                for (int i = 0; i < selectedScratch.Count; i++)
                {
                    int neighbor = selectedScratch[i];
                    if (neighbor == nodeIndex) continue;
                    nodeLayerList.Add(neighbor);

                    List<int>[] neighborLayers = _graph[neighbor];
                    if (l < neighborLayers.Length)
                    {
                        List<int> nList = neighborLayers[l];
                        nList.Add(nodeIndex);
                        if (nList.Count > maxM)
                        {
                            PruneNeighbors(_vectors[neighbor], nList, maxM, pruneScratch);
                        }
                    }
                }
            }

            if (nodeLevel > _maxLayer)
            {
                _maxLayer = nodeLevel;
                _entryPoint = nodeIndex;
            }
        }
        finally
        {
            _rwLock.ExitWriteLock();
        }
    }

    /// <inheritdoc />
    public int Search(ReadOnlySpan<float> query, Span<long> outIds, Span<float> outDistances, int topK)
    {
        if (query.Length != _dimension)
        {
            throw new ArgumentException($"Query vector length ({query.Length}) does not match index dimension ({_dimension}).", nameof(query));
        }

        if (topK <= 0)
        {
            return 0;
        }

        _rwLock.EnterReadLock();
        try
        {
            if (_entryPoint == -1 || _vectors.Count == 0)
            {
                return 0;
            }

            int currObj = _entryPoint;
            int currMaxLayer = _maxLayer;
            float curDist = SimdDistance.Calculate(query, _vectors[currObj], _metric);

            // 1. Greedy descent down to layer 1 (safe under read lock)
            for (int l = currMaxLayer; l > 0; l--)
            {
                bool changed = true;
                while (changed)
                {
                    changed = false;
                    List<int>[] currLayers = _graph[currObj];
                    if (l >= currLayers.Length) break;
                    ReadOnlySpan<int> neighbors = CollectionsMarshal.AsSpan(currLayers[l]);
                    for (int i = 0; i < neighbors.Length; i++)
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

            // 2. Beam search at layer 0 using thread-local zero-allocation scratch
            int ef = Math.Max(EfSearch, topK);
            ReadOnlySpan<Candidate> results = SearchLayerCore(query, currObj, ef, 0);

            int count = Math.Min(topK, Math.Min(results.Length, Math.Min(outIds.Length, outDistances.Length)));
            for (int i = 0; i < count; i++)
            {
                outIds[i] = _ids[results[i].NodeIndex];
                outDistances[i] = results[i].Distance;
            }

            return count;
        }
        finally
        {
            _rwLock.ExitReadLock();
        }
    }

    private readonly record struct Candidate(int NodeIndex, float Distance);

    private ReadOnlySpan<Candidate> SearchLayerCore(ReadOnlySpan<float> query, int entryPoint, int ef, int layer)
    {
        HnswAgentSearchScratch scratch = HnswAgentSearchScratch.Acquire(_vectors.Count, ef);
        ref ValueMinHeap candidates = ref scratch.Candidates;
        ref ValueMaxHeap results = ref scratch.Results;

        float dEntry = SimdDistance.Calculate(query, _vectors[entryPoint], _metric);
        scratch.TryMarkVisited(entryPoint);
        candidates.Push(new Candidate(entryPoint, dEntry));
        results.Push(new Candidate(entryPoint, dEntry));

        while (candidates.Count > 0)
        {
            Candidate curr = candidates.Pop();
            if (results.Count >= ef && curr.Distance > results.PeekMax().Distance)
            {
                break;
            }

            List<int>[] currLayers = _graph[curr.NodeIndex];
            if (layer >= currLayers.Length)
            {
                continue;
            }

            ReadOnlySpan<int> neighbors = CollectionsMarshal.AsSpan(currLayers[layer]);
            for (int i = 0; i < neighbors.Length; i++)
            {
                int neighbor = neighbors[i];
                if (scratch.TryMarkVisited(neighbor))
                {
                    float d = SimdDistance.Calculate(query, _vectors[neighbor], _metric);
                    if (results.Count < ef)
                    {
                        candidates.Push(new Candidate(neighbor, d));
                        results.Push(new Candidate(neighbor, d));
                    }
                    else if (d < results.PeekMax().Distance)
                    {
                        candidates.Push(new Candidate(neighbor, d));
                        results.ReplaceTop(new Candidate(neighbor, d));
                    }
                }
            }
        }

        results.SortAscendingInPlace();
        return results.AsSpan();
    }

    private sealed class CandidateComparer : IComparer<Candidate>
    {
        public static readonly CandidateComparer Instance = new();
        public int Compare(Candidate x, Candidate y) => x.Distance.CompareTo(y.Distance);
    }

    private void SelectNeighborsHeuristic(ReadOnlySpan<Candidate> candidates, int maxM, List<int> result)
    {
        if (candidates.Length <= maxM)
        {
            for (int i = 0; i < candidates.Length; i++)
            {
                result.Add(candidates[i].NodeIndex);
            }
            return;
        }

        for (int i = 0; i < candidates.Length; i++)
        {
            if (result.Count >= maxM) break;

            Candidate candidate = candidates[i];
            bool isDiverse = true;
            ReadOnlySpan<float> candVec = _vectors[candidate.NodeIndex];

            for (int j = 0; j < result.Count; j++)
            {
                float distBetweenNeighbors = SimdDistance.Calculate(candVec, _vectors[result[j]], _metric);
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
            for (int i = 0; i < candidates.Length; i++)
            {
                int candIdx = candidates[i].NodeIndex;
                if (!result.Contains(candIdx))
                {
                    result.Add(candIdx);
                    if (result.Count >= maxM) break;
                }
            }
        }
    }

    private void PruneNeighbors(ReadOnlySpan<float> baseVec, List<int> neighbors, int maxM, List<Candidate> candidatesScratch)
    {
        candidatesScratch.Clear();
        for (int i = 0; i < neighbors.Count; i++)
        {
            int n = neighbors[i];
            float d = SimdDistance.Calculate(baseVec, _vectors[n], _metric);
            candidatesScratch.Add(new Candidate(n, d));
        }
        candidatesScratch.Sort(CandidateComparer.Instance);

        neighbors.Clear();
        SelectNeighborsHeuristic(CollectionsMarshal.AsSpan(candidatesScratch), maxM, neighbors);
    }

    private int GenerateRandomLevel()
    {
        double r = Random.Shared.NextDouble();
        if (r == 0.0) r = 0.0000001;
        int level = (int)(-Math.Log(r) * _mL);
        return Math.Min(level, 16);
    }

    /// <inheritdoc />
    public void Save(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        _rwLock.EnterReadLock();
        try
        {
            using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
            writer.Write(MagicHeader);
            writer.Write(1); // Version
            writer.Write(_dimension);
            writer.Write((int)_metric);
            writer.Write(_m);
            writer.Write(_m0);
            writer.Write(_efConstruction);
            writer.Write(EfSearch);
            writer.Write(_entryPoint);
            writer.Write(_maxLayer);

            int count = _vectors.Count;
            writer.Write(count);

            for (int i = 0; i < count; i++)
            {
                writer.Write(_ids[i]);
                float[] vec = _vectors[i];
                if (BitConverter.IsLittleEndian)
                {
                    writer.Write(MemoryMarshal.AsBytes(vec.AsSpan()));
                }
                else
                {
                    for (int d = 0; d < _dimension; d++)
                    {
                        writer.Write(vec[d]);
                    }
                }

                List<int>[] layers = _graph[i];
                writer.Write(layers.Length);
                for (int l = 0; l < layers.Length; l++)
                {
                    List<int> nList = layers[l];
                    writer.Write(nList.Count);
                    if (BitConverter.IsLittleEndian)
                    {
                        writer.Write(MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(nList)));
                    }
                    else
                    {
                        for (int n = 0; n < nList.Count; n++)
                        {
                            writer.Write(nList[n]);
                        }
                    }
                }
            }
        }
        finally
        {
            _rwLock.ExitReadLock();
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Throws <see cref="InvalidDataException"/> on malformed headers, incompatible dimensions/parameters,
    /// or out-of-range counts/entry points/neighbor indices, and <see cref="EndOfStreamException"/> if the stream ends prematurely.
    /// </remarks>
    public void Load(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        int magic = reader.ReadInt32();
        if (magic != MagicHeader)
        {
            throw new InvalidDataException("Invalid HNSW file header magic.");
        }

        int version = reader.ReadInt32();
        if (version != 1)
        {
            throw new InvalidDataException($"Unsupported HNSW version: {version}.");
        }

        int dim = reader.ReadInt32();
        if (dim != _dimension)
        {
            throw new InvalidDataException($"Dimension mismatch: index is {_dimension}, stream is {dim}.");
        }

        DistanceMetric metric = (DistanceMetric)reader.ReadInt32();
        if (!Enum.IsDefined(metric))
        {
            throw new InvalidDataException($"Invalid distance metric: {(int)metric}.");
        }

        int m = reader.ReadInt32();
        int m0 = reader.ReadInt32();
        int efConstruction = reader.ReadInt32();
        int efSearch = reader.ReadInt32();
        int entryPoint = reader.ReadInt32();
        int maxLayer = reader.ReadInt32();
        int count = reader.ReadInt32();

        if (m < 2 || m0 < m || efConstruction < 1 || efSearch < 1)
        {
            throw new InvalidDataException("Invalid HNSW configuration parameters in stream.");
        }
        if (count < 0 || count > 50_000_000)
        {
            throw new InvalidDataException($"Invalid HNSW node count: {count}.");
        }
        if (entryPoint < -1 || (count == 0 && entryPoint != -1) || (count > 0 && (entryPoint < 0 || entryPoint >= count)))
        {
            throw new InvalidDataException($"Invalid HNSW entry point: {entryPoint} for count {count}.");
        }
        if (maxLayer < -1 || maxLayer > 16)
        {
            throw new InvalidDataException($"Invalid HNSW max layer: {maxLayer}.");
        }

        var loadedVectors = new List<float[]>(count);
        var loadedIds = new List<long>(count);
        var loadedIdToIndex = new Dictionary<long, int>(count);
        var loadedGraph = new List<List<int>[]>(count);

        for (int i = 0; i < count; i++)
        {
            long id = reader.ReadInt64();
            float[] vec = new float[_dimension];
            if (BitConverter.IsLittleEndian)
            {
                ReadExactBytes(reader, MemoryMarshal.AsBytes(vec.AsSpan()));
            }
            else
            {
                for (int d = 0; d < _dimension; d++)
                {
                    vec[d] = reader.ReadSingle();
                }
            }

            int layerCount = reader.ReadInt32();
            if (layerCount < 1 || layerCount > 17)
            {
                throw new InvalidDataException($"Invalid HNSW layer count {layerCount} at node {i}.");
            }

            var layers = new List<int>[layerCount];
            for (int l = 0; l < layerCount; l++)
            {
                int nSize = reader.ReadInt32();
                int maxAllowedNeighbors = Math.Max(count, m0);
                if (nSize < 0 || nSize > maxAllowedNeighbors)
                {
                    throw new InvalidDataException($"Invalid HNSW neighbor count {nSize} at node {i}, layer {l}.");
                }

                var nList = new List<int>(nSize);
                if (nSize > 0)
                {
                    CollectionsMarshal.SetCount(nList, nSize);
                    Span<int> nSpan = CollectionsMarshal.AsSpan(nList);
                    if (BitConverter.IsLittleEndian)
                    {
                        ReadExactBytes(reader, MemoryMarshal.AsBytes(nSpan));
                    }
                    else
                    {
                        for (int n = 0; n < nSize; n++)
                        {
                            nSpan[n] = reader.ReadInt32();
                        }
                    }

                    for (int n = 0; n < nSize; n++)
                    {
                        if ((uint)nSpan[n] >= (uint)count)
                        {
                            throw new InvalidDataException($"Out-of-range HNSW neighbor index {nSpan[n]} at node {i}, layer {l}.");
                        }
                    }
                }
                layers[l] = nList;
            }

            loadedVectors.Add(vec);
            loadedIds.Add(id);
            loadedIdToIndex[id] = i;
            loadedGraph.Add(layers);
        }

        _rwLock.EnterWriteLock();
        try
        {
            _efSearch = Math.Max(1, efSearch);
            _entryPoint = entryPoint;
            _maxLayer = maxLayer;

            _vectors.Clear();
            _ids.Clear();
            _idToIndex.Clear();
            _graph.Clear();

            _vectors.AddRange(loadedVectors);
            _ids.AddRange(loadedIds);
            foreach (KeyValuePair<long, int> kvp in loadedIdToIndex)
            {
                _idToIndex[kvp.Key] = kvp.Value;
            }
            _graph.AddRange(loadedGraph);
        }
        finally
        {
            _rwLock.ExitWriteLock();
        }
    }

    private static void ReadExactBytes(BinaryReader reader, Span<byte> destination)
    {
        int totalRead = 0;
        while (totalRead < destination.Length)
        {
            int read = reader.Read(destination.Slice(totalRead));
            if (read == 0)
            {
                throw new EndOfStreamException("Unexpected end of stream while reading HNSW binary payload.");
            }
            totalRead += read;
        }
    }

    public void Dispose()
    {
        _rwLock.Dispose();
    }

    private sealed class HnswAgentSearchScratch
    {
        public uint[] VisitedTags = Array.Empty<uint>();
        public uint CurrentTag;
        public ValueMinHeap Candidates = new(64);
        public ValueMaxHeap Results = new(64);

        [ThreadStatic]
        private static HnswAgentSearchScratch? t_instance;

        public static HnswAgentSearchScratch Acquire(int nodeCount, int ef)
        {
            HnswAgentSearchScratch scratch = t_instance ??= new HnswAgentSearchScratch();
            scratch.Prepare(nodeCount, ef);
            return scratch;
        }

        private void Prepare(int nodeCount, int ef)
        {
            if (VisitedTags.Length < nodeCount)
            {
                int newSize = Math.Max(256, Math.Max(nodeCount, VisitedTags.Length * 2));
                VisitedTags = new uint[newSize];
                CurrentTag = 1;
            }
            else
            {
                CurrentTag++;
                if (CurrentTag == 0)
                {
                    Array.Clear(VisitedTags, 0, VisitedTags.Length);
                    CurrentTag = 1;
                }
            }

            Candidates.ClearAndEnsureCapacity(Math.Max(64, ef * 2));
            Results.ClearAndEnsureCapacity(Math.Max(64, ef + 1));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryMarkVisited(int nodeIndex)
        {
            if (VisitedTags[nodeIndex] == CurrentTag)
            {
                return false;
            }
            VisitedTags[nodeIndex] = CurrentTag;
            return true;
        }
    }

    private struct ValueMinHeap
    {
        private Candidate[] _items;
        private int _count;

        public ValueMinHeap(int capacity)
        {
            _items = new Candidate[Math.Max(16, capacity)];
            _count = 0;
        }

        public readonly int Count => _count;

        public void ClearAndEnsureCapacity(int minCapacity)
        {
            _count = 0;
            if (_items.Length < minCapacity)
            {
                _items = new Candidate[Math.Max(minCapacity, _items.Length * 2)];
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Push(Candidate item)
        {
            if (_count == _items.Length)
            {
                Array.Resize(ref _items, _items.Length * 2);
            }
            int i = _count++;
            _items[i] = item;
            SiftUp(i);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Candidate Pop()
        {
            Candidate top = _items[0];
            int last = --_count;
            if (last > 0)
            {
                _items[0] = _items[last];
                SiftDown(0);
            }
            return top;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void SiftUp(int i)
        {
            Candidate item = _items[i];
            while (i > 0)
            {
                int parent = (i - 1) >> 1;
                if (item.Distance >= _items[parent].Distance) break;
                _items[i] = _items[parent];
                i = parent;
            }
            _items[i] = item;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void SiftDown(int i)
        {
            Candidate item = _items[i];
            int half = _count >> 1;
            while (i < half)
            {
                int child = (i << 1) + 1;
                int right = child + 1;
                if (right < _count && _items[right].Distance < _items[child].Distance)
                {
                    child = right;
                }
                if (item.Distance <= _items[child].Distance) break;
                _items[i] = _items[child];
                i = child;
            }
            _items[i] = item;
        }
    }

    private struct ValueMaxHeap
    {
        private Candidate[] _items;
        private int _count;

        public ValueMaxHeap(int capacity)
        {
            _items = new Candidate[Math.Max(16, capacity)];
            _count = 0;
        }

        public readonly int Count => _count;

        public void ClearAndEnsureCapacity(int minCapacity)
        {
            _count = 0;
            if (_items.Length < minCapacity)
            {
                _items = new Candidate[Math.Max(minCapacity, _items.Length * 2)];
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly Candidate PeekMax() => _items[0];

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Push(Candidate item)
        {
            if (_count == _items.Length)
            {
                Array.Resize(ref _items, _items.Length * 2);
            }
            int i = _count++;
            _items[i] = item;
            SiftUp(i);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ReplaceTop(Candidate item)
        {
            _items[0] = item;
            SiftDown(0);
        }

        public void SortAscendingInPlace()
        {
            int originalCount = _count;
            for (int i = originalCount - 1; i > 0; i--)
            {
                Candidate max = _items[0];
                _count = i;
                _items[0] = _items[i];
                SiftDown(0);
                _items[i] = max;
            }
            _count = originalCount;
        }

        public readonly ReadOnlySpan<Candidate> AsSpan() => _items.AsSpan(0, _count);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void SiftUp(int i)
        {
            Candidate item = _items[i];
            while (i > 0)
            {
                int parent = (i - 1) >> 1;
                if (item.Distance <= _items[parent].Distance) break;
                _items[i] = _items[parent];
                i = parent;
            }
            _items[i] = item;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void SiftDown(int i)
        {
            Candidate item = _items[i];
            int half = _count >> 1;
            while (i < half)
            {
                int child = (i << 1) + 1;
                int right = child + 1;
                if (right < _count && _items[right].Distance > _items[child].Distance)
                {
                    child = right;
                }
                if (item.Distance >= _items[child].Distance) break;
                _items[i] = _items[child];
                i = child;
            }
            _items[i] = item;
        }
    }
}
