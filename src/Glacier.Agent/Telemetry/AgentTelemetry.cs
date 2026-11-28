using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Glacier.Agent;

public static class Telemetry
{
    public static readonly ActivitySource ActivitySource = new("Glacier.Agent");
    public static readonly Meter Meter = new("Glacier.Agent");

    // Metrics
    public static readonly Counter<long> TokenCounter = Meter.CreateCounter<long>("Glacier.Agent_tokens_total", "tokens", "Total tokens consumed by agents");
    public static readonly Counter<long> AgentRunsCounter = Meter.CreateCounter<long>("Glacier.Agent_agent_runs_total", "runs", "Total number of agent executions");
    public static readonly Counter<long> ToolExecutionCounter = Meter.CreateCounter<long>("Glacier.Agent_tool_executions_total", "calls", "Total number of tool calls");
    public static readonly Counter<long> VerificationAttemptsCounter = Meter.CreateCounter<long>("Glacier.Agent_verification_attempts_total", "attempts", "Total number of agent self-verification checks attempted");
    public static readonly Counter<long> VerificationPassesCounter = Meter.CreateCounter<long>("Glacier.Agent_verification_passes_total", "passes", "Total number of agent self-verification checks that passed");
    public static readonly Counter<long> VerificationFailuresCounter = Meter.CreateCounter<long>("Glacier.Agent_verification_failures_total", "failures", "Total number of agent self-verification checks that failed");
    public static readonly Counter<long> CircuitBreakerTripsCounter = Meter.CreateCounter<long>("Glacier.Agent_verification_circuit_breaker_trips_total", "trips", "Total number of times the consecutive verification retry circuit breaker tripped");
}
