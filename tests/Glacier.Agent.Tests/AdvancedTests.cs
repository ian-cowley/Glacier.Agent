namespace Glacier.Agent.Tests;

using System;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Glacier.Agent.CommandLine;
using Glacier.Agent.Mcp;
using Glacier.Agent.Runtime;
using Glacier.Agent.Tokenizer;
using Glacier.Agent.VectorIdx;
using Xunit;

public sealed class AdvancedTests
{
    [Fact]
    public void Tokenizer_HandlesEmptyAndWhitespace()
    {
        var tokenizer = SimdRadixTrieTokenizer.CreateDefault();
        Span<int> buffer = stackalloc int[10];

        int emptyCount = tokenizer.Encode(ReadOnlySpan<byte>.Empty, buffer);
        Assert.Equal(0, emptyCount);

        byte[] spaces = Encoding.UTF8.GetBytes("       ");
        int spaceCount = tokenizer.Encode(spaces, buffer);
        Assert.True(spaceCount > 0);

        Span<byte> decoded = stackalloc byte[32];
        int written = tokenizer.Decode(buffer.Slice(0, spaceCount), decoded);
        Assert.Equal("       ", Encoding.UTF8.GetString(decoded.Slice(0, written)));
    }

    [Fact]
    public void CliApp_HandlesSubcommandAliasesAndDefaults()
    {
        var app = new CliApp("glacier");
        int runs = 0;
        int timeout = 0;

        app.AddCommand(new CliCommand("eval")
            .AddOption("timeout", "t", "Timeout seconds", hasValue: true, defaultValue: "30")
            .SetHandler(ctx =>
            {
                runs++;
                timeout = ctx.GetOptionInt("timeout");
                return 0;
            }));

        app.Run(["eval", "-t", "60"]);
        Assert.Equal(1, runs);
        Assert.Equal(60, timeout);

        app.Run(["eval"]);
        Assert.Equal(2, runs);
        Assert.Equal(30, timeout);
    }

    [Fact]
    public void Hnsw_ConcurrentInsertsAndSearches_ThreadSafe()
    {
        const int dim = 8;
        using var index = new HnswVectorIndex(dim, DistanceMetric.Cosine);

        Parallel.For(0, 64, i =>
        {
            float[] vec = [i + 1f, (i + 1) * 2f, i + 2f, 1f, 0.5f, 0.2f, 0.1f, 0.9f];
            index.Add(i, vec);

            Span<long> ids = stackalloc long[3];
            Span<float> dists = stackalloc float[3];
            float[] query = [5f, 10f, 6f, 1f, 0.5f, 0.2f, 0.1f, 0.9f];
            int count = index.Search(query, ids, dists, 3);
            Assert.True(count >= 0);
        });

        Assert.Equal(64, index.Count);
    }

    [Fact]
    public async Task ActorSupervisor_RestartsFailingActor()
    {
        var supervisor = new ActorSupervisor("super-1");
        int restartAttempts = 0;
        var restartedSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var failingActor = new FaultyActor(
            "failing-actor",
            onMsg: () =>
            {
                if (Interlocked.Increment(ref restartAttempts) == 1)
                {
                    throw new TimeoutException("Simulated actor timeout failure");
                }
            },
            onRestart: () => restartedSignal.TrySetResult(true));

        supervisor.RegisterChild(failingActor);
        failingActor.Start();

        var msg = AgentMessage.Create("tester", "failing-actor", "trigger", "data");
        await failingActor.SendAsync(msg);

        await restartedSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(restartAttempts >= 1);
        Assert.True(failingActor.State == ActorLifecycleState.Active || failingActor.State == ActorLifecycleState.Starting);
        await failingActor.StopAsync();
    }

    private sealed class FaultyActor : AgentActor
    {
        private readonly Action _onMsg;
        private readonly Action _onRestart;

        public FaultyActor(string id, Action onMsg, Action onRestart) : base(id)
        {
            _onMsg = onMsg;
            _onRestart = onRestart;
        }

        protected override Task OnMessageAsync(AgentMessage msg, CancellationToken ct)
        {
            _onMsg();
            return Task.CompletedTask;
        }

        protected override Task OnRestartAsync()
        {
            _onRestart();
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task StreamMcpTransport_PipesConcurrentMessagesAcrossStreams()
    {
        using var pipeOut = new AnonymousPipeServerStream(PipeDirection.Out);
        using var pipeIn = new AnonymousPipeClientStream(PipeDirection.In, pipeOut.ClientSafePipeHandle);

        await using var writerTransport = new StreamMcpTransport(Stream.Null, pipeOut);
        await using var readerTransport = new StreamMcpTransport(pipeIn, Stream.Null);

        Task[] writes = new Task[8];
        for (int i = 0; i < writes.Length; i++)
        {
            int idx = i;
            writes[i] = writerTransport.SendMessageAsync($"{{\"jsonrpc\":\"2.0\",\"method\":\"ping\",\"id\":{idx}}}").AsTask();
        }
        await Task.WhenAll(writes);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var enumerator = readerTransport.ReadMessagesAsync(cts.Token).GetAsyncEnumerator();

        for (int i = 0; i < writes.Length; i++)
        {
            Assert.True(await enumerator.MoveNextAsync());
            Assert.Contains("ping", enumerator.Current);
        }
    }
}
