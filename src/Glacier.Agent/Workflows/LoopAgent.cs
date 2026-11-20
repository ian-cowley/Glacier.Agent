using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Glacier.Agent.Workflows;

/// <summary>
/// Executes a task repeatedly until a condition is met, verification passes, or a turn/circuit-breaker limit is reached.
/// </summary>
public class LoopAgent : IAgent
{
    public string Name { get; }
    public string Description { get; }
    private readonly IAgent _loopMember;
    private readonly int _maxTurns;
    private readonly Func<string, bool>? _stopCondition;
    private readonly List<VerificationResult> _verificationHistory = new();
    private IVerificationHook? _toolBackedHook;
    private SelfVerificationTool? _attachedSelfVerificationTool;

    public VerificationOptions VerificationOptions { get; set; }
    public IVerificationHook? VerificationHook { get; set; }
    public VerificationCircuitBreakerState? LastVerificationState { get; private set; }
    public VerificationResult? LastVerificationResult { get; private set; }
    public IReadOnlyList<VerificationResult> VerificationHistory => _verificationHistory;

    public event Action<AgentEvent>? EventPublished;

    public LoopAgent(string name, string description, IAgent loopMember, int maxTurns = 5, Func<string, bool>? stopCondition = null)
    {
        Name = name;
        Description = description;
        _loopMember = loopMember;
        _maxTurns = maxTurns;
        _stopCondition = stopCondition;
        VerificationOptions = new VerificationOptions();
    }

    public LoopAgent(
        string name,
        string description,
        IAgent loopMember,
        VerificationOptions verificationOptions,
        IVerificationHook? verificationHook = null,
        int maxTurns = 5,
        Func<string, bool>? stopCondition = null)
        : this(name, description, loopMember, maxTurns, stopCondition)
    {
        VerificationOptions = verificationOptions ?? throw new ArgumentNullException(nameof(verificationOptions));
        VerificationHook = verificationHook;
    }

    public LoopAgent(
        string name,
        string description,
        IAgent loopMember,
        int maxTurns,
        Func<string, bool>? stopCondition,
        IVerificationHook verificationHook)
        : this(name, description, loopMember, maxTurns, stopCondition)
    {
        VerificationHook = verificationHook ?? throw new ArgumentNullException(nameof(verificationHook));
    }

    public void UseVerificationTool(ITool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        var hook = new ToolVerificationHook(tool);
        _toolBackedHook = hook;
        _attachedSelfVerificationTool = tool as SelfVerificationTool;
        VerificationHook = hook;
    }

    public void UseVerificationTool(IAgentTool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        var hook = new ToolVerificationHook(tool);
        _toolBackedHook = hook;
        _attachedSelfVerificationTool = tool as SelfVerificationTool;
        VerificationHook = hook;
    }

    public async Task<string> RunAsync(string prompt, ILlmService llmService, string? sessionId = null)
    {
        using var activity = Telemetry.ActivitySource.StartActivity($"Loop {Name}");
        activity?.SetTag("agent.name", Name);
        activity?.SetTag("session.id", sessionId);

        var breaker = InitializeRunVerification();
        string currentInput = prompt;
        string lastOutput = prompt;

        for (int i = 1; i <= _maxTurns; i++)
        {
            string memberOutput = (await _loopMember.RunAsync(currentInput, llmService, sessionId).ConfigureAwait(false)) ?? string.Empty;
            lastOutput = memberOutput;

            if (VerificationHook == null)
            {
                if (_loopMember is LlmAgent llmMember && llmMember.LastVerificationState != null)
                {
                    LastVerificationState = llmMember.LastVerificationState;
                    LastVerificationResult = llmMember.LastVerificationResult;
                    if (llmMember.LastVerificationState.IsTripped && VerificationOptions.HaltOnCircuitBreaker)
                    {
                        EmitVerificationEvent("VerificationCircuitBreakerTripped", sessionId, i, LastVerificationResult!, LastVerificationState);
                        return memberOutput;
                    }
                }

                currentInput = memberOutput;
                if (_stopCondition != null && _stopCondition(currentInput))
                {
                    break;
                }
                continue;
            }

            if (!VerificationOptions.RequireVerificationForStopCondition &&
                _stopCondition != null &&
                _stopCondition(memberOutput))
            {
                return memberOutput;
            }

            var verResult = await ExecuteVerificationAsync(prompt, currentInput, memberOutput, i, sessionId, breaker).ConfigureAwait(false);
            if (verResult.Passed)
            {
                if (_stopCondition != null)
                {
                    if (_stopCondition(memberOutput))
                    {
                        return memberOutput;
                    }
                    currentInput = memberOutput;
                    continue;
                }

                if (VerificationOptions.StopOnVerificationPass)
                {
                    return memberOutput;
                }

                currentInput = memberOutput;
                continue;
            }

            var state = LastVerificationState!;
            if (state.IsTripped)
            {
                if (VerificationOptions.ThrowOnCircuitBreakerTripped)
                {
                    throw new VerificationCircuitBreakerException(state, LastVerificationResult!);
                }

                if (VerificationOptions.HaltOnCircuitBreaker)
                {
                    return FormatCircuitBreakerOutput(LastVerificationResult!, state, memberOutput);
                }
            }

            if (i < _maxTurns)
            {
                currentInput = VerificationOptions.AppendStructuredFeedbackToPrompt
                    ? FormatRetryFeedback(prompt, memberOutput, LastVerificationResult!, state)
                    : memberOutput;
            }
        }

        if (VerificationHook != null &&
            LastVerificationResult is { Passed: false } &&
            LastVerificationState != null &&
            VerificationOptions.HaltOnCircuitBreaker)
        {
            return FormatCircuitBreakerOutput(LastVerificationResult, LastVerificationState, lastOutput);
        }

        return lastOutput;
    }

