namespace Glacier.Agent.Benchmarks;

using System;
using System.Diagnostics;
using System.Text;
using BenchmarkDotNet.Running;
using Glacier.Agent.CommandLine;
using Glacier.Agent.Runtime;
using Glacier.Agent.Tokenizer;
using Glacier.Agent.VectorIdx;

public static class Program
{
    public static void Main(string[] args)
    {
        if (args.Length > 0 && args[0].Equals("--bdn", StringComparison.OrdinalIgnoreCase))
        {
            BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
            return;
        }

        Console.WriteLine("================================================================================");
        Console.WriteLine("           GLACIER.AGENT (PILLAR 14) PERFORMANCE VERIFICATION SUITE              ");
        Console.WriteLine("================================================================================");

        RunTokenizerMicrobenchmark();
        RunCommandLineMicrobenchmark();
        RunVectorIndexMicrobenchmark();
        RunActorRuntimeMicrobenchmark();

        Console.WriteLine("================================================================================");
        Console.WriteLine("           ALL PERFORMANCE CHECKS COMPLETED SUCCESSFULLY                        ");
        Console.WriteLine("================================================================================");
    }

    private static void RunTokenizerMicrobenchmark()
    {
        Console.WriteLine("\n[1] SIMD Radix Trie Tokenizer Throughput:");
        var tokenizer = SimdRadixTrieTokenizer.CreateDefault();
        string sampleText = """
        public sealed class AgentExecutionEngine : IAgentRuntime
        {
            public async ValueTask<AgentResponse> ExecuteStepAsync(AgentContext context, CancellationToken ct)
            {
                var response = await ProcessAgentStepAsync(context, ct);
                return response;
            }
        }
        """;
        byte[] bytes = Encoding.UTF8.GetBytes(sampleText);
        Span<int> buffer = stackalloc int[1024];

        // Warmup
        for (int i = 0; i < 50_000; i++)
        {
            tokenizer.Encode(bytes, buffer);
        }

        int tokensPerIter = tokenizer.Encode(bytes, buffer);
        const int iterations = 1_000_000;
        long totalTokens = (long)tokensPerIter * iterations;

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
        {
            tokenizer.Encode(bytes, buffer);
        }
        sw.Stop();

        double tokensPerSec = totalTokens / sw.Elapsed.TotalSeconds;
        Console.WriteLine($"    Tokens/Iteration: {tokensPerIter}");
        Console.WriteLine($"    Iterations:       {iterations:N0}");
        Console.WriteLine($"    Total Tokens:     {totalTokens:N0}");
        Console.WriteLine($"    Elapsed Time:     {sw.ElapsedMilliseconds} ms");
        Console.WriteLine($"    Throughput:       {tokensPerSec:N0} tokens/sec");
    }

    private static void RunCommandLineMicrobenchmark()
    {
        Console.WriteLine("\n[2] Branchless CLI Cold Startup & Dispatch:");
        string[] args = ["serve", "--model", "gpt-4", "-p", "8080", "-v", "config.json"];
        var app = new CliApp("glacier", "Glacier CLI");
        app.Output = System.IO.TextWriter.Null;
        app.Error = System.IO.TextWriter.Null;

        app.AddCommand(new CliCommand("serve")
            .AddOption("model", "m", "Model identifier", hasValue: true)
            .AddOption("port", "p", "Port", hasValue: true)
            .AddFlag("verbose", "v", "Verbose")
            .AddArgument("config", "Config path")
            .SetHandler(ctx => 0));

        // Cold invocation
        var coldSw = Stopwatch.StartNew();
        app.Run(args);
        coldSw.Stop();
        Console.WriteLine($"    Cold invocation:  {coldSw.Elapsed.TotalMilliseconds:F3} ms");

        // Warm throughput
        const int iters = 200_000;
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < iters; i++)
        {
            app.Run(args);
        }
        sw.Stop();
        double opsPerSec = iters / sw.Elapsed.TotalSeconds;
        Console.WriteLine($"    Warm Dispatch:    {opsPerSec:N0} dispatches/sec");
    }

    private static void RunVectorIndexMicrobenchmark()
    {
        Console.WriteLine("\n[3] In-Process Vector Search (HNSW & IVF-PQ):");
        const int dim = 128;
        const int n = 5_000;
        var rand = new Random(42);

        var hnsw = new HnswVectorIndex(dim, DistanceMetric.SquaredL2, m: 16, efConstruction: 64, efSearch: 32);
        var ivf = new IvfPqVectorIndex(dim, numSubVectors: 8, numClusters: 16);

        float[] query = new float[dim];
        for (int d = 0; d < dim; d++) query[d] = (float)rand.NextDouble();

        for (int i = 0; i < n; i++)
        {
            float[] v = new float[dim];
            for (int d = 0; d < dim; d++) v[d] = (float)rand.NextDouble();
            hnsw.Add(i, v);
            ivf.Add(i, v);
        }

        Span<long> outIds = stackalloc long[10];
        Span<float> outDists = stackalloc float[10];

        const int queries = 10_000;
        var swHnsw = Stopwatch.StartNew();
        for (int i = 0; i < queries; i++)
        {
            hnsw.Search(query, outIds, outDists, 10);
        }
        swHnsw.Stop();
        double hnswQps = queries / swHnsw.Elapsed.TotalSeconds;
        Console.WriteLine($"    HNSW QPS ({n:N0} vectors):   {hnswQps:N0} queries/sec (Latency: {swHnsw.Elapsed.TotalMilliseconds / queries * 1000:F1} µs/query)");

        var swIvf = Stopwatch.StartNew();
        for (int i = 0; i < queries; i++)
        {
            ivf.Search(query, outIds, outDists, 10);
        }
        swIvf.Stop();
        double ivfQps = queries / swIvf.Elapsed.TotalSeconds;
        Console.WriteLine($"    IVF-PQ QPS ({n:N0} vectors): {ivfQps:N0} queries/sec (Latency: {swIvf.Elapsed.TotalMilliseconds / queries * 1000:F1} µs/query)");
    }

    private static void RunActorRuntimeMicrobenchmark()
    {
        Console.WriteLine("\n[4] Reactive Actor Event Bus & Mailbox Throughput:");
        using var actor = new AgentActor("bench-actor", mailboxCapacity: 500_000, strategy: MailboxBackpressureStrategy.Wait);
        actor.Start();

        var msg = AgentMessage.Create("sender", "bench-actor", "ping", "test payload");

        const int iters = 500_000;
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < iters; i++)
        {
            actor.TrySend(msg);
        }
        sw.Stop();

        double msgPerSec = iters / sw.Elapsed.TotalSeconds;
        Console.WriteLine($"    Mailbox Enqueue:  {msgPerSec:N0} msgs/sec");
    }
}
