namespace Glacier.Agent.Runtime;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
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
    private readonly List<IVerificationHook> _verificationHooks = new();
    private readonly HashSet<string> _verificationToolNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, VerificationCircuitBreaker> _sessionBreakers = new(StringComparer.Ordinal);
    private VerificationOptions _verificationOptions = new();
    private MemExEngine? _memory;
    private int _recallBudgetTokens = 512;
    private ClavierSession? _clavierSession;
    private float _conformalAlpha = 0.05f;

    public string AgentId { get; }
    public AgentToolRegistry Tools => _tools;
    public MemExEngine? EpisodicMemory => _memory;
    public ClavierSession? ConformalRouter => _clavierSession;
    public VerificationOptions VerificationOptions => _verificationOptions;

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

    public void ConfigureVerification(VerificationOptions options)
    {
        _verificationOptions = options ?? throw new ArgumentNullException(nameof(options));
    }

    public void AttachVerificationHook(IVerificationHook hook)
    {
        ArgumentNullException.ThrowIfNull(hook);
        _verificationHooks.Add(hook);
    }

    public void AttachVerificationHook(
        string name,
        Func<VerificationContext, CancellationToken, ValueTask<VerificationResult>> handler)
    {
        _verificationHooks.Add(new DelegateVerificationHook(name, handler));
    }

    public void RegisterTool(IAgentTool tool)
    {
        _tools.Register(tool);
        PublishEvent("ToolRegistered", tool.Name);
    }

    public void RegisterVerificationTool(IAgentTool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        _tools.Register(tool);
        _verificationToolNames.Add(tool.Name);
        PublishEvent("ToolRegistered", tool.Name);
    }

    public VerificationCircuitBreaker? GetCircuitBreaker(string sessionId)
        => _sessionBreakers.TryGetValue(sessionId, out var breaker) ? breaker : null;

    public void ResetVerificationCircuitBreaker(string? sessionId = null)
    {
        if (sessionId == null)
        {
            foreach (var breaker in _sessionBreakers.Values)
            {
                breaker.Reset();
            }
            return;
        }

        if (_sessionBreakers.TryGetValue(sessionId, out var sessionBreaker))
        {
            sessionBreaker.Reset();
        }
    }

    public async ValueTask<AgentResponse> ExecuteLoopAsync(
        string sessionId,
        string initialPrompt,
        int maxSteps = 8,
        CancellationToken ct = default)
    {
        if (maxSteps <= 0) throw new ArgumentOutOfRangeException(nameof(maxSteps));

        var loopBreaker = new VerificationCircuitBreaker(_verificationOptions.MaxConsecutiveVerificationRetries);
        _sessionBreakers[sessionId] = loopBreaker;

        string startPrompt = initialPrompt;
        if (_memory != null && _memory.Count > 0)
        {
            var recalled = _memory.Query(initialPrompt, ReadOnlySpan<float>.Empty, maxTokens: _recallBudgetTokens);
            if (!string.IsNullOrWhiteSpace(recalled.FormattedContext))
            {
                PublishEvent("MemoryRecalled", $"Traces: {recalled.SelectedTraces.Count}, Tokens: {recalled.TotalTokens}");
                startPrompt = $"[EPISODIC MEMORY]\n{recalled.FormattedContext}\n\n[USER PROMPT]\n{initialPrompt}";
            }
        }

        var promptBuilder = new StringBuilder(startPrompt);
        AgentResponse lastResponse = new(string.Empty, true);

        for (int step = 1; step <= maxSteps; step++)
        {
            string currentPrompt = promptBuilder.ToString();
            lastResponse = await ExecuteStepAsync(new AgentContext(sessionId, currentPrompt, step), ct).ConfigureAwait(false);

            if (lastResponse.IsVerificationCircuitBreakerTripped && _verificationOptions.HaltOnCircuitBreaker)
            {
                return lastResponse;
            }

            if (lastResponse.IsCompleted)
            {
                bool hasHooks = _verificationHooks.Count > 0;
                IAgentTool? autoTool = null;
                bool runAutoTool = !hasHooks
                    && _verificationOptions.AutoInvokeRegisteredVerificationTool
                    && lastResponse.VerificationStatus != VerificationStatus.Passed
                    && TryFindAutoVerificationTool(out autoTool);

                if (hasHooks || runAutoTool)
                {
                    var (verResult, state) = await RunPostCompletionVerificationAsync(
                        sessionId,
                        initialPrompt,
                        currentPrompt,
                        lastResponse,
                        step,
                        autoTool,
                        ct).ConfigureAwait(false);

                    if (verResult.Passed)
                    {
                        _memory?.StoreTrace(new EpisodicTrace
                        {
                            Content = $"Prompt: {initialPrompt}\nResult: {lastResponse.Output}",
                            BaseImportance = 2.0f
                        });
                        return lastResponse with
                        {
                            VerificationStatus = VerificationStatus.Passed,
                            VerificationResult = verResult
                        };
                    }

                    if (state.IsTripped)
                    {
                        if (_verificationOptions.ThrowOnCircuitBreakerTripped)
                        {
                            throw new VerificationCircuitBreakerException(state, verResult);
                        }

                        if (_verificationOptions.HaltOnCircuitBreaker)
                        {
                            return new AgentResponse(
                                Output: FormatCircuitBreakerOutput(verResult, state, lastResponse.Output),
                                IsCompleted: false,
                                ToolCallName: lastResponse.ToolCallName)
                            {
                                VerificationStatus = VerificationStatus.CircuitBreakerTripped,
                                VerificationResult = verResult
                            };
                        }
                    }

                    if (step < maxSteps)
                    {
                        if (_verificationOptions.AppendStructuredFeedbackToPrompt)
                        {
                            AppendVerificationFailureFeedback(promptBuilder, step, verResult, state);
                        }
                        continue;
                    }

                    return new AgentResponse(
                        Output: lastResponse.Output,
                        IsCompleted: false,
                        ToolCallName: lastResponse.ToolCallName)
                    {
                        VerificationStatus = state.IsTripped ? VerificationStatus.CircuitBreakerTripped : VerificationStatus.Failed,
                        VerificationResult = verResult
                    };
                }

                _memory?.StoreTrace(new EpisodicTrace
                {
                    Content = $"Prompt: {initialPrompt}\nResult: {lastResponse.Output}",
                    BaseImportance = 2.0f
                });
                return lastResponse;
            }

            promptBuilder.Append('\n')
                .Append("<|tool_result|>{\"tool\":")
                .Append(JsonEncodeString(lastResponse.ToolCallName ?? string.Empty))
                .Append(",\"output\":")
                .Append(JsonEncodeString(lastResponse.Output))
                .Append('}');

            if ((lastResponse.VerificationStatus == VerificationStatus.Failed || lastResponse.VerificationStatus == VerificationStatus.CircuitBreakerTripped)
                && _verificationOptions.AppendStructuredFeedbackToPrompt
                && lastResponse.VerificationResult != null)
            {
                var breaker = GetOrCreateSessionBreaker(sessionId);
                AppendVerificationFailureFeedback(promptBuilder, step, lastResponse.VerificationResult, breaker.GetState());
            }
        }

        var activeBreaker = GetCircuitBreaker(sessionId);
        if (activeBreaker != null && activeBreaker.TotalFailures > 0 && activeBreaker.ConsecutiveFailures > 0)
        {
            return new AgentResponse(
                Output: lastResponse.Output,
                IsCompleted: false,
                ToolCallName: lastResponse.ToolCallName)
            {
                VerificationStatus = activeBreaker.IsTripped ? VerificationStatus.CircuitBreakerTripped : VerificationStatus.Failed,
                VerificationResult = activeBreaker.LastResult
            };
        }

        bool postHooksConfigured = _verificationHooks.Count > 0;
        IAgentTool? postAutoTool = null;
        bool postAutoToolConfigured = !postHooksConfigured
            && _verificationOptions.AutoInvokeRegisteredVerificationTool
            && lastResponse.VerificationStatus != VerificationStatus.Passed
            && TryFindAutoVerificationTool(out postAutoTool);

        if (postHooksConfigured || postAutoToolConfigured)
        {
            var (verResult, state) = await RunPostCompletionVerificationAsync(
                sessionId,
                initialPrompt,
                promptBuilder.ToString(),
                lastResponse,
                maxSteps,
                postAutoTool,
                ct).ConfigureAwait(false);

            if (verResult.Passed)
            {
                _memory?.StoreTrace(new EpisodicTrace
                {
                    Content = $"Prompt: {initialPrompt}\nResult: {lastResponse.Output}",
                    BaseImportance = 1.5f
                });
                return lastResponse with
                {
                    IsCompleted = true,
                    VerificationStatus = VerificationStatus.Passed,
                    VerificationResult = verResult
                };
            }

            if (state.IsTripped)
            {
                if (_verificationOptions.ThrowOnCircuitBreakerTripped)
                {
                    throw new VerificationCircuitBreakerException(state, verResult);
                }

                if (_verificationOptions.HaltOnCircuitBreaker)
                {
                    return new AgentResponse(
                        Output: FormatCircuitBreakerOutput(verResult, state, lastResponse.Output),
                        IsCompleted: false,
                        ToolCallName: lastResponse.ToolCallName)
                    {
                        VerificationStatus = VerificationStatus.CircuitBreakerTripped,
                        VerificationResult = verResult
                    };
                }
            }

            return new AgentResponse(
                Output: lastResponse.Output,
                IsCompleted: false,
                ToolCallName: lastResponse.ToolCallName)
            {
                VerificationStatus = state.IsTripped ? VerificationStatus.CircuitBreakerTripped : VerificationStatus.Failed,
                VerificationResult = verResult
            };
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
                bool isVerificationTool = IsVerificationTool(tool!);
                PublishEvent("ToolInvoking", decision.ToolName);
                string args = decision.ToolArgumentsJson ?? "{}";

                string result;
                if (_verificationOptions.GuardToolExceptions || isVerificationTool)
                {
                    try
                    {
                        result = await tool!.ExecuteAsync(args, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                    {
                        string errorPayload = BuildToolErrorJson(decision.ToolName, ex);
                        PublishEvent("ToolFailed", $"{decision.ToolName}: {ex.Message}");

                        if (isVerificationTool)
                        {
                            var errorResult = VerificationResult.Error(
                                $"Verification tool '{decision.ToolName}' threw {ex.GetType().Name}: {ex.Message}",
                                decision.ToolName);
                            return HandleVerificationToolOutcome(
                                context.SessionId,
                                context.StepCount,
                                tool!,
                                errorResult,
                                errorPayload,
                                decision.IsCompleted,
                                toolThrew: true);
                        }

                        return new AgentResponse(
                            Output: errorPayload,
                            IsCompleted: false,
                            ToolCallName: decision.ToolName);
                    }
                }
                else
                {
                    result = await tool!.ExecuteAsync(args, ct).ConfigureAwait(false);
                }

                PublishEvent("ToolCompleted", $"{decision.ToolName} -> {result}");

                if (isVerificationTool)
                {
                    var parsedResult = VerificationResultParser.Parse(result, tool!.Name);
                    return HandleVerificationToolOutcome(
                        context.SessionId,
                        context.StepCount,
                        tool,
                        parsedResult,
                        result,
                        decision.IsCompleted,
                        toolThrew: false);
                }

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

    private AgentResponse HandleVerificationToolOutcome(
        string sessionId,
        int stepCount,
        IAgentTool tool,
        VerificationResult rawResult,
        string rawOutput,
        bool decisionIsCompleted,
        bool toolThrew)
    {
        using var verifyActivity = Telemetry.ActivitySource.StartActivity($"Verify {AgentId}");
        verifyActivity?.SetTag("agent.name", AgentId);
        verifyActivity?.SetTag("runtime.kind", "AgentExecutionEngine");
        verifyActivity?.SetTag("session.id", sessionId);
        verifyActivity?.SetTag("verification.attempt", stepCount);

        var metricTags = new[]
        {
            new KeyValuePair<string, object?>("agent", AgentId),
            new KeyValuePair<string, object?>("runtime", "AgentExecutionEngine")
        };
        Telemetry.VerificationAttemptsCounter.Add(1, metricTags);

        VerificationCircuitBreaker breaker;
        VerificationCircuitBreakerState state;
        if (tool is SelfVerificationTool { CircuitBreaker: not null } selfVerifyTool)
        {
            var externalBreaker = selfVerifyTool.CircuitBreaker;
            _sessionBreakers[sessionId] = externalBreaker;
            breaker = externalBreaker;
            if (toolThrew)
            {
                var preState = externalBreaker.GetState();
                EmitVerificationEvent("VerificationAttempted", sessionId, stepCount, rawResult, preState, tool.Name);
                state = externalBreaker.RecordFailure(rawResult);
            }
            else
            {
                state = externalBreaker.GetState();
                EmitVerificationEvent("VerificationAttempted", sessionId, stepCount, rawResult, state, tool.Name);
            }
        }
        else
        {
            breaker = GetOrCreateSessionBreaker(sessionId);
            var preState = breaker.GetState();
            EmitVerificationEvent("VerificationAttempted", sessionId, stepCount, rawResult, preState, tool.Name);
            state = rawResult.Passed
                ? breaker.RecordSuccess(rawResult)
                : breaker.RecordFailure(rawResult);
        }

        var recordedResult = state.LastResult ?? rawResult;
        if (recordedResult.Passed)
        {
            Telemetry.VerificationPassesCounter.Add(1, metricTags);
            verifyActivity?.SetTag("verification.passed", true);
            verifyActivity?.SetTag("verification.consecutive_failures", 0);
            verifyActivity?.SetTag("verification.circuit_breaker_tripped", false);
            verifyActivity?.SetTag("status", "passed");

            EmitVerificationEvent("VerificationPassed", sessionId, stepCount, recordedResult, state, tool.Name);
            return new AgentResponse(
                Output: rawOutput,
                IsCompleted: decisionIsCompleted,
                ToolCallName: tool.Name)
            {
                VerificationStatus = VerificationStatus.Passed,
                VerificationResult = recordedResult
            };
        }

        Telemetry.VerificationFailuresCounter.Add(1, metricTags);
        verifyActivity?.SetTag("verification.passed", false);
        verifyActivity?.SetTag("verification.consecutive_failures", state.ConsecutiveFailures);
        verifyActivity?.SetTag("verification.circuit_breaker_tripped", state.IsTripped);

        EmitVerificationEvent("VerificationFailed", sessionId, stepCount, recordedResult, state, tool.Name);

        if (state.IsTripped)
        {
            Telemetry.CircuitBreakerTripsCounter.Add(1, metricTags);
            verifyActivity?.SetTag("status", "circuit_breaker_tripped");
            EmitVerificationEvent("VerificationCircuitBreakerTripped", sessionId, stepCount, recordedResult, state, tool.Name);

            if (_verificationOptions.ThrowOnCircuitBreakerTripped)
            {
                throw new VerificationCircuitBreakerException(state, recordedResult);
            }

            if (_verificationOptions.HaltOnCircuitBreaker)
            {
                return new AgentResponse(
                    Output: FormatCircuitBreakerOutput(recordedResult, state, rawOutput),
                    IsCompleted: false,
                    ToolCallName: tool.Name)
                {
                    VerificationStatus = VerificationStatus.CircuitBreakerTripped,
                    VerificationResult = recordedResult
                };
            }
        }
        else
        {
            verifyActivity?.SetTag("status", "failed");
        }

        return new AgentResponse(
            Output: rawOutput,
            IsCompleted: false,
            ToolCallName: tool.Name)
        {
            VerificationStatus = state.IsTripped ? VerificationStatus.CircuitBreakerTripped : VerificationStatus.Failed,
            VerificationResult = recordedResult
        };
    }

    private async ValueTask<(VerificationResult Result, VerificationCircuitBreakerState State)> RunPostCompletionVerificationAsync(
        string sessionId,
        string originalPrompt,
        string currentPrompt,
        AgentResponse candidateResponse,
        int step,
        IAgentTool? autoTool,
        CancellationToken ct)
    {
        var breaker = autoTool is SelfVerificationTool { CircuitBreaker: not null } selfAutoTool
            ? selfAutoTool.CircuitBreaker
            : GetOrCreateSessionBreaker(sessionId);
        using var verifyActivity = Telemetry.ActivitySource.StartActivity($"Verify {AgentId}");
        verifyActivity?.SetTag("agent.name", AgentId);
        verifyActivity?.SetTag("runtime.kind", "AgentExecutionEngine");
        verifyActivity?.SetTag("session.id", sessionId);
        verifyActivity?.SetTag("verification.attempt", step);

        var metricTags = new[]
        {
            new KeyValuePair<string, object?>("agent", AgentId),
            new KeyValuePair<string, object?>("runtime", "AgentExecutionEngine")
        };
        Telemetry.VerificationAttemptsCounter.Add(1, metricTags);

        var preState = breaker.GetState();
        var preAttempt = new VerificationResult
        {
            Passed = false,
            Status = VerificationStatus.None,
            Summary = $"Running post-completion verification for step {step}.",
            VerifierName = _verificationHooks.Count > 0 ? _verificationHooks[0].Name : autoTool?.Name,
            ConsecutiveFailures = preState.ConsecutiveFailures
        };
        EmitVerificationEvent("VerificationAttempted", sessionId, step, preAttempt, preState, candidateResponse.ToolCallName);

        var context = new VerificationContext(
            AgentName: AgentId,
            OriginalPrompt: originalPrompt,
            CurrentPrompt: currentPrompt,
            CandidateOutput: candidateResponse.Output,
            TurnOrStep: step,
            SessionId: sessionId,
            ToolCallName: candidateResponse.ToolCallName,
            ConsecutiveFailures: breaker.ConsecutiveFailures);

        VerificationResult? finalResult = null;
        if (_verificationHooks.Count > 0)
        {
            foreach (var hook in _verificationHooks)
            {
                VerificationResult hookResult;
                try
                {
                    hookResult = await hook.VerifyAsync(context, ct).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(hookResult.VerifierName))
                    {
                        hookResult = hookResult with { VerifierName = hook.Name };
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    if (!_verificationOptions.TreatHookExceptionAsFailure)
                    {
                        throw;
                    }
                    hookResult = VerificationResult.Error(ex.Message, hook.Name);
                }

                finalResult = hookResult;
                if (!hookResult.Passed)
                {
                    break;
                }
            }
        }
        else if (autoTool != null)
        {
            if (autoTool is SelfVerificationTool { CircuitBreaker: not null } selfTool)
            {
                // Auto-invoking a tool that carries its own breaker should update that breaker directly.
                string toolArgs = ToolVerificationHook.BuildToolArgumentsJson(context);
                VerificationResult autoResult;
                bool threw = false;
                try
                {
                    string raw = await autoTool.ExecuteAsync(toolArgs, ct).ConfigureAwait(false);
                    autoResult = VerificationResultParser.Parse(raw, autoTool.Name);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    threw = true;
                    if (!_verificationOptions.TreatHookExceptionAsFailure)
                    {
                        throw;
                    }
                    autoResult = VerificationResult.Error(ex.Message, autoTool.Name);
                }

                _sessionBreakers[sessionId] = selfTool.CircuitBreaker;
                var extState = threw
                    ? selfTool.CircuitBreaker.RecordFailure(autoResult)
                    : selfTool.CircuitBreaker.GetState();
                var extResult = extState.LastResult ?? autoResult;
                RecordVerificationTelemetryAndEvents(verifyActivity, metricTags, sessionId, step, extResult, extState, autoTool.Name);
                return (extResult, extState);
            }
            else
            {
                string toolArgs = ToolVerificationHook.BuildToolArgumentsJson(context);
                try
                {
                    string raw = await autoTool.ExecuteAsync(toolArgs, ct).ConfigureAwait(false);
                    finalResult = VerificationResultParser.Parse(raw, autoTool.Name);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    if (!_verificationOptions.TreatHookExceptionAsFailure)
                    {
                        throw;
                    }
                    finalResult = VerificationResult.Error(ex.Message, autoTool.Name);
                }
            }
        }

        finalResult ??= VerificationResult.Pass("Verification passed.", AgentId);
        var state = finalResult.Passed
            ? breaker.RecordSuccess(finalResult)
            : breaker.RecordFailure(finalResult);
        var recordedResult = state.LastResult ?? finalResult;

        RecordVerificationTelemetryAndEvents(verifyActivity, metricTags, sessionId, step, recordedResult, state, candidateResponse.ToolCallName);
        return (recordedResult, state);
    }

    private void RecordVerificationTelemetryAndEvents(
        Activity? verifyActivity,
        KeyValuePair<string, object?>[] metricTags,
        string sessionId,
        int step,
        VerificationResult recordedResult,
        VerificationCircuitBreakerState state,
        string? toolCallName)
    {
        if (recordedResult.Passed)
        {
            Telemetry.VerificationPassesCounter.Add(1, metricTags);
            verifyActivity?.SetTag("verification.passed", true);
            verifyActivity?.SetTag("verification.consecutive_failures", 0);
            verifyActivity?.SetTag("verification.circuit_breaker_tripped", false);
            verifyActivity?.SetTag("status", "passed");
            EmitVerificationEvent("VerificationPassed", sessionId, step, recordedResult, state, toolCallName);
            return;
        }

        Telemetry.VerificationFailuresCounter.Add(1, metricTags);
        verifyActivity?.SetTag("verification.passed", false);
        verifyActivity?.SetTag("verification.consecutive_failures", state.ConsecutiveFailures);
        verifyActivity?.SetTag("verification.circuit_breaker_tripped", state.IsTripped);
        EmitVerificationEvent("VerificationFailed", sessionId, step, recordedResult, state, toolCallName);

        if (state.IsTripped)
        {
            Telemetry.CircuitBreakerTripsCounter.Add(1, metricTags);
            verifyActivity?.SetTag("status", "circuit_breaker_tripped");
            EmitVerificationEvent("VerificationCircuitBreakerTripped", sessionId, step, recordedResult, state, toolCallName);
        }
        else
        {
            verifyActivity?.SetTag("status", "failed");
        }
    }

    private VerificationCircuitBreaker GetOrCreateSessionBreaker(string sessionId)
        => _sessionBreakers.GetOrAdd(sessionId, _ => new VerificationCircuitBreaker(_verificationOptions.MaxConsecutiveVerificationRetries));

    private bool HasVerificationConfigured()
        => _verificationHooks.Count > 0
            || _verificationToolNames.Count > 0
            || _verificationOptions.VerificationToolNames.Count > 0
            || _verificationOptions.AutoInvokeRegisteredVerificationTool;

    private bool IsVerificationTool(IAgentTool tool)
        => tool is IVerificationTool
            || _verificationToolNames.Contains(tool.Name)
            || _verificationOptions.VerificationToolNames.Contains(tool.Name)
            || string.Equals(tool.Name, _verificationOptions.VerificationToolName, StringComparison.OrdinalIgnoreCase);

    private bool TryFindAutoVerificationTool(out IAgentTool? tool)
    {
        if (_tools.TryGetTool(_verificationOptions.VerificationToolName, out tool))
        {
            return true;
        }
        foreach (var candidateName in _verificationToolNames)
        {
            if (_tools.TryGetTool(candidateName, out tool))
            {
                return true;
            }
        }
        foreach (var candidateName in _verificationOptions.VerificationToolNames)
        {
            if (_tools.TryGetTool(candidateName, out tool))
            {
                return true;
            }
        }
        foreach (var registered in _tools.Tools)
        {
            if (registered is IVerificationTool)
            {
                tool = registered;
                return true;
            }
        }
        tool = null;
        return false;
    }

    private void AppendVerificationFailureFeedback(
        StringBuilder promptBuilder,
        int step,
        VerificationResult result,
        VerificationCircuitBreakerState state)
    {
        if (_verificationOptions.FeedbackFormatter != null)
        {
            promptBuilder.Append('\n').Append(_verificationOptions.FeedbackFormatter(result, state));
            return;
        }

        var payload = new VerificationFeedbackPayload(
            TurnOrStep: step,
            ConsecutiveFailures: state.ConsecutiveFailures,
            MaxConsecutiveRetries: state.MaxConsecutiveRetries,
            VerifierName: result.VerifierName,
            FailureReason: result.FailureReason ?? result.Summary,
            SuggestedFix: result.SuggestedFix,
            Issues: result.Issues is { Count: > 0 } ? result.Issues : null);

        string json = JsonSerializer.Serialize(payload, AdkJsonContext.Default.VerificationFeedbackPayload);
        promptBuilder.Append('\n')
            .Append("<|verification_failed|>")
            .Append(json)
            .Append('\n')
            .Append("Fix the verification errors and retry.");
    }

    private string FormatCircuitBreakerOutput(
        VerificationResult result,
        VerificationCircuitBreakerState state,
        string lastOutput)
    {
        if (_verificationOptions.CircuitBreakerOutputFormatter != null)
        {
            return _verificationOptions.CircuitBreakerOutputFormatter(result, state, lastOutput);
        }

        return result.ToJson();
    }

    private void EmitVerificationEvent(
        string eventType,
        string sessionId,
        int step,
        VerificationResult result,
        VerificationCircuitBreakerState state,
        string? toolCallName)
    {
        if (EventPublished == null) return;

        var payload = new VerificationEventPayload(
            AgentName: AgentId,
            SessionId: sessionId,
            TurnOrStep: step,
            Status: result.Status,
            Passed: result.Passed,
            ConsecutiveFailures: state.ConsecutiveFailures,
            MaxConsecutiveRetries: state.MaxConsecutiveRetries,
            CircuitTripped: state.IsTripped,
            VerifierName: result.VerifierName,
            Summary: result.Summary,
            FailureReason: result.FailureReason,
            ToolCallName: toolCallName);

        string json = JsonSerializer.Serialize(payload, AdkJsonContext.Default.VerificationEventPayload);
        PublishEvent(eventType, json);
    }

    private static string BuildToolErrorJson(string toolName, Exception ex)
    {
        var payload = new ToolErrorPayload(
            Status: "error",
            Tool: toolName,
            ErrorType: ex.GetType().Name,
            Message: ex.Message);
        return JsonSerializer.Serialize(payload, AdkJsonContext.Default.ToolErrorPayload);
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

    public static AgentStepDecision ParseModelOrStructuredDirective(string? rawInput, bool isRawModelOutput = false)
    {
        if (string.IsNullOrWhiteSpace(rawInput))
        {
            return new AgentStepDecision(string.Empty, IsCompleted: true);
        }

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