    public async IAsyncEnumerable<string> RunStreamingAsync(string prompt, ILlmService llmService, string? sessionId = null)
    {
        using var activity = Telemetry.ActivitySource.StartActivity($"Loop {Name}");
        activity?.SetTag("agent.name", Name);
        activity?.SetTag("session.id", sessionId);

        var breaker = InitializeRunVerification();
        string currentInput = prompt;

        for (int i = 1; i <= _maxTurns; i++)
        {
            string memberOutput = (await _loopMember.RunAsync(currentInput, llmService, sessionId).ConfigureAwait(false)) ?? string.Empty;
            yield return $"--- Turn {i} ---\n{memberOutput}\n";

            if (VerificationHook == null)
            {
                if (_loopMember is LlmAgent llmMember && llmMember.LastVerificationState != null)
                {
                    LastVerificationState = llmMember.LastVerificationState;
                    LastVerificationResult = llmMember.LastVerificationResult;
                    if (llmMember.LastVerificationState.IsTripped && VerificationOptions.HaltOnCircuitBreaker)
                    {
                        EmitVerificationEvent("VerificationCircuitBreakerTripped", sessionId, i, LastVerificationResult!, LastVerificationState);
                        yield break;
                    }
                }

                currentInput = memberOutput;
                if (_stopCondition != null && _stopCondition(currentInput))
                {
                    break;
                }
                continue;
            }

            if (!VerificationOptions.RequireVerificationForStopCondition &&
                _stopCondition != null &&
                _stopCondition(memberOutput))
            {
                yield break;
            }

            var verResult = await ExecuteVerificationAsync(prompt, currentInput, memberOutput, i, sessionId, breaker).ConfigureAwait(false);
            if (verResult.Passed)
            {
                if (_stopCondition != null)
                {
                    if (_stopCondition(memberOutput))
                    {
                        yield break;
                    }
                    currentInput = memberOutput;
                    continue;
                }

                if (VerificationOptions.StopOnVerificationPass)
                {
                    yield break;
                }

                currentInput = memberOutput;
                continue;
            }

            var state = LastVerificationState!;
            if (state.IsTripped)
            {
                if (VerificationOptions.ThrowOnCircuitBreakerTripped)
                {
                    throw new VerificationCircuitBreakerException(state, LastVerificationResult!);
                }

                if (VerificationOptions.HaltOnCircuitBreaker)
                {
                    string halted = FormatCircuitBreakerOutput(LastVerificationResult!, state, memberOutput);
                    yield return $"--- Verification Circuit Breaker Tripped ---\n{halted}\n";
                    yield break;
                }
            }

            if (i < _maxTurns)
            {
                currentInput = VerificationOptions.AppendStructuredFeedbackToPrompt
                    ? FormatRetryFeedback(prompt, memberOutput, LastVerificationResult!, state)
                    : memberOutput;
            }

            yield return $"--- Verification Failed (Consecutive: {state.ConsecutiveFailures}, Max Retries: {state.MaxConsecutiveRetries}) ---\n{LastVerificationResult!.FailureReason}\n";
        }
    }

