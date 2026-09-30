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

        // Squared L2 = (1-2)^2 + (2-3)^2 + (3-4)^2 + (4-5)^2 = 1 + 1 + 1 + 1 = 4.0
        float l2 = SimdDistance.SquaredL2(a, b);
        Assert.Equal(4.0f, l2, precision: 5);

        // Dot product = 1*2 + 2*3 + 3*4 + 4*5 = 2 + 6 + 12 + 20 = 40.0
        float dot = SimdDistance.DotProduct(a, b);
        Assert.Equal(40.0f, dot, precision: 5);

        // Self cosine distance = 0.0
        float cos = SimdDistance.CosineDistance(a, a);
        Assert.Equal(0.0f, cos, precision: 5);
    }

    [Fact]
    public void HnswVectorIndex_RecallAndRoundtripPersistence()
    {
        const int dim = 16;
        using var index = new HnswVectorIndex(dim, DistanceMetric.SquaredL2, m: 8, efConstruction: 32, efSearch: 32);

        // Insert 100 vectors
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

        // Search for exact vector #15
        Span<long> outIds = stackalloc long[5];
        Span<float> outDistances = stackalloc float[5];
        int found = index.Search(testVectors[15], outIds, outDistances, topK: 5);

        Assert.True(found >= 1);
        Assert.Equal(1015, outIds[0]); // ID = 15 + 1000
        Assert.True(outDistances[0] < 1e-4f);

        // Roundtrip Save & Load
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
    public void IvfPqVectorIndex_RecallAndRoundtripPersistence()
    {
        const int dim = 16;
        using var index = new IvfPqVectorIndex(dim, numSubVectors: 4, numClusters: 4, DistanceMetric.SquaredL2);

        var rand = new Random(99);
        float[][] testVectors = new float[50][];
        for (int i = 0; i < 50; i++)
        {
            testVectors[i] = new float[dim];
            for (int d = 0; d < dim; d++)
            {
                testVectors[i][d] = (float)rand.NextDouble();
            }
            index.Add(i + 500, testVectors[i]);
        }

        Assert.Equal(50, index.Count);

        Span<long> outIds = stackalloc long[3];
        Span<float> outDistances = stackalloc float[3];
        int found = index.Search(testVectors[10], outIds, outDistances, topK: 3);

        Assert.True(found > 0);

        // Test serialization
        using var ms = new MemoryStream();
        index.Save(ms);

        ms.Position = 0;
        using var loaded = new IvfPqVectorIndex(dim, numSubVectors: 4, numClusters: 4);
        loaded.Load(ms);

        Assert.Equal(50, loaded.Count);

        Span<long> loadedIds = stackalloc long[3];
        Span<float> loadedDists = stackalloc float[3];
        int loadedFound = loaded.Search(testVectors[10], loadedIds, loadedDists, topK: 3);

        Assert.True(loadedFound > 0);
        Assert.Equal(outIds[0], loadedIds[0]);
    }
}
