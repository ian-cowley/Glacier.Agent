namespace Glacier.Agent.Runtime;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Multi-agent swarm coordinator orchestrating reactive actor swarms,
/// message routing, and collaborative execution pipelines.
/// </summary>
public sealed class MultiAgentSwarm : IDisposable
{
    private readonly ConcurrentDictionary<string, IAgentActor> _actors = new(StringComparer.Ordinal);
    private readonly ActorSupervisor _supervisor;

    public string SwarmId { get; }
    public IReadOnlyCollection<IAgentActor> Actors => (IReadOnlyCollection<IAgentActor>)_actors.Values;

    public MultiAgentSwarm(string swarmId)
    {
        SwarmId = swarmId;
        _supervisor = new ActorSupervisor($"supervisor-{swarmId}");
    }

    public void RegisterActor(IAgentActor actor)
    {
        _actors[actor.Id] = actor;
        _supervisor.RegisterChild(actor);
        actor.Start();
    }

    public bool TryGetActor(string actorId, out IAgentActor? actor)
    {
        return _actors.TryGetValue(actorId, out actor);
    }

    public async ValueTask SendToAsync(string targetActorId, AgentMessage message, CancellationToken ct = default)
    {
        if (_actors.TryGetValue(targetActorId, out IAgentActor? actor))
        {
            await actor.SendAsync(message, ct).ConfigureAwait(false);
        }
        else
        {
            throw new KeyNotFoundException($"Actor '{targetActorId}' is not registered in swarm '{SwarmId}'.");
        }
    }

    public async ValueTask BroadcastAsync(AgentMessage message, CancellationToken ct = default)
    {
        foreach (var (_, actor) in _actors)
        {
            await actor.SendAsync(message, ct).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        foreach (var (_, actor) in _actors)
        {
            actor.Dispose();
        }
        _actors.Clear();
    }
}
