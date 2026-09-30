namespace Glacier.Agent.Mcp;

using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Adapts a remote MCP tool to the <see cref="IAgentTool"/> interface.
/// </summary>
public sealed class McpAgentToolAdapter : IAgentTool
{
    private readonly McpClient _client;
    private readonly McpTool _tool;

    public string Name => _tool.Name;
    public string Description => _tool.Description;
    public string ParameterJsonSchema => JsonSerializer.Serialize(_tool.InputSchema);

    public McpAgentToolAdapter(McpClient client, McpTool tool)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _tool = tool ?? throw new ArgumentNullException(nameof(tool));
    }

    public async ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        McpToolCallResult result = await _client.CallToolAsync(Name, argumentsJson, ct).ConfigureAwait(false);
        if (result.IsError)
        {
            string err = result.Content.Count > 0 ? (result.Content[0].Text ?? "Tool execution failed") : "Tool execution failed";
            throw new InvalidOperationException($"MCP Tool '{Name}' failed: {err}");
        }

        if (result.Content.Count > 0 && result.Content[0].Text != null)
        {
            return result.Content[0].Text!;
        }

        return "{}";
    }
}
