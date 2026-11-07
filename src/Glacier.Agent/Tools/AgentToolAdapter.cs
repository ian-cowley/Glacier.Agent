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
