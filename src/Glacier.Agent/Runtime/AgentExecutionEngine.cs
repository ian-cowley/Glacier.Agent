namespace Glacier.Agent.Runtime;

using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Glacier.Clavier.Engine;
using Glacier.MemEx;
using Glacier.MemEx.Models;

public sealed record AgentStepDecision(
    string Output,
    bool IsCompleted,
    string? ToolName = null,
    string? ToolArgumentsJson = null);

/// <summary>
/// Pluggable LLM completion backend for <see cref="AgentExecutionEngine"/> (e.g. Glacier.Inference or HTTP chat endpoint).
/// </summary>
public interface IAgentChatModel
{
    ValueTask<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct = default);
}

/// <summary>
/// Autonomous agent execution engine coordinating LLM or structured tool-call planning, tool execution, and reactive event publishing.
/// </summary>
public sealed class AgentExecutionEngine : IAgentRuntime
{
    private readonly AgentToolRegistry _tools = new();
    private readonly Func<AgentContext, IReadOnlyCollection<IAgentTool>, CancellationToken, ValueTask<AgentStepDecision>>? _stepPlanner;
    private readonly IAgentChatModel? _chatModel;
    private MemExEngine? _memory;
    private int _recallBudgetTokens = 512;
    private ClavierSession? _clavierSession;
    private float _conformalAlpha = 0.05f;

    public string AgentId { get; }
    public AgentToolRegistry Tools => _tools;
    public MemExEngine? EpisodicMemory => _memory;
    public ClavierSession? ConformalRouter => _clavierSession;

    public event Action<AgentEvent>? EventPublished;

    public AgentExecutionEngine(
        string agentId,
        Func<AgentContext, IReadOnlyCollection<IAgentTool>, CancellationToken, ValueTask<AgentStepDecision>>? stepPlanner = null)
    {
        AgentId = agentId ?? throw new ArgumentNullException(nameof(agentId));
        _stepPlanner = stepPlanner;
    }

    public AgentExecutionEngine(string agentId, IAgentChatModel chatModel)
    {
        AgentId = agentId ?? throw new ArgumentNullException(nameof(agentId));
        _chatModel = chatModel ?? throw new ArgumentNullException(nameof(chatModel));
    }

    public void AttachEpisodicMemory(MemExEngine memory, int recallBudgetTokens = 512)
    {
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        if (recallBudgetTokens <= 0) throw new ArgumentOutOfRangeException(nameof(recallBudgetTokens));
        _recallBudgetTokens = recallBudgetTokens;
    }

    public void AttachConformalRouter(ClavierSession clavierSession, float conformalAlpha = 0.05f)
    {
        _clavierSession = clavierSession ?? throw new ArgumentNullException(nameof(clavierSession));
        _conformalAlpha = conformalAlpha;
    }

    public void RegisterTool(IAgentTool tool)
    {
        _tools.Register(tool);
        PublishEvent("ToolRegistered", tool.Name);
    }

    public async ValueTask<AgentResponse> ExecuteLoopAsync(
        string sessionId,
        string initialPrompt,
        int maxSteps = 8,
        CancellationToken ct = default)
    {
        if (maxSteps <= 0) throw new ArgumentOutOfRangeException(nameof(maxSteps));

        string currentPrompt = initialPrompt;
        if (_memory != null && _memory.Count > 0)
        {
            var recalled = _memory.Query(initialPrompt, ReadOnlySpan<float>.Empty, maxTokens: _recallBudgetTokens);
            if (!string.IsNullOrWhiteSpace(recalled.FormattedContext))
            {
                PublishEvent("MemoryRecalled", $"Traces: {recalled.SelectedTraces.Count}, Tokens: {recalled.TotalTokens}");
                currentPrompt = $"[EPISODIC MEMORY]\n{recalled.FormattedContext}\n\n[USER PROMPT]\n{initialPrompt}";
            }
        }

        AgentResponse lastResponse = new(string.Empty, true);

        for (int step = 1; step <= maxSteps; step++)
        {
            lastResponse = await ExecuteStepAsync(new AgentContext(sessionId, currentPrompt, step), ct).ConfigureAwait(false);
            if (lastResponse.IsCompleted)
            {
                _memory?.StoreTrace(new EpisodicTrace
                {
                    Content = $"Prompt: {initialPrompt}\nResult: {lastResponse.Output}",
                    BaseImportance = 2.0f
                });
                return lastResponse;
            }

            currentPrompt = $"{initialPrompt}\n<|tool_result|>{{\"tool\":\"{lastResponse.ToolCallName}\",\"output\":{JsonEncodeString(lastResponse.Output)}}}";
        }

        _memory?.StoreTrace(new EpisodicTrace
        {
            Content = $"Prompt: {initialPrompt}\nResult: {lastResponse.Output}",
            BaseImportance = 1.5f
        });
        return new AgentResponse(lastResponse.Output, IsCompleted: true, lastResponse.ToolCallName);
    }

