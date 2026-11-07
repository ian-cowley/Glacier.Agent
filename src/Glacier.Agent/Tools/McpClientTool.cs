using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Glacier.Agent.Mcp;

namespace Glacier.Agent.Tools;

public class McpClientTool : ITool
{
    private readonly McpClient _client;
    private readonly Glacier.Agent.Mcp.McpTool _mcpTool;

    public string Name => _mcpTool.Name;
    public string Description => _mcpTool.Description ?? string.Empty;

    public McpClientTool(McpClient client, Glacier.Agent.Mcp.McpTool mcpTool)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _mcpTool = mcpTool ?? throw new ArgumentNullException(nameof(mcpTool));
    }

    public JsonNode GetParametersSchema()
    {
        if (_mcpTool.InputSchema is JsonNode node)
        {
            return node.DeepClone();
        }

        if (_mcpTool.InputSchema is JsonElement element)
        {
            return JsonNode.Parse(element.GetRawText()) ?? new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() };
        }

        if (_mcpTool.InputSchema is string rawJson && !string.IsNullOrWhiteSpace(rawJson))
        {
            try
            {
                return JsonNode.Parse(rawJson) ?? new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() };
            }
            catch
            {
            }
        }

        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject()
        };
    }

    public async Task<string> ExecuteAsync(string arguments)
    {
        try
        {
            var result = await _client.CallToolAsync(Name, arguments).ConfigureAwait(false);

            if (result.IsError)
            {
                return new JsonObject
                {
                    ["status"] = "error",
                    ["message"] = "Tool reported an error."
                }.ToJsonString();
            }

            var outputs = new List<string>();
            foreach (var content in result.Content)
            {
                if (content.Text != null)
                {
                    outputs.Add(content.Text);
                }
            }

            return string.Join("\n", outputs);
        }
        catch (Exception ex)
        {
            return new JsonObject
            {
                ["status"] = "error",
                ["message"] = ex.Message
            }.ToJsonString();
        }
    }
}

public class McpTool : McpClientTool
{
    public McpTool(McpClient client, Glacier.Agent.Mcp.McpTool mcpTool) : base(client, mcpTool) { }
}
