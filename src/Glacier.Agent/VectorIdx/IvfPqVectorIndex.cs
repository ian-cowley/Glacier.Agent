namespace Glacier.Agent.VectorIdx;

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Threading;

/// <summary>
/// Inverted File with Product Quantization (IVF-PQ) vector index supporting k-means training,
/// SquaredL2 / Cosine / DotProduct metrics, and Asymmetric Distance Computation (ADC).
/// </summary>
public sealed class IvfPqVectorIndex : IVectorIndex
{
    private const int MagicHeader = 0x49564650; // "IVFP"

    private readonly int _dimension;
    private readonly int _numSubVectors;
    private readonly int _subDim;
    private readonly int _numCentroids;
    private readonly int _numClusters;
    private DistanceMetric _metric;
    private int _nProbe = 4;

    private readonly float[] _codebooks;       // [numSubVectors * 256 * subDim]
    private readonly float[] _coarseCentroids; // [numClusters * dimension]
    private readonly List<int>[] _invertedLists;

    private readonly List<long> _ids = new();
    private readonly List<byte[]> _quantizedCodes = new();
    private readonly List<int> _coarseAssignments = new();
    private readonly Lock _syncLock = new();
    private bool _isTrained;

    public int Count { get { lock (_syncLock) return _ids.Count; } }
    public int Dimension => _dimension;
    public int NumSubVectors => _numSubVectors;
    public int SubDimension => _subDim;
    public DistanceMetric Metric => _metric;
    public bool IsTrained { get { lock (_syncLock) return _isTrained; } }

    public int NProbe
    {
        get => _nProbe;
        set => _nProbe = Math.Max(1, Math.Min(_numClusters, value));
    }

    public IvfPqVectorIndex(
        int dimension,
        int numSubVectors = 8,
        int numClusters = 16,
        DistanceMetric metric = DistanceMetric.SquaredL2)
    {
        if (dimension <= 0) throw new ArgumentOutOfRangeException(nameof(dimension));
        if (numSubVectors <= 0 || dimension % numSubVectors != 0)
        {
            throw new ArgumentException($"Dimension ({dimension}) must be divisible by numSubVectors ({numSubVectors}).", nameof(numSubVectors));
        }

        _dimension = dimension;
        _numSubVectors = numSubVectors;
        _subDim = dimension / numSubVectors;
        _numCentroids = 256;
        _numClusters = Math.Max(1, numClusters);
        _metric = metric;

        _codebooks = new float[_numSubVectors * _numCentroids * _subDim];
        _coarseCentroids = new float[_numClusters * _dimension];
        _invertedLists = new List<int>[_numClusters];
        for (int i = 0; i < _numClusters; i++)
        {
            _invertedLists[i] = new List<int>();
        }

        InitializeSeedCodebooks();
    }

    private void InitializeSeedCodebooks()
    {
        var rand = new Random(1337);
        for (int i = 0; i < _codebooks.Length; i++)
        {
            _codebooks[i] = (float)(rand.NextDouble() * 2.0 - 1.0);
        }

        for (int i = 0; i < _coarseCentroids.Length; i++)
        {
            _coarseCentroids[i] = (float)(rand.NextDouble() * 2.0 - 1.0);
        }

        if (_metric == DistanceMetric.Cosine)
        {
            for (int k = 0; k < _numClusters; k++)
            {
                NormalizeInPlace(_coarseCentroids.AsSpan(k * _dimension, _dimension));
            }
        }

        _isTrained = false;
    }

