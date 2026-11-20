namespace Glacier.Agent;

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// Outcome status of a self-verification check.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<VerificationStatus>))]
public enum VerificationStatus
{
    None = 0,
    Passed = 1,
    Failed = 2,
    CircuitBreakerTripped = 3,
    Error = 4
}

/// <summary>
/// Operational state of a <see cref="VerificationCircuitBreaker"/>.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<CircuitBreakerStatus>))]
public enum CircuitBreakerStatus
{
    Closed = 0,
    Open = 1
}

/// <summary>
/// Structured finding produced by a verification check.
/// </summary>
public sealed record VerificationIssue(
    string Code,
    string Message,
    string? Field = null,
    string? SuggestedFix = null);

/// <summary>
/// Structured, AOT-serializable outcome of a verification check.
/// </summary>
public sealed record VerificationResult
{
    public bool Passed { get; init; }
    public VerificationStatus Status { get; init; } = VerificationStatus.None;
    public string Summary { get; init; } = string.Empty;
    public string? FailureReason { get; init; }
    public List<VerificationIssue> Issues { get; init; } = new();
    public string? SuggestedFix { get; init; }
    public string? VerifierName { get; init; }
    public bool CircuitTripped { get; init; }
    public int ConsecutiveFailures { get; init; }

    public static VerificationResult Pass(
        string summary = "Verification passed.",
        string? verifierName = null)
        => new()
        {
            Passed = true,
            Status = VerificationStatus.Passed,
            Summary = summary,
            VerifierName = verifierName,
            CircuitTripped = false,
            ConsecutiveFailures = 0
        };

    public static VerificationResult Fail(
        string failureReason,
        IEnumerable<VerificationIssue>? issues = null,
        string? suggestedFix = null,
        string? verifierName = null,
        string? summary = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failureReason);
        return new VerificationResult
        {
            Passed = false,
            Status = VerificationStatus.Failed,
            Summary = summary ?? failureReason,
            FailureReason = failureReason,
            Issues = issues != null ? new List<VerificationIssue>(issues) : new List<VerificationIssue>(),
            SuggestedFix = suggestedFix,
            VerifierName = verifierName
        };
    }

    public static VerificationResult Tripped(
        string failureReason,
        int consecutiveFailures,
        VerificationResult? lastResult = null,
        string? verifierName = null)
        => new()
        {
            Passed = false,
            Status = VerificationStatus.CircuitBreakerTripped,
            Summary = $"Verification circuit breaker tripped after {consecutiveFailures} consecutive failures: {failureReason}",
            FailureReason = failureReason,
            Issues = lastResult?.Issues != null ? new List<VerificationIssue>(lastResult.Issues) : new List<VerificationIssue>(),
            SuggestedFix = lastResult?.SuggestedFix,
            VerifierName = verifierName ?? lastResult?.VerifierName,
            CircuitTripped = true,
            ConsecutiveFailures = consecutiveFailures
        };

    public static VerificationResult Error(
        string errorMessage,
        string? verifierName = null,
        string? suggestedFix = null,
        IEnumerable<VerificationIssue>? issues = null)
    {
        var message = string.IsNullOrWhiteSpace(errorMessage) ? "Verification check threw an unexpected error." : errorMessage;
        var issueList = issues != null
            ? new List<VerificationIssue>(issues)
            : new List<VerificationIssue> { new("verification_error", message, SuggestedFix: suggestedFix) };

        return new VerificationResult
        {
            Passed = false,
            Status = VerificationStatus.Error,
            Summary = message,
            FailureReason = message,
            Issues = issueList,
            SuggestedFix = suggestedFix,
            VerifierName = verifierName
        };
    }

    public string ToJson() => JsonSerializer.Serialize(this, AdkJsonContext.Default.VerificationResult);
}

/// <summary>
/// Context passed into an <see cref="IVerificationHook"/> check.
/// </summary>
public sealed record VerificationContext(
    string AgentName,
    string OriginalPrompt,
    string CurrentPrompt,
    string CandidateOutput,
    int TurnOrStep,
    string? SessionId = null,
    string? ToolCallName = null,
    int ConsecutiveFailures = 0);

/// <summary>
/// Configuration controlling autonomous self-verification and circuit-breaker behavior.
/// </summary>
public sealed class VerificationOptions
{
    private int _maxConsecutiveVerificationRetries = 3;

