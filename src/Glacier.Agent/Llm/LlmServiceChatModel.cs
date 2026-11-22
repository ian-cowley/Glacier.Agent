namespace Glacier.Agent.Llm;

using System;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Glacier.Agent.Runtime;

/// <summary>
/// NativeAOT-compatible <see cref="IAgentChatModel"/> adapter bridging an <see cref="ILlmService"/>
/// (such as <see cref="OpenAiService"/>) and a target model identifier into <see cref="AgentExecutionEngine"/>.
/// </summary>
public sealed class LlmServiceChatModel : IAgentChatModel
{
    private readonly ILlmService _llmService;
    private readonly string _modelId;
    private readonly float? _temperature;
    private readonly int? _maxTokens;

    public ILlmService LlmService => _llmService;
    public string ModelId => _modelId;
    public float? Temperature => _temperature;
    public int? MaxTokens => _maxTokens;

    public LlmServiceChatModel(
        ILlmService llmService,
        string modelId,
        float? temperature = null,
        int? maxTokens = null)
    {
        ArgumentNullException.ThrowIfNull(llmService);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        if (temperature is { } t && (float.IsNaN(t) || float.IsInfinity(t) || t < 0f))
            throw new ArgumentOutOfRangeException(nameof(temperature), "Temperature must be finite and non-negative.");
        if (maxTokens is <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxTokens), "MaxTokens must be positive.");

        _llmService = llmService;
        _modelId = modelId.Trim();
        _temperature = temperature;
        _maxTokens = maxTokens;
    }

    public async ValueTask<string> CompleteAsync(
        string systemPrompt,
        string userPrompt,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var request = new LlmRequest
        {
            Model = _modelId,
            SystemInstruction = string.IsNullOrWhiteSpace(systemPrompt)
                ? null
                : new LlmContent
                {
                    Role = "system",
                    Parts = { new LlmPart { Text = systemPrompt } }
                },
            Contents =
            {
                LlmContent.User(userPrompt ?? string.Empty)
            },
            Temperature = _temperature,
            MaxTokens = _maxTokens
        };

        LlmResponse response = _llmService is OpenAiService openAi
            ? await openAi.GenerateContentAsync(request, ct).ConfigureAwait(false)
            : await _llmService.GenerateContentAsync(request).WaitAsync(ct).ConfigureAwait(false);

        if (response.FunctionCalls is { Count: > 0 } calls)
        {
            LlmFunctionCall first = calls[0];
            if (string.Equals(first.Name, "error", StringComparison.Ordinal))
            {
                if (first.Args is JsonObject errorObj && errorObj["error"] is { } errorNode)
                {
                    return errorNode.ToString();
                }

                if (first.Args is { } nonObjectArgs && first.Args is not JsonObject)
                {
                    return nonObjectArgs.ToJsonString();
                }

                if (!string.IsNullOrEmpty(response.Content))
                {
                    return response.Content;
                }

                return "Error: LLM service reported an error.";
            }

            var toolPayload = new JsonObject
            {
                ["tool"] = first.Name,
                ["arguments"] = first.Args?.DeepClone() ?? new JsonObject()
            };

            if (calls.Count > 1)
            {
                var allCalls = new JsonArray();
                foreach (var call in calls)
                {
                    if (string.Equals(call.Name, "error", StringComparison.Ordinal))
                        continue;

                    allCalls.Add((JsonNode)new JsonObject
                    {
                        ["tool"] = call.Name,
                        ["arguments"] = call.Args?.DeepClone() ?? new JsonObject()
                    });
                }

                if (allCalls.Count > 0)
                {
                    toolPayload["tool_calls"] = allCalls;
                }
            }

            return toolPayload.ToJsonString();
        }

        if (!string.IsNullOrEmpty(response.Content))
        {
            return response.Content;
        }

        return string.Empty;
    }
}
