namespace Glacier.Agent.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
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

    [Fact]
    public async Task HnswVectorIndex_ConcurrentAddAndSearch_IsThreadSafe()
    {
        const int dim = 16;
        using var index = new HnswVectorIndex(dim, DistanceMetric.SquaredL2, m: 8, efConstruction: 32, efSearch: 24);

        for (int i = 0; i < 40; i++)
        {
            float[] v = new float[dim];
            v[i % dim] = 1.0f;
            index.Add(i, v);
        }

        var tasks = new List<Task>();
        for (int w = 0; w < 2; w++)
        {
            int workerId = w;
            tasks.Add(Task.Run(() =>
            {
                var rng = new Random(100 + workerId);
                float[] v = new float[dim];
                for (int i = 0; i < 80; i++)
                {
                    for (int d = 0; d < dim; d++) v[d] = (float)rng.NextDouble();
                    index.Add(1000 + workerId * 100 + i, v);
                }
            }));
        }

        for (int r = 0; r < 4; r++)
        {
            int readerId = r;
            tasks.Add(Task.Run(() =>
            {
                var rng = new Random(500 + readerId);
                float[] q = new float[dim];
                long[] ids = new long[5];
                float[] dists = new float[5];
                for (int i = 0; i < 120; i++)
                {
                    for (int d = 0; d < dim; d++) q[d] = (float)rng.NextDouble();
                    int found = index.Search(q, ids, dists, 5);
                    Assert.True(found > 0);
                }
            }));
        }

        await Task.WhenAll(tasks);
        Assert.Equal(200, index.Count);
    }

    [Fact]
    public void HnswVectorIndex_DuplicateIdOverwrite_RelinksToNewNeighborhood()
    {
      Span<long> singleIds = stackalloc long[1];
      Span<float> singleDists = stackalloc float[1];
      Span<long> ids = stackalloc long[2];
      Span<float> dists = stackalloc float[2];
      for (int iter = 0; iter < 50; iter++)
      {
        const int dim = 8;
        using var index = new HnswVectorIndex(dim, DistanceMetric.SquaredL2, m: 4, efConstruction: 16, efSearch: 16);

        float[] origin = new float[dim];
        index.Add(0, origin);

        // Overwrite when Count == 1
        float[] updatedOrigin = [1f, 0f, 0f, 0f, 0f, 0f, 0f, 0f];
        index.Add(0, updatedOrigin);
        Assert.Equal(1, index.Search(updatedOrigin, singleIds, singleDists, 1));
        Assert.Equal(0, singleIds[0]);
        Assert.True(singleDists[0] < 1e-5f);

        // Add cluster around 0..1 and another cluster far away
        for (int i = 1; i < 30; i++)
        {
            float[] v = new float[dim];
            v[0] = i * 0.05f;
            index.Add(i, v);
        }

        float[] farAnchor = [100f, 100f, 100f, 100f, 100f, 100f, 100f, 100f];
        index.Add(99, farAnchor);

        // Relink id=0 (entry/existing node) to the far cluster
        float[] movedZero = [100.1f, 100f, 100f, 100f, 100f, 100f, 100f, 100f];
        index.Add(0, movedZero);

        int count = index.Search(movedZero, ids, dists, 2);

        Assert.Equal(2, count);
        Assert.Equal(0, ids[0]);
        Assert.Equal(99, ids[1]);
      }
    }

    [Fact]
    public void HnswVectorIndex_Load_RejectsInvalidHeadersAndBounds()
    {
        using var index = new HnswVectorIndex(4);

        using var badMagic = new MemoryStream(BitConverter.GetBytes(0x12345678));
        Assert.Throws<InvalidDataException>(() => index.Load(badMagic));

        using var badCount = new MemoryStream();
        using (var bw = new BinaryWriter(badCount, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            bw.Write(0x484E5357); // magic
            bw.Write(1);          // version
            bw.Write(4);          // dim
            bw.Write(0);          // metric
            bw.Write(8);          // m
            bw.Write(16);         // m0
            bw.Write(32);         // efConstruction
            bw.Write(16);         // efSearch
            bw.Write(-1);         // entryPoint
            bw.Write(-1);         // maxLayer
            bw.Write(-5);         // negative count
        }
        badCount.Position = 0;
        Assert.Throws<InvalidDataException>(() => index.Load(badCount));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(33)]
    [InlineData(65)]
    public void SimdDistance_CosineDistance_MatchesScalarReference(int length)
    {
        var rng = new Random(42 + length);
        float[] a = new float[length];
        float[] b = new float[length];
        double dot = 0, normA = 0, normB = 0;
        for (int i = 0; i < length; i++)
        {
            a[i] = (float)(rng.NextDouble() * 2.0 - 1.0);
            b[i] = (float)(rng.NextDouble() * 2.0 - 1.0);
            dot += (double)a[i] * b[i];
            normA += (double)a[i] * a[i];
            normB += (double)b[i] * b[i];
        }

        float expected = 1.0f - Math.Clamp((float)(dot / Math.Sqrt(normA * normB)), -1.0f, 1.0f);
        float actual = SimdDistance.CosineDistance(a, b);
        Assert.Equal(expected, actual, precision: 4);

        float[] zero = new float[length];
        Assert.Equal(1.0f, SimdDistance.CosineDistance(zero, b));
    }

    [Fact]
    public void HnswVectorIndex_SearchLayerCore_EfSearch2CompleteGraphMatchesEfSearch64()
    {
        Span<long> ids2 = stackalloc long[2];
        Span<float> dists2 = stackalloc float[2];
        Span<long> ids64 = stackalloc long[2];
        Span<float> dists64 = stackalloc float[2];

        for (int seed = 1; seed <= 50; seed++)
        {
            var rng = new Random(seed);
            using var index = new HnswVectorIndex(1, DistanceMetric.SquaredL2, m: 16, efConstruction: 64, efSearch: 2);
            for (int i = 0; i < 10; i++)
            {
                index.Add(i, [(float)(rng.NextDouble() * 100.0)]);
            }

            float[] query = [(float)(rng.NextDouble() * 100.0)];

            index.EfSearch = 2;
            int found2 = index.Search(query, ids2, dists2, topK: 2);

            index.EfSearch = 64;
            int found64 = index.Search(query, ids64, dists64, topK: 2);

            Assert.Equal(2, found2);
            Assert.Equal(2, found64);
            Assert.True(dists2[0] <= dists64[0] + 1e-6f, $"Seed {seed}: efSearch=2 returned worse 1st dist {dists2[0]} vs {dists64[0]}");
            Assert.True(dists2[1] <= dists64[1] + 1e-6f, $"Seed {seed}: efSearch=2 returned worse 2nd dist {dists2[1]} vs {dists64[1]}");
        }
    }

    [Fact]
    public void HnswVectorIndex_Load_RejectsPositiveCountWithInvalidEntryPoint_AndTruncatedStream()
    {
        using var index = new HnswVectorIndex(2, DistanceMetric.SquaredL2, m: 4, efConstruction: 16, efSearch: 8);
        index.Add(42, [1.0f, 2.0f]);

        using var saved = new MemoryStream();
        index.Save(saved);
        byte[] bytes = saved.ToArray();

        // Header layout: magic(0), version(4), dim(8), metric(12), m(16), m0(20), efConstruction(24), efSearch(28), entryPoint(32)
        byte[] negEntryBytes = (byte[])bytes.Clone();
        BitConverter.TryWriteBytes(negEntryBytes.AsSpan(32, 4), -1);
        using var target1 = new HnswVectorIndex(2);
        Assert.Throws<InvalidDataException>(() => target1.Load(new MemoryStream(negEntryBytes)));

        byte[] oobEntryBytes = (byte[])bytes.Clone();
        BitConverter.TryWriteBytes(oobEntryBytes.AsSpan(32, 4), 1);
        using var target2 = new HnswVectorIndex(2);
        Assert.Throws<InvalidDataException>(() => target2.Load(new MemoryStream(oobEntryBytes)));

        using var truncated = new MemoryStream(bytes, 0, bytes.Length / 2);
        using var target3 = new HnswVectorIndex(2);
        Assert.Throws<EndOfStreamException>(() => target3.Load(truncated));
    }

    [Fact]
    public void SimdDistance_CosineDistance_LargeFiniteNorms_DoesNotOverflow()
    {
        Assert.Equal(0.0f, SimdDistance.CosineDistance([1e12f, 1e12f], [1e12f, 1e12f]), precision: 5);
        Assert.Equal(1.0f, SimdDistance.CosineDistance([1e12f, 0f], [0f, 1e12f]), precision: 5);
        Assert.Equal(2.0f, SimdDistance.CosineDistance([1e12f, 0f], [-1e12f, 0f]), precision: 5);

        float[] bigA = new float[33];
        float[] bigB = new float[33];
        Array.Fill(bigA, 1e12f);
        Array.Fill(bigB, 1e12f);
        Assert.Equal(0.0f, SimdDistance.CosineDistance(bigA, bigB), precision: 5);
    }
}
