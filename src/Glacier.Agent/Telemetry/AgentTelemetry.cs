using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Glacier.Agent;

public static class Telemetry
{
    public static readonly ActivitySource ActivitySource = new("Glacier.AgentDevKit.Adk");
    public static readonly Meter Meter = new("Glacier.AgentDevKit.Adk");

    // Metrics
    public static readonly Counter<long> TokenCounter = Meter.CreateCounter<long>("Glacier.AgentDevKit.Adk_tokens_total", "tokens", "Total tokens consumed by agents");
    public static readonly Counter<long> AgentRunsCounter = Meter.CreateCounter<long>("Glacier.AgentDevKit.Adk_agent_runs_total", "runs", "Total number of agent executions");
    public static readonly Counter<long> ToolExecutionCounter = Meter.CreateCounter<long>("Glacier.AgentDevKit.Adk_tool_executions_total", "calls", "Total number of tool calls");
}