    /// <summary>
    /// Trains coarse Voronoi centroids and per-subspace PQ codebooks via Lloyd's k-means clustering.
    /// </summary>
    public void Train(IReadOnlyList<float[]> trainingVectors, int maxIterations = 12)
    {
        ArgumentNullException.ThrowIfNull(trainingVectors);
        if (trainingVectors.Count == 0)
        {
            throw new ArgumentException("At least one training vector is required.", nameof(trainingVectors));
        }

        int n = trainingVectors.Count;
        var prepared = new float[n][];
        for (int i = 0; i < n; i++)
        {
            if (trainingVectors[i].Length != _dimension)
            {
                throw new ArgumentException($"Training vector {i} length ({trainingVectors[i].Length}) != {_dimension}.", nameof(trainingVectors));
            }
            prepared[i] = (float[])trainingVectors[i].Clone();
            if (_metric == DistanceMetric.Cosine)
            {
                NormalizeInPlace(prepared[i]);
            }
        }

        lock (_syncLock)
        {
            int iters = Math.Max(1, maxIterations);

            // 1. Train coarse centroids (K = min(_numClusters, n))
            int activeCoarse = Math.Min(_numClusters, n);
            for (int k = 0; k < _numClusters; k++)
            {
                prepared[k % n].AsSpan().CopyTo(_coarseCentroids.AsSpan(k * _dimension, _dimension));
            }

            int[] assignments = new int[n];
            int[] counts = new int[activeCoarse];
            float[] sums = new float[activeCoarse * _dimension];

            for (int iter = 0; iter < iters; iter++)
            {
                Array.Clear(counts);
                Array.Clear(sums);

                for (int i = 0; i < n; i++)
                {
                    int bestK = 0;
                    float bestD = float.MaxValue;
                    for (int k = 0; k < activeCoarse; k++)
                    {
                        float d = SimdDistance.SquaredL2(prepared[i], _coarseCentroids.AsSpan(k * _dimension, _dimension));
                        if (d < bestD) { bestD = d; bestK = k; }
                    }
                    assignments[i] = bestK;
                    counts[bestK]++;
                    Span<float> sumSlice = sums.AsSpan(bestK * _dimension, _dimension);
                    for (int d = 0; d < _dimension; d++) sumSlice[d] += prepared[i][d];
                }

                for (int k = 0; k < activeCoarse; k++)
                {
                    if (counts[k] > 0)
                    {
                        float inv = 1.0f / counts[k];
                        Span<float> cSlice = _coarseCentroids.AsSpan(k * _dimension, _dimension);
                        Span<float> sSlice = sums.AsSpan(k * _dimension, _dimension);
                        for (int d = 0; d < _dimension; d++) cSlice[d] = sSlice[d] * inv;
                        if (_metric == DistanceMetric.Cosine) NormalizeInPlace(cSlice);
                    }
                }
            }

            // 2. Train each PQ subspace codebook (C = min(256, n))
            int activeSubCentroids = Math.Min(_numCentroids, n);
            int[] subCounts = new int[activeSubCentroids];
            float[] subSums = new float[activeSubCentroids * _subDim];

            for (int m = 0; m < _numSubVectors; m++)
            {
                int codebookBase = m * _numCentroids * _subDim;
                int vecOffset = m * _subDim;

                for (int c = 0; c < _numCentroids; c++)
                {
                    prepared[c % n].AsSpan(vecOffset, _subDim).CopyTo(_codebooks.AsSpan(codebookBase + c * _subDim, _subDim));
                }

                for (int iter = 0; iter < iters; iter++)
                {
                    Array.Clear(subCounts);
                    Array.Clear(subSums);

                    for (int i = 0; i < n; i++)
                    {
                        ReadOnlySpan<float> subVec = prepared[i].AsSpan(vecOffset, _subDim);
                        int bestC = 0;
                        float bestD = float.MaxValue;
                        for (int c = 0; c < activeSubCentroids; c++)
                        {
                            float d = SimdDistance.SquaredL2(subVec, _codebooks.AsSpan(codebookBase + c * _subDim, _subDim));
                            if (d < bestD) { bestD = d; bestC = c; }
                        }
                        subCounts[bestC]++;
                        Span<float> sumSlice = subSums.AsSpan(bestC * _subDim, _subDim);
                        for (int d = 0; d < _subDim; d++) sumSlice[d] += subVec[d];
                    }

                    for (int c = 0; c < activeSubCentroids; c++)
                    {
                        if (subCounts[c] > 0)
                        {
                            float inv = 1.0f / subCounts[c];
                            Span<float> cSlice = _codebooks.AsSpan(codebookBase + c * _subDim, _subDim);
                            Span<float> sSlice = subSums.AsSpan(c * _subDim, _subDim);
                            for (int d = 0; d < _subDim; d++) cSlice[d] = sSlice[d] * inv;
                        }
                    }
                }
            }

            _isTrained = true;
        }
    }