    private VerificationCircuitBreaker? GetAttachedExternalCircuitBreaker()
        => ReferenceEquals(VerificationHook, _toolBackedHook)
            ? _attachedSelfVerificationTool?.CircuitBreaker
            : null;

    private VerificationCircuitBreaker InitializeRunVerification()
    {
        var breaker = GetAttachedExternalCircuitBreaker()
            ?? new VerificationCircuitBreaker(VerificationOptions.MaxConsecutiveVerificationRetries);
        _verificationHistory.Clear();
        LastVerificationState = breaker.GetState();
        LastVerificationResult = null;
        return breaker;
    }

    private async ValueTask<VerificationResult> ExecuteVerificationAsync(
        string originalPrompt,
        string currentPrompt,
        string candidateOutput,
        int turn,
        string? sessionId,
        VerificationCircuitBreaker breaker)
    {
        var hook = VerificationHook!;
        var externalBreaker = GetAttachedExternalCircuitBreaker();
        var activeBreaker = externalBreaker ?? breaker;

        using var verifyActivity = Telemetry.ActivitySource.StartActivity($"Verify {Name}");
        verifyActivity?.SetTag("agent.name", Name);
        verifyActivity?.SetTag("runtime.kind", "LoopAgent");
        verifyActivity?.SetTag("session.id", sessionId);
        verifyActivity?.SetTag("verification.attempt", turn);

        var metricTags = new[]
        {
            new KeyValuePair<string, object?>("agent", Name),
            new KeyValuePair<string, object?>("runtime", "LoopAgent")
        };

        Telemetry.VerificationAttemptsCounter.Add(1, metricTags);

        var attemptState = activeBreaker.GetState();
        var attemptResult = new VerificationResult
        {
            Passed = false,
            Status = VerificationStatus.None,
            Summary = $"Running verification hook '{hook.Name}' for turn {turn}.",
            VerifierName = hook.Name,
            ConsecutiveFailures = attemptState.ConsecutiveFailures
        };
        EmitVerificationEvent("VerificationAttempted", sessionId, turn, attemptResult, attemptState);

        var context = new VerificationContext(
            AgentName: Name,
            OriginalPrompt: originalPrompt,
            CurrentPrompt: currentPrompt,
            CandidateOutput: candidateOutput,
            TurnOrStep: turn,
            SessionId: sessionId,
            ToolCallName: null,
            ConsecutiveFailures: attemptState.ConsecutiveFailures);

        VerificationResult rawResult;
        bool hookThrew = false;
        try
        {
            rawResult = await hook.VerifyAsync(context, CancellationToken.None).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(rawResult.VerifierName))
            {
                rawResult = rawResult with { VerifierName = hook.Name };
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (!VerificationOptions.TreatHookExceptionAsFailure)
            {
                throw;
            }
            hookThrew = true;
            rawResult = VerificationResult.Error(ex.Message, hook.Name);
        }

        if (externalBreaker != null && !hookThrew)
        {
            LastVerificationState = externalBreaker.GetState();
            LastVerificationResult = LastVerificationState.LastResult ?? rawResult;
        }
        else if (rawResult.Passed)
        {
            LastVerificationState = activeBreaker.RecordSuccess(rawResult);
            LastVerificationResult = LastVerificationState.LastResult!;
        }
        else
        {
            LastVerificationState = activeBreaker.RecordFailure(rawResult);
            LastVerificationResult = LastVerificationState.LastResult!;

            if (!LastVerificationState.IsTripped &&
                (rawResult.CircuitTripped || rawResult.Status == VerificationStatus.CircuitBreakerTripped))
            {
                var trippedResult = rawResult with
                {
                    Passed = false,
                    Status = VerificationStatus.CircuitBreakerTripped,
                    CircuitTripped = true,
                    ConsecutiveFailures = Math.Max(rawResult.ConsecutiveFailures, LastVerificationState.ConsecutiveFailures)
                };
                LastVerificationResult = trippedResult;
                LastVerificationState = new VerificationCircuitBreakerState(
                    CircuitBreakerStatus.Open,
                    trippedResult.ConsecutiveFailures,
                    Math.Min(LastVerificationState.MaxConsecutiveRetries, Math.Max(0, trippedResult.ConsecutiveFailures - 1)),
                    LastVerificationState.TotalPasses,
                    LastVerificationState.TotalFailures,
                    trippedResult.FailureReason ?? LastVerificationState.LastFailureReason,
                    trippedResult);
            }
        }

        _verificationHistory.Add(LastVerificationResult);

        if (LastVerificationResult.Passed)
        {
            Telemetry.VerificationPassesCounter.Add(1, metricTags);
            verifyActivity?.SetTag("verification.passed", true);
            verifyActivity?.SetTag("verification.consecutive_failures", 0);
            verifyActivity?.SetTag("verification.circuit_breaker_tripped", false);
            verifyActivity?.SetTag("status", "passed");

            EmitVerificationEvent("VerificationPassed", sessionId, turn, LastVerificationResult, LastVerificationState);
            return LastVerificationResult;
        }

        Telemetry.VerificationFailuresCounter.Add(1, metricTags);
        verifyActivity?.SetTag("verification.passed", false);
        verifyActivity?.SetTag("verification.consecutive_failures", LastVerificationState.ConsecutiveFailures);
        verifyActivity?.SetTag("verification.circuit_breaker_tripped", LastVerificationState.IsTripped);

        EmitVerificationEvent("VerificationFailed", sessionId, turn, LastVerificationResult, LastVerificationState);

        if (LastVerificationState.IsTripped)
        {
            Telemetry.CircuitBreakerTripsCounter.Add(1, metricTags);
            verifyActivity?.SetTag("status", "circuit_breaker_tripped");
            EmitVerificationEvent("VerificationCircuitBreakerTripped", sessionId, turn, LastVerificationResult, LastVerificationState);
        }
        else
        {
            verifyActivity?.SetTag("status", "failed");
        }

        return LastVerificationResult;
    }

