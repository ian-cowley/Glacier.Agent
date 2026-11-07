namespace Glacier.Agent.Sessions;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;

/// <summary>
/// Thread-safe in-memory session provider storing agent interaction histories in pure C# data structures.
/// Zero native dependencies and zero unmanaged allocations.
/// </summary>
public class MemorySessionProvider : ISessionProvider
{
    private readonly ConcurrentDictionary<string, List<LlmContent>> _sessions = new();

    public Task<List<LlmContent>> GetHistoryAsync(string sessionId)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        if (_sessions.TryGetValue(sessionId, out var history))
        {
            lock (history)
            {
                return Task.FromResult(new List<LlmContent>(history));
            }
        }
        return Task.FromResult(new List<LlmContent>());
    }

    public Task SaveMessageAsync(string sessionId, LlmContent message)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        ArgumentNullException.ThrowIfNull(message);

        var history = _sessions.GetOrAdd(sessionId, _ => new List<LlmContent>());
        lock (history)
        {
            history.Add(message);
        }
        return Task.CompletedTask;
    }

    public void ClearSession(string sessionId)
    {
        _sessions.TryRemove(sessionId, out _);
    }

    public void ClearAll()
    {
        _sessions.Clear();
    }
}