    /// <inheritdoc />
    public void Add(long id, ReadOnlySpan<float> vector)
    {
        if (vector.Length != _dimension)
        {
            throw new ArgumentException($"Vector length ({vector.Length}) does not match index dimension ({_dimension}).", nameof(vector));
        }

        float[]? rentedNorm = null;
        ReadOnlySpan<float> workingVec = vector;
        if (_metric == DistanceMetric.Cosine)
        {
            rentedNorm = ArrayPool<float>.Shared.Rent(_dimension);
            Span<float> normSpan = rentedNorm.AsSpan(0, _dimension);
            vector.CopyTo(normSpan);
            NormalizeInPlace(normSpan);
            workingVec = normSpan;
        }

        try
        {
            lock (_syncLock)
            {
                int bestCluster = 0;
                float bestCoarseDist = float.MaxValue;
                for (int k = 0; k < _numClusters; k++)
                {
                    ReadOnlySpan<float> centroid = _coarseCentroids.AsSpan(k * _dimension, _dimension);
                    float dist = _metric == DistanceMetric.DotProduct
                        ? -SimdDistance.DotProduct(workingVec, centroid)
                        : SimdDistance.SquaredL2(workingVec, centroid);
                    if (dist < bestCoarseDist)
                    {
                        bestCoarseDist = dist;
                        bestCluster = k;
                    }
                }

                // Quantize sub-vectors using SquaredL2 reconstruction fidelity so codes reconstruct vector accurately under all metrics
                byte[] codes = new byte[_numSubVectors];
                for (int m = 0; m < _numSubVectors; m++)
                {
                    ReadOnlySpan<float> subVec = workingVec.Slice(m * _subDim, _subDim);
                    int bestCentroid = 0;
                    float bestSubDist = float.MaxValue;

                    int codebookOffset = m * _numCentroids * _subDim;
                    for (int c = 0; c < _numCentroids; c++)
                    {
                        ReadOnlySpan<float> codeCentroid = _codebooks.AsSpan(codebookOffset + c * _subDim, _subDim);
                        float d = SimdDistance.SquaredL2(subVec, codeCentroid);
                        if (d < bestSubDist)
                        {
                            bestSubDist = d;
                            bestCentroid = c;
                        }
                    }

                    codes[m] = (byte)bestCentroid;
                }

                int vecIdx = _ids.Count;
                _ids.Add(id);
                _quantizedCodes.Add(codes);
                _coarseAssignments.Add(bestCluster);
                _invertedLists[bestCluster].Add(vecIdx);
            }
        }
        finally
        {
            if (rentedNorm != null)
            {
                ArrayPool<float>.Shared.Return(rentedNorm);
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

        if (topK <= 0)
        {
            return 0;
        }

        float[]? rentedQuery = null;
        int[]? rentedClusters = null;
        float[]? rentedClusterDists = null;
        float[]? rentedLut = null;

        try
        {
            ReadOnlySpan<float> workingQuery = query;
            if (_metric == DistanceMetric.Cosine)
            {
                rentedQuery = ArrayPool<float>.Shared.Rent(_dimension);
                Span<float> qSpan = rentedQuery.AsSpan(0, _dimension);
                query.CopyTo(qSpan);
                NormalizeInPlace(qSpan);
                workingQuery = qSpan;
            }

            Span<int> probedClusters = _numClusters <= 256
                ? stackalloc int[_numClusters]
                : (rentedClusters = ArrayPool<int>.Shared.Rent(_numClusters)).AsSpan(0, _numClusters);
            Span<float> clusterDists = _numClusters <= 256
                ? stackalloc float[_numClusters]
                : (rentedClusterDists = ArrayPool<float>.Shared.Rent(_numClusters)).AsSpan(0, _numClusters);

            int lutLen = _numSubVectors * _numCentroids;
            Span<float> lut = lutLen <= 4096
                ? stackalloc float[lutLen]
                : (rentedLut = ArrayPool<float>.Shared.Rent(lutLen)).AsSpan(0, lutLen);

            lock (_syncLock)
            {
                if (_ids.Count == 0)
                {
                    return 0;
                }

                for (int k = 0; k < _numClusters; k++)
                {
                    ReadOnlySpan<float> centroid = _coarseCentroids.AsSpan(k * _dimension, _dimension);
                    clusterDists[k] = _metric == DistanceMetric.DotProduct
                        ? -SimdDistance.DotProduct(workingQuery, centroid)
                        : SimdDistance.SquaredL2(workingQuery, centroid);
                    probedClusters[k] = k;
                }

                int probeCount = Math.Min(_nProbe, _numClusters);
                for (int i = 0; i < probeCount; i++)
                {
                    for (int j = i + 1; j < _numClusters; j++)
                    {
                        if (clusterDists[j] < clusterDists[i])
                        {
                            (clusterDists[i], clusterDists[j]) = (clusterDists[j], clusterDists[i]);
                            (probedClusters[i], probedClusters[j]) = (probedClusters[j], probedClusters[i]);
                        }
                    }
                }

                for (int m = 0; m < _numSubVectors; m++)
                {
                    ReadOnlySpan<float> subQuery = workingQuery.Slice(m * _subDim, _subDim);
                    int baseCodebookOffset = m * _numCentroids * _subDim;
                    int lutBase = m * _numCentroids;

                    for (int c = 0; c < _numCentroids; c++)
                    {
                        ReadOnlySpan<float> codeCentroid = _codebooks.AsSpan(baseCodebookOffset + c * _subDim, _subDim);
                        lut[lutBase + c] = _metric == DistanceMetric.DotProduct
                            ? -SimdDistance.DotProduct(subQuery, codeCentroid)
                            : (_metric == DistanceMetric.Cosine
                                ? 0.5f * SimdDistance.SquaredL2(subQuery, codeCentroid)
                                : SimdDistance.SquaredL2(subQuery, codeCentroid));
                    }
                }

                int maxResults = Math.Min(topK, Math.Min(outIds.Length, outDistances.Length));
                if (maxResults <= 0) return 0;

                int resultCount = 0;
                for (int p = 0; p < probeCount; p++)
                {
                    int clusterIdx = probedClusters[p];
                    List<int> invList = _invertedLists[clusterIdx];

                    for (int i = 0; i < invList.Count; i++)
                    {
                        int vecIdx = invList[i];
                        byte[] codes = _quantizedCodes[vecIdx];

                        float dist = 0f;
                        for (int m = 0; m < _numSubVectors; m++)
                        {
                            dist += lut[m * _numCentroids + codes[m]];
                        }

                        InsertTopK(_ids[vecIdx], dist, outIds, outDistances, ref resultCount, maxResults);
                    }
                }

                return resultCount;
            }
        }
        finally
        {
            if (rentedQuery != null) ArrayPool<float>.Shared.Return(rentedQuery);
            if (rentedClusters != null) ArrayPool<int>.Shared.Return(rentedClusters);
            if (rentedClusterDists != null) ArrayPool<float>.Shared.Return(rentedClusterDists);
            if (rentedLut != null) ArrayPool<float>.Shared.Return(rentedLut);
        }
    }

    private static void InsertTopK(long id, float dist, Span<long> outIds, Span<float> outDists, ref int count, int capacity)
    {
        if (count == capacity && dist >= outDists[count - 1])
        {
            return;
        }

        int pos = count < capacity ? count++ : capacity - 1;
        while (pos > 0 && dist < outDists[pos - 1])
        {
            outIds[pos] = outIds[pos - 1];
            outDists[pos] = outDists[pos - 1];
            pos--;
        }
        outIds[pos] = id;
        outDists[pos] = dist;
    }

    private static void NormalizeInPlace(Span<float> v)
    {
        float normSq = SimdDistance.DotProduct(v, v);
        if (normSq > 1e-12f)
        {
            float inv = 1.0f / MathF.Sqrt(normSq);
            for (int i = 0; i < v.Length; i++) v[i] *= inv;
        }
    }

    /// <inheritdoc />
    public void Save(Stream stream)
    {
        lock (_syncLock)
        {
            using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
            writer.Write(MagicHeader);
            writer.Write(1);
            writer.Write(_dimension);
            writer.Write(_numSubVectors);
            writer.Write(_subDim);
            writer.Write(_numCentroids);
            writer.Write(_numClusters);
            writer.Write((int)_metric);
            writer.Write(_nProbe);
            writer.Write(_isTrained);

            for (int i = 0; i < _codebooks.Length; i++) writer.Write(_codebooks[i]);
            for (int i = 0; i < _coarseCentroids.Length; i++) writer.Write(_coarseCentroids[i]);

            writer.Write(_ids.Count);
            for (int i = 0; i < _ids.Count; i++)
            {
                writer.Write(_ids[i]);
                writer.Write(_coarseAssignments[i]);
                writer.Write(_quantizedCodes[i]);
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
            throw new InvalidDataException("Invalid IVF-PQ file header magic.");
        }

        _ = reader.ReadInt32(); // version
        int dim = reader.ReadInt32();
        if (dim != _dimension)
        {
            throw new InvalidDataException($"Dimension mismatch: index is {_dimension}, stream is {dim}.");
        }

        int m = reader.ReadInt32();
        int subDim = reader.ReadInt32();
        int nCentroids = reader.ReadInt32();
        int nClusters = reader.ReadInt32();
        if (m != _numSubVectors || subDim != _subDim || nCentroids != _numCentroids || nClusters != _numClusters)
        {
            throw new InvalidDataException("IVF-PQ structure parameters do not match target index.");
        }

        lock (_syncLock)
        {
            _metric = (DistanceMetric)reader.ReadInt32();
            _nProbe = Math.Clamp(reader.ReadInt32(), 1, _numClusters);
            _isTrained = reader.ReadBoolean();

            for (int i = 0; i < _codebooks.Length; i++) _codebooks[i] = reader.ReadSingle();
            for (int i = 0; i < _coarseCentroids.Length; i++) _coarseCentroids[i] = reader.ReadSingle();

            int count = reader.ReadInt32();
            _ids.Clear();
            _quantizedCodes.Clear();
            _coarseAssignments.Clear();
            for (int k = 0; k < _numClusters; k++) _invertedLists[k].Clear();

            for (int i = 0; i < count; i++)
            {
                long id = reader.ReadInt64();
                int cluster = reader.ReadInt32();
                byte[] codes = reader.ReadBytes(_numSubVectors);

                _ids.Add(id);
                _coarseAssignments.Add(cluster);
                _quantizedCodes.Add(codes);
                if ((uint)cluster < (uint)_numClusters)
                {
                    _invertedLists[cluster].Add(i);
                }
            }
        }
    }

    public void Dispose()
    {
    }
}
