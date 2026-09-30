namespace Glacier.Agent.Runtime;

using System;

/// <summary>
/// Immutable message passed between reactive agent actors.
/// </summary>
public sealed record AgentMessage(
    string Id,
    string Sender,
    string Target,
    string Type,
    string Payload,
    DateTimeOffset Timestamp)
{
    public static AgentMessage Create(string sender, string target, string type, string payload)
        => new(Guid.NewGuid().ToString("N"), sender, target, type, payload, DateTimeOffset.UtcNow);
}

/// <summary>
/// Lifecycle states for an agent actor.
/// </summary>
public enum ActorLifecycleState
{
    Uninitialized = 0,
    Starting = 1,
    Active = 2,
    Paused = 3,
    Stopped = 4,
    Faulted = 5
}

/// <summary>
/// Backpressure strategy for actor mailboxes.
/// </summary>
public enum MailboxBackpressureStrategy
{
    Wait = 0,
    DropOldest = 1,
    DropNewest = 2,
    Unbounded = 3
}

/// <summary>
/// Directives returned by supervisors when a child actor faults.
/// </summary>
public enum SupervisionDirective
{
    Resume = 0,
    Restart = 1,
    Stop = 2,
    Escalate = 3
}
