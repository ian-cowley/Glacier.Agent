namespace Glacier.Agent.Tests;

using System;
using System.IO;
using Glacier.Agent.VectorIdx;
using Xunit;

public sealed class VectorIndexTests
{
    [Fact]
    public void SimdDistance_ComputesCorrectMetrics()
    {
        float[] a = [1f, 2f, 3f, 4f];
        float[] b = [2f, 3f, 4f, 5f];

        float l2 = SimdDistance.SquaredL2(a, b);
        Assert.Equal(4.0f, l2, precision: 5);

        float dot = SimdDistance.DotProduct(a, b);
        Assert.Equal(40.0f, dot, precision: 5);

        float cos = SimdDistance.CosineDistance(a, a);
        Assert.Equal(0.0f, cos, precision: 5);
    }

    [Fact]
    public void HnswVectorIndex_RecallAndRoundtripPersistence()
    {
        const int dim = 16;
        using var index = new HnswVectorIndex(dim, DistanceMetric.SquaredL2, m: 8, efConstruction: 32, efSearch: 32);

        var rand = new Random(42);
        float[][] testVectors = new float[100][];
        for (int i = 0; i < 100; i++)
        {
            testVectors[i] = new float[dim];
            for (int d = 0; d < dim; d++)
            {
                testVectors[i][d] = (float)rand.NextDouble();
            }
            index.Add(i + 1000, testVectors[i]);
        }

        Assert.Equal(100, index.Count);

        Span<long> outIds = stackalloc long[5];
        Span<float> outDistances = stackalloc float[5];
        int found = index.Search(testVectors[15], outIds, outDistances, topK: 5);

        Assert.True(found >= 1);
        Assert.Equal(1015, outIds[0]);
        Assert.True(outDistances[0] < 1e-4f);

        using var ms = new MemoryStream();
        index.Save(ms);

        ms.Position = 0;
        using var loadedIndex = new HnswVectorIndex(dim, DistanceMetric.SquaredL2);
        loadedIndex.Load(ms);

        Assert.Equal(100, loadedIndex.Count);

        Span<long> loadedIds = stackalloc long[5];
        Span<float> loadedDistances = stackalloc float[5];
        int loadedFound = loadedIndex.Search(testVectors[15], loadedIds, loadedDistances, topK: 5);

        Assert.True(loadedFound >= 1);
        Assert.Equal(1015, loadedIds[0]);
    }

    [Fact]
    public void IvfPqVectorIndex_TrainRecallAndRoundtripPersistence()
    {
        const int dim = 16;
        using var index = new IvfPqVectorIndex(dim, numSubVectors: 4, numClusters: 8, DistanceMetric.SquaredL2);

        var rand = new Random(99);
        float[][] testVectors = new float[60][];
        for (int i = 0; i < 60; i++)
        {
            testVectors[i] = new float[dim];
            for (int d = 0; d < dim; d++)
            {
                testVectors[i][d] = (float)rand.NextDouble() + (i % 4) * 5f;
            }
        }

        index.Train(testVectors, maxIterations: 10);
        Assert.True(index.IsTrained);

        for (int i = 0; i < 60; i++)
        {
            index.Add(i + 500, testVectors[i]);
        }

        Assert.Equal(60, index.Count);

        Span<long> outIds = stackalloc long[3];
        Span<float> outDistances = stackalloc float[3];
        int found = index.Search(testVectors[10], outIds, outDistances, topK: 3);

        Assert.True(found > 0);
        Assert.Equal(510, outIds[0]);

        using var ms = new MemoryStream();
        index.Save(ms);

        ms.Position = 0;
        using var loaded = new IvfPqVectorIndex(dim, numSubVectors: 4, numClusters: 8);
        loaded.Load(ms);

        Assert.Equal(60, loaded.Count);

        Span<long> loadedIds = stackalloc long[3];
        Span<float> loadedDists = stackalloc float[3];
        int loadedFound = loaded.Search(testVectors[10], loadedIds, loadedDists, topK: 3);

        Assert.True(loadedFound > 0);
        Assert.Equal(outIds[0], loadedIds[0]);
    }
}