    /// <summary>
    /// Maximum number of consecutive verification retries permitted after an initial failure
    /// before tripping the circuit breaker. For example, <c>3</c> allows 3 retries (trips on the 4th
    /// consecutive failure); <c>0</c> trips immediately on the 1st failure.
    /// </summary>
    public int MaxConsecutiveVerificationRetries
    {
        get => _maxConsecutiveVerificationRetries;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _maxConsecutiveVerificationRetries = value;
        }
    }

    /// <summary>
    /// When <see langword="true"/> and no custom stop condition is supplied in <c>LoopAgent</c>,
    /// a passing verification check terminates the loop immediately.
    /// </summary>
    public bool StopOnVerificationPass { get; set; } = true;

    /// <summary>
    /// When <see langword="true"/>, <c>LoopAgent</c> runs verification before evaluating its stop condition
    /// so unverified outputs cannot bypass verification.
    /// </summary>
    public bool RequireVerificationForStopCondition { get; set; } = true;

    /// <summary>
    /// When <see langword="true"/>, non-cancellation exceptions thrown by verification hooks or tools
    /// are captured as <see cref="VerificationStatus.Error"/> results that feed the circuit breaker.
    /// </summary>
    public bool TreatHookExceptionAsFailure { get; set; } = true;

    /// <summary>
    /// When <see langword="true"/>, non-cancellation exceptions thrown by tools in
    /// <c>AgentExecutionEngine.ExecuteStepAsync</c> are caught, emitted as <c>ToolFailed</c>, and
    /// returned as structured JSON errors with <c>IsCompleted = false</c>.
    /// </summary>
    public bool GuardToolExceptions { get; set; } = true;

    /// <summary>
    /// When <see langword="true"/>, tripping the verification circuit breaker halts execution cleanly.
    /// </summary>
    public bool HaltOnCircuitBreaker { get; set; } = true;

    /// <summary>
    /// When <see langword="true"/>, tripping the circuit breaker throws
    /// <see cref="VerificationCircuitBreakerException"/> instead of returning a structured response.
    /// </summary>
    public bool ThrowOnCircuitBreakerTripped { get; set; } = false;

    /// <summary>
    /// When <see langword="true"/>, structured failure feedback is appended to the next turn/step prompt.
    /// </summary>
    public bool AppendStructuredFeedbackToPrompt { get; set; } = true;

    /// <summary>
    /// When <see langword="true"/> in <c>AgentExecutionEngine</c>, an idle completion response automatically
    /// invokes a registered verification tool if no explicit verification hook is attached.
    /// </summary>
    public bool AutoInvokeRegisteredVerificationTool { get; set; } = false;

    /// <summary>
    /// Default tool name recognized as a self-verification tool.
    /// </summary>
    public string VerificationToolName { get; set; } = SelfVerificationTool.DefaultToolName;

    /// <summary>
    /// Additional tool names recognized as verification tools.
    /// </summary>
    public HashSet<string> VerificationToolNames { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Optional custom formatter for retry feedback prompts.
    /// </summary>
    [JsonIgnore]
    public Func<VerificationResult, VerificationCircuitBreakerState, string>? FeedbackFormatter { get; set; }

    /// <summary>
    /// Optional custom formatter for output returned when the circuit breaker trips.
    /// </summary>
    [JsonIgnore]
    public Func<VerificationResult, VerificationCircuitBreakerState, string, string>? CircuitBreakerOutputFormatter { get; set; }
}

/// <summary>
/// AOT-safe payload emitted with verification lifecycle <see cref="AgentEvent"/> notifications.
/// </summary>
public sealed record VerificationEventPayload(
    string AgentName,
    string? SessionId,
    int TurnOrStep,
    VerificationStatus Status,
    bool Passed,
    int ConsecutiveFailures,
    int MaxConsecutiveRetries,
    bool CircuitTripped,
    string? VerifierName = null,
    string? Summary = null,
    string? FailureReason = null,
    string? ToolCallName = null);

/// <summary>
/// AOT-safe structured payload returned when a guarded tool execution throws an exception.
/// </summary>
public sealed record ToolErrorPayload(
    string Status,
    string Tool,
    string ErrorType,
    string Message);

/// <summary>
/// AOT-safe structured feedback payload injected into prompts on verification failure retries.
/// </summary>
public sealed record VerificationFeedbackPayload(
    int TurnOrStep,
    int ConsecutiveFailures,
    int MaxConsecutiveRetries,
    string? VerifierName,
    string FailureReason,
    string? SuggestedFix,
    List<VerificationIssue>? Issues = null);

/// <summary>
/// AOT-safe argument contract for <see cref="SelfVerificationTool"/>.
/// </summary>
public sealed class SelfVerificationToolArgs
{
    public string? Output { get; set; }
    public string? Prompt { get; set; }
    public string? OriginalPrompt { get; set; }
    public int? Turn { get; set; }
    public string? SessionId { get; set; }
    public bool? RequireValidJson { get; set; }
    public List<string>? RequiredSubstrings { get; set; }
    public List<string>? RequiredFields { get; set; }
}
