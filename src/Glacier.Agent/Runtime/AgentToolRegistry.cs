namespace Glacier.Agent.Runtime;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

/// <summary>
/// Thread-safe registry for agent tools with compile-time zero-reflection dispatch.
/// </summary>
public sealed class AgentToolRegistry
{
    private readonly ConcurrentDictionary<string, IAgentTool> _tools = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<IAgentTool> Tools => (IReadOnlyCollection<IAgentTool>)_tools.Values;

    public void Register(IAgentTool tool)
    {
        if (tool == null) throw new ArgumentNullException(nameof(tool));
        _tools[tool.Name] = tool;
    }

    public bool TryGetTool(string name, out IAgentTool? tool)
    {
        return _tools.TryGetValue(name, out tool);
    }

    public bool Remove(string name)
    {
        return _tools.TryRemove(name, out _);
    }

    public void Clear()
    {
        _tools.Clear();
    }
}
