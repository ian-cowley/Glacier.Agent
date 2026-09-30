namespace Glacier.Agent.Benchmarks;

using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Glacier.Agent.Runtime;

[MemoryDiagnoser]
public class ActorRuntimeBenchmark
{
    private AgentActor _actor = null!;
    private AgentMessage _msg = null!;

    [GlobalSetup]
    public void Setup()
    {
        _actor = new AgentActor("bench-actor", mailboxCapacity: 100_000);
        _actor.Start();
        _msg = AgentMessage.Create("sender", "bench-actor", "benchmark", "payload");
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _actor.Dispose();
    }

    [Benchmark]
    public bool EnqueueActorMessage()
    {
        return _actor.TrySend(_msg);
    }
}
