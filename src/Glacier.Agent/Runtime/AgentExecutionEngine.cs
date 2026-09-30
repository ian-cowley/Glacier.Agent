namespace Glacier.Agent.Runtime;

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

public sealed record AgentStepDecision(
    string Output,
    bool IsCompleted,
    string? ToolName = null,
    string? ToolArgumentsJson = null);

/// <summary>
/// Autonomous agent execution engine coordinating step reasoning, tool execution, and reactive event publishing.
/// Implements <see cref="IAgentRuntime"/>.
/// </summary>
public sealed class AgentExecutionEngine : IAgentRuntime
{
    private readonly AgentToolRegistry _tools = new();
    private readonly Func<AgentContext, IReadOnlyCollection<IAgentTool>, CancellationToken, ValueTask<AgentStepDecision>>? _stepPlanner;

    public string AgentId { get; }
    public AgentToolRegistry Tools => _tools;

    public event Action<AgentEvent>? EventPublished;

    public AgentExecutionEngine(
        string agentId,
        Func<AgentContext, IReadOnlyCollection<IAgentTool>, CancellationToken, ValueTask<AgentStepDecision>>? stepPlanner = null)
    {
        AgentId = agentId ?? throw new ArgumentNullException(nameof(agentId));
        _stepPlanner = stepPlanner;
    }

    public void RegisterTool(IAgentTool tool)
    {
        _tools.Register(tool);
        PublishEvent("ToolRegistered", tool.Name);
    }

    public async ValueTask<AgentResponse> ExecuteStepAsync(AgentContext context, CancellationToken ct = default)
    {
        PublishEvent("StepStarting", $"Step: {context.StepCount}, Session: {context.SessionId}");

        AgentStepDecision decision;
        if (_stepPlanner != null)
        {
            decision = await _stepPlanner(context, _tools.Tools, ct).ConfigureAwait(false);
        }
        else
        {
            // Default deterministic heuristic planner:
            // Check if context prompt requests a tool: e.g., "CALL <tool_name>: <json_args>"
            decision = EvaluateHeuristicPrompt(context);
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

    private AgentStepDecision EvaluateHeuristicPrompt(AgentContext context)
    {
        string prompt = context.Prompt.Trim();

        // Check for "CALL <tool>: <args>" or "tool:<tool_name>"
        if (prompt.StartsWith("CALL ", StringComparison.OrdinalIgnoreCase))
        {
            string remainder = prompt.Substring(5).Trim();
            int colonIdx = remainder.IndexOf(':');
            if (colonIdx > 0)
            {
                string toolName = remainder.Substring(0, colonIdx).Trim();
                string toolArgs = remainder.Substring(colonIdx + 1).Trim();
                return new AgentStepDecision("Invoking tool " + toolName, false, toolName, toolArgs);
            }
            else
            {
                string toolName = remainder;
                return new AgentStepDecision("Invoking tool " + toolName, false, toolName, "{}");
            }
        }

        return new AgentStepDecision($"Processed prompt: {prompt}", IsCompleted: true);
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