    private void EmitVerificationEvent(
        string eventType,
        string? sessionId,
        int turn,
        VerificationResult result,
        VerificationCircuitBreakerState state)
    {
        if (EventPublished == null) return;

        var payload = new VerificationEventPayload(
            AgentName: Name,
            SessionId: sessionId,
            TurnOrStep: turn,
            Status: result.Status,
            Passed: result.Passed,
            ConsecutiveFailures: state.ConsecutiveFailures,
            MaxConsecutiveRetries: state.MaxConsecutiveRetries,
            CircuitTripped: state.IsTripped,
            VerifierName: result.VerifierName,
            Summary: result.Summary,
            FailureReason: result.FailureReason);

        string json = JsonSerializer.Serialize(payload, AdkJsonContext.Default.VerificationEventPayload);
        EventPublished.Invoke(new AgentEvent(Name, eventType, json));
    }

    private string FormatRetryFeedback(
        string originalPrompt,
        string previousOutput,
        VerificationResult result,
        VerificationCircuitBreakerState state)
    {
        if (VerificationOptions.FeedbackFormatter != null)
        {
            return VerificationOptions.FeedbackFormatter(result, state);
        }

        var feedbackPayload = new VerificationFeedbackPayload(
            TurnOrStep: _verificationHistory.Count,
            ConsecutiveFailures: state.ConsecutiveFailures,
            MaxConsecutiveRetries: state.MaxConsecutiveRetries,
            VerifierName: result.VerifierName,
            FailureReason: result.FailureReason ?? result.Summary,
            SuggestedFix: result.SuggestedFix,
            Issues: result.Issues is { Count: > 0 } ? result.Issues : null);

        string feedbackJson = JsonSerializer.Serialize(feedbackPayload, AdkJsonContext.Default.VerificationFeedbackPayload);
        var sb = new StringBuilder();
        sb.AppendLine(originalPrompt);
        sb.AppendLine();
        sb.AppendLine("[PREVIOUS OUTPUT]");
        sb.AppendLine(previousOutput);
        sb.AppendLine();
        sb.Append("<|verification_failed|>");
        sb.AppendLine(feedbackJson);
        sb.Append("Please fix the verification failure above and produce a corrected output.");
        return sb.ToString();
    }

    private string FormatCircuitBreakerOutput(
        VerificationResult result,
        VerificationCircuitBreakerState state,
        string lastOutput)
    {
        if (VerificationOptions.CircuitBreakerOutputFormatter != null)
        {
            return VerificationOptions.CircuitBreakerOutputFormatter(result, state, lastOutput);
        }

        return result.ToJson();
    }
}
