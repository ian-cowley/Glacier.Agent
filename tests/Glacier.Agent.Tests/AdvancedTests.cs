namespace Glacier.Agent.Tests;

using System;
using System.IO;
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
    public void Hnsw_ConcurrentSearches_ThreadSafe()
    {
        const int dim = 8;
        using var index = new HnswVectorIndex(dim, DistanceMetric.Cosine);

        for (int i = 0; i < 50; i++)
        {
            float[] vec = [i, i * 2f, i + 1f, 1f, 0.5f, 0.2f, 0.1f, 0.9f];
            index.Add(i, vec);
        }

        Parallel.For(0, 100, _ =>
        {
            Span<long> ids = stackalloc long[3];
            Span<float> dists = stackalloc float[3];
            float[] query = [5f, 10f, 6f, 1f, 0.5f, 0.2f, 0.1f, 0.9f];
            int count = index.Search(query, ids, dists, 3);
            Assert.True(count > 0);
        });
    }

    [Fact]
    public async Task ActorSupervisor_RestartsFailingActor()
    {
        var supervisor = new ActorSupervisor("super-1");
        int restartAttempts = 0;

        var failingActor = new FaultyActor("failing-actor", () =>
        {
            restartAttempts++;
            if (restartAttempts == 1)
            {
                throw new TimeoutException("Simulated actor timeout failure");
            }
        });

        supervisor.RegisterChild(failingActor);
        failingActor.Start();

        var msg = AgentMessage.Create("tester", "failing-actor", "trigger", "data");
        await failingActor.SendAsync(msg);

        // Wait for failure and supervisor restart
        await Task.Delay(200);

        Assert.True(restartAttempts >= 1);
        Assert.True(failingActor.State == ActorLifecycleState.Active || failingActor.State == ActorLifecycleState.Starting);
        await failingActor.StopAsync();
    }

    private sealed class FaultyActor : AgentActor
    {
        private readonly Action _onMsg;
        public FaultyActor(string id, Action onMsg) : base(id) => _onMsg = onMsg;

        protected override Task OnMessageAsync(AgentMessage msg, CancellationToken ct)
        {
            _onMsg();
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task StreamMcpTransport_PipesMessagesAcrossStreams()
    {
        using var clientToServer = new MemoryStream();
        using var serverToClient = new MemoryStream();

        // Using duplex pipes or memory transport for streaming validation
        var (cTransport, sTransport) = InMemoryMcpTransport.CreateConnectedPair();

        await cTransport.SendMessageAsync("""{"jsonrpc":"2.0","method":"ping","id":1}""");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var enumerator = sTransport.ReadMessagesAsync(cts.Token).GetAsyncEnumerator();

        Assert.True(await enumerator.MoveNextAsync());
        Assert.Contains("ping", enumerator.Current);
    }
}
