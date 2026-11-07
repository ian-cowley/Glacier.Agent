using System;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace Glacier.Agent.Tools;

/// <summary>
/// Wraps an IAgent as an ITool, enabling delegation (Manager -> Worker).
/// </summary>
public class DelegationTool : ITool
{
    private readonly IAgent _agent;
    private readonly ILlmService _llmService;

    public string Name { get; }
    public string Description { get; }

    public DelegationTool(IAgent agent, ILlmService llmService, string? name = null, string? description = null)
    {
        _agent = agent ?? throw new ArgumentNullException(nameof(agent));
        _llmService = llmService ?? throw new ArgumentNullException(nameof(llmService));
        Name = string.IsNullOrWhiteSpace(name) ? _agent.Name : name;
        Description = string.IsNullOrWhiteSpace(description) ? _agent.Description : description;
    }

    public JsonNode GetParametersSchema()
    {
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["prompt"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "The specific task or query to delegate to this specialized agent."
                },
                ["sessionId"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "Optional session ID to maintain worker memory."
                }
            },
            ["required"] = new JsonArray("prompt")
        };
    }

    public async Task<string> ExecuteAsync(string arguments)
    {
        try
        {
            var args = JsonNode.Parse(arguments);
            var prompt = args?["prompt"]?.ToString() ?? string.Empty;
            var sessionId = args?["sessionId"]?.ToString();

            if (string.IsNullOrWhiteSpace(prompt))
            {
                return new JsonObject
                {
                    ["status"] = "error",
                    ["message"] = "No prompt provided for delegation."
                }.ToJsonString();
            }

            return await _agent.RunAsync(prompt, _llmService, sessionId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new JsonObject
            {
                ["status"] = "error",
                ["message"] = $"Delegation failed: {ex.Message}"
            }.ToJsonString();
        }
    }
}
