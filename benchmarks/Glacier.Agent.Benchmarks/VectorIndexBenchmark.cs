namespace Glacier.Agent.Benchmarks;

using System;
using BenchmarkDotNet.Attributes;
using Glacier.Agent.VectorIdx;

[MemoryDiagnoser]
public class VectorIndexBenchmark
{
    private const int Dimension = 128;
    private float[] _vecA = null!;
    private float[] _vecB = null!;
    private HnswVectorIndex _hnsw = null!;
    private IvfPqVectorIndex _ivfPq = null!;
    private long[] _outIds = null!;
    private float[] _outDists = null!;

    [GlobalSetup]
    public void Setup()
    {
        var rand = new Random(42);
        _vecA = new float[Dimension];
        _vecB = new float[Dimension];
        for (int i = 0; i < Dimension; i++)
        {
            _vecA[i] = (float)rand.NextDouble();
            _vecB[i] = (float)rand.NextDouble();
        }

        _hnsw = new HnswVectorIndex(Dimension, DistanceMetric.SquaredL2, m: 16, efConstruction: 64, efSearch: 32);
        _ivfPq = new IvfPqVectorIndex(Dimension, numSubVectors: 8, numClusters: 16);

        // Populate indices with 1,000 vectors
        for (int i = 0; i < 1_000; i++)
        {
            float[] vec = new float[Dimension];
            for (int d = 0; d < Dimension; d++) vec[d] = (float)rand.NextDouble();
            _hnsw.Add(i, vec);
            _ivfPq.Add(i, vec);
        }

        _outIds = new long[10];
        _outDists = new float[10];
    }

    [Benchmark]
    public float Distance_SimdSquaredL2()
    {
        return SimdDistance.SquaredL2(_vecA, _vecB);
    }

    [Benchmark]
    public float Distance_SimdCosine()
    {
        return SimdDistance.CosineDistance(_vecA, _vecB);
    }

    [Benchmark]
    public int Hnsw_SearchTop10()
    {
        return _hnsw.Search(_vecA, _outIds, _outDists, 10);
    }

    [Benchmark]
    public int IvfPq_SearchTop10()
    {
        return _ivfPq.Search(_vecA, _outIds, _outDists, 10);
    }
}