    public async ValueTask<AgentResponse> ExecuteStepAsync(AgentContext context, CancellationToken ct = default)
    {
        PublishEvent("StepStarting", $"Step: {context.StepCount}, Session: {context.SessionId}");

        AgentStepDecision? decision = null;
        if (_clavierSession != null && context.StepCount == 1 && _tools.Tools.Count > 0)
        {
            var toolNames = new string[_tools.Tools.Count];
            int idx = 0;
            foreach (var t in _tools.Tools) toolNames[idx++] = t.Name;

            var predictionSet = _clavierSession.DecideConformalSet(context.Prompt, _conformalAlpha, toolNames);
            if (predictionSet.IsSystem1Eligible &&
                !string.IsNullOrWhiteSpace(predictionSet.BestActionName) &&
                _tools.TryGetTool(predictionSet.BestActionName, out _))
            {
                PublishEvent("ConformalFastDispatch", $"{predictionSet.BestActionName} (conf={predictionSet.BestConfidence:F3})");
                decision = new AgentStepDecision(string.Empty, IsCompleted: false, ToolName: predictionSet.BestActionName, ToolArgumentsJson: "{}");
            }
            else
            {
                PublishEvent("ConformalEscalated", predictionSet.EscalationReason.ToString());
            }
        }

        if (decision == null)
        {
            if (_stepPlanner != null)
            {
                decision = await _stepPlanner(context, _tools.Tools, ct).ConfigureAwait(false);
            }
            else if (_chatModel != null)
            {
                string sysPrompt = BuildToolSystemPrompt(_tools.Tools);
                string completion = await _chatModel.CompleteAsync(sysPrompt, context.Prompt, ct).ConfigureAwait(false);
                decision = ParseModelOrStructuredDirective(completion, isRawModelOutput: true);
            }
            else
            {
                decision = ParseModelOrStructuredDirective(context.Prompt, isRawModelOutput: false);
            }
        }

        if (!string.IsNullOrWhiteSpace(decision.ToolName))
        {
            if (_tools.TryGetTool(decision.ToolName, out IAgentTool? tool))
            {
                PublishEvent("ToolInvoking", decision.ToolName);
                string args = decision.ToolArgumentsJson ?? "{}";
                string result = await tool!.ExecuteAsync(args, ct).ConfigureAwait(false);
                PublishEvent("ToolCompleted", $"{decision.ToolName} -> {result}");

                return new AgentResponse(
                    Output: result,
                    IsCompleted: decision.IsCompleted,
                    ToolCallName: decision.ToolName);
            }
            else
            {
                string errorMsg = $"Error: Requested tool '{decision.ToolName}' is not registered.";
                PublishEvent("ToolFailed", errorMsg);
                return new AgentResponse(Output: errorMsg, IsCompleted: true);
            }
        }

        PublishEvent("StepCompleted", decision.Output);
        return new AgentResponse(decision.Output, decision.IsCompleted);
    }

