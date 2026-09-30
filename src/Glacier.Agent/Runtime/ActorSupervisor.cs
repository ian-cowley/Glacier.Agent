namespace Glacier.Agent.Runtime;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;

public interface IActorSupervisor
{
    SupervisionDirective HandleFailure(IAgentActor actor, Exception exception);
    void RegisterChild(IAgentActor child);
    void UnregisterChild(string actorId);
}

/// <summary>
/// Supervisor hierarchy coordinator managing child actor lifecycles and fault isolation.
/// </summary>
public sealed class ActorSupervisor : IActorSupervisor
{
    private readonly ConcurrentDictionary<string, IAgentActor> _children = new(StringComparer.Ordinal);
    private readonly Func<IAgentActor, Exception, SupervisionDirective> _strategy;

    public string SupervisorId { get; }
    public IReadOnlyCollection<IAgentActor> Children => (IReadOnlyCollection<IAgentActor>)_children.Values;

    public ActorSupervisor(
        string supervisorId,
        Func<IAgentActor, Exception, SupervisionDirective>? strategy = null)
    {
        SupervisorId = supervisorId;
        _strategy = strategy ?? DefaultStrategy;
    }

    private static SupervisionDirective DefaultStrategy(IAgentActor actor, Exception ex)
    {
        return ex switch
        {
            ArgumentException => SupervisionDirective.Resume,
            TimeoutException => SupervisionDirective.Restart,
            InvalidOperationException => SupervisionDirective.Restart,
            _ => SupervisionDirective.Escalate
        };
    }

    public void RegisterChild(IAgentActor child)
    {
        child.Supervisor = this;
        _children[child.Id] = child;
    }

    public void UnregisterChild(string actorId)
    {
        if (_children.TryRemove(actorId, out IAgentActor? child))
        {
            child.Supervisor = null;
        }
    }

    public SupervisionDirective HandleFailure(IAgentActor actor, Exception exception)
    {
        SupervisionDirective directive = _strategy(actor, exception);

        if (directive == SupervisionDirective.Stop)
        {
            UnregisterChild(actor.Id);
        }

        return directive;
    }
}
