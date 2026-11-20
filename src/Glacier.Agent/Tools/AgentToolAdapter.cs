namespace Glacier.Agent.Tools;

using System;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Glacier.Agent;

/// <summary>
/// Bidirectional adapter between Glacier.Agent.IAgentTool and Glacier.Agent.Tools.ITool.
/// </summary>
public sealed class AgentToolAdapter : ITool
{
    private readonly IAgentTool _inner;

    public string Name => _inner.Name;
    public string Description => _inner.Description;

    public AgentToolAdapter(IAgentTool inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public JsonNode GetParametersSchema()
    {
        try
        {
            return JsonNode.Parse(_inner.ParameterJsonSchema)
                ?? new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() };
        }
        catch
        {
            return new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() };
        }
    }

    public async Task<string> ExecuteAsync(string arguments)
    {
        return await _inner.ExecuteAsync(arguments, CancellationToken.None).ConfigureAwait(false);
    }
}

/// <summary>
/// Adapts a <see cref="ITool"/> into an <see cref="IAgentTool"/> for <see cref="Glacier.Agent.Runtime.AgentExecutionEngine"/>.
/// </summary>
public sealed class ToolToAgentToolAdapter : IAgentTool
{
    private readonly ITool _inner;

    public string Name => _inner.Name;
    public string Description => _inner.Description;
    public string ParameterJsonSchema => _inner.GetParametersSchema().ToJsonString();

    public ToolToAgentToolAdapter(ITool inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public async ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return await _inner.ExecuteAsync(argumentsJson).ConfigureAwait(false);
    }
}