    private static string BuildToolSystemPrompt(IReadOnlyCollection<IAgentTool> tools)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are an autonomous Glacier agent. When invoking a tool, respond ONLY with JSON: {\"tool\":\"<name>\",\"arguments\":{...}}.");
        if (tools.Count > 0)
        {
            sb.AppendLine("Available tools:");
            foreach (var t in tools)
            {
                sb.Append("- ").Append(t.Name).Append(": ").Append(t.Description).Append(" | schema: ").AppendLine(t.ParameterJsonSchema);
            }
        }
        return sb.ToString();
    }

    public static AgentStepDecision ParseModelOrStructuredDirective(string rawInput, bool isRawModelOutput = false)
    {
        string trimmed = rawInput.Trim();

        // 1. Support <|tool_call|>{...} tags
        const string toolTag = "<|tool_call|>";
        int tagIdx = trimmed.IndexOf(toolTag, StringComparison.OrdinalIgnoreCase);
        if (tagIdx >= 0)
        {
            string afterTag = trimmed[(tagIdx + toolTag.Length)..].Trim();
            int endTag = afterTag.IndexOf("<|", StringComparison.Ordinal);
            if (endTag > 0) afterTag = afterTag[..endTag].Trim();
            if (TryParseJsonToolCall(afterTag, out var taggedDecision))
            {
                return taggedDecision;
            }
        }

        // 2. Support structured JSON {"tool":"...","arguments":{...}} or {"name":"...","arguments":{...}}
        if (trimmed.StartsWith('{') && trimmed.EndsWith('}') && TryParseJsonToolCall(trimmed, out var jsonDecision))
        {
            return jsonDecision;
        }

        // 3. Support "CALL <tool>: <args>" or "tool:<tool> <args>"
        if (trimmed.StartsWith("CALL ", StringComparison.OrdinalIgnoreCase))
        {
            string remainder = trimmed[5..].Trim();
            int colonIdx = remainder.IndexOf(':');
            if (colonIdx > 0)
            {
                string toolName = remainder[..colonIdx].Trim();
                string toolArgs = remainder[(colonIdx + 1)..].Trim();
                return new AgentStepDecision("Invoking tool " + toolName, false, toolName, string.IsNullOrEmpty(toolArgs) ? "{}" : toolArgs);
            }
            return new AgentStepDecision("Invoking tool " + remainder, false, remainder, "{}");
        }

        if (trimmed.StartsWith("tool:", StringComparison.OrdinalIgnoreCase))
        {
            string remainder = trimmed[5..].Trim();
            int spaceIdx = remainder.IndexOfAny([' ', ':']);
            if (spaceIdx > 0)
            {
                string toolName = remainder[..spaceIdx].Trim();
                string toolArgs = remainder[(spaceIdx + 1)..].Trim();
                return new AgentStepDecision("Invoking tool " + toolName, false, toolName, string.IsNullOrEmpty(toolArgs) ? "{}" : toolArgs);
            }
            return new AgentStepDecision("Invoking tool " + remainder, false, remainder, "{}");
        }

        string output = isRawModelOutput ? trimmed : $"Processed prompt: {trimmed}";
        return new AgentStepDecision(output, IsCompleted: true);
    }

    private static bool TryParseJsonToolCall(string json, out AgentStepDecision decision)
    {
        decision = default!;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;

            string? name = null;
            if (root.TryGetProperty("tool", out var tProp) && tProp.ValueKind == JsonValueKind.String)
                name = tProp.GetString();
            else if (root.TryGetProperty("name", out var nProp) && nProp.ValueKind == JsonValueKind.String)
                name = nProp.GetString();

            if (string.IsNullOrWhiteSpace(name)) return false;

            string argsJson = "{}";
            if (root.TryGetProperty("arguments", out var argsProp))
            {
                argsJson = argsProp.ValueKind == JsonValueKind.String
                    ? (argsProp.GetString() ?? "{}")
                    : argsProp.GetRawText();
            }

            decision = new AgentStepDecision("Invoking tool " + name, false, name, argsJson);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string JsonEncodeString(string value)
    {
        using var ms = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStringValue(value);
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private void PublishEvent(string eventType, string payload)
    {
        EventPublished?.Invoke(new AgentEvent(AgentId, eventType, payload));
    }

    public void Dispose()
    {
        _tools.Clear();
    }
}
