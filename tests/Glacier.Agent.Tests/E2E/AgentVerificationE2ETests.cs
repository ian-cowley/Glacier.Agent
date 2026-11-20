using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Glacier.Agent.Runtime;
using Glacier.Agent.Serialization;
using Glacier.Agent.Tools;
using Glacier.Agent.Workflows;
using Xunit;

namespace Glacier.Agent.Tests.E2E;

[Trait("Suite", "UpgradesE2E")]
[Trait("Tier", "Tier1")]
public sealed class AgentVerificationE2ETests
{
    [Fact]
    public async Task T1_F14_01_LoopAgent_VerificationHookAndTool_PassOnFirstTry()
    {
        var hook = new ScriptedVerificationHook("first-try-hook", (ctx, _) =>
            VerificationResult.Pass($"Verified '{ctx.CandidateOutput}'", "first-try-hook"));

        var member = new ScriptedWorkflowAgent("drafter", (_, _) => "{\"answer\":\"42\"}");
        var options = new VerificationOptions
        {
            MaxConsecutiveVerificationRetries = 3,
            StopOnVerificationPass = true
        };
        var loop = new LoopAgent("loop-verifier", "Tier1 loop check", member, options, hook, maxTurns: 5);
        var events = new List<AgentEvent>();
        loop.EventPublished += e => events.Add(e);

        using var llm = new StubLlmService();
        string output = await loop.RunAsync("Compute answer", llm, "t1-f14-01-hook");

        Assert.Equal("{\"answer\":\"42\"}", output);
        Assert.Equal(1, member.InvocationCount);
        Assert.Equal(1, hook.CallCount);
        Assert.NotNull(loop.LastVerificationResult);
        Assert.True(loop.LastVerificationResult!.Passed);
        Assert.Equal(VerificationStatus.Passed, loop.LastVerificationResult.Status);
        Assert.NotNull(loop.LastVerificationState);
        Assert.False(loop.LastVerificationState!.IsTripped);
        Assert.Equal(0, loop.LastVerificationState.ConsecutiveFailures);
        Assert.Equal(1, loop.LastVerificationState.TotalPasses);
        Assert.Equal(0, loop.LastVerificationState.TotalFailures);
        Assert.Single(loop.VerificationHistory);
        Assert.Contains(events, e => e.EventType == "VerificationAttempted");
        Assert.Contains(events, e => e.EventType == "VerificationPassed");
        Assert.DoesNotContain(events, e => e.EventType == "VerificationCircuitBreakerTripped");

        // Also validate structured IAgentTool wiring via LoopAgent.UseVerificationTool
        var toolAgent = new ScriptedWorkflowAgent("tool-drafter", (_, _) => "{\"status\":\"ok\",\"count\":1}");
        var toolLoop = new LoopAgent("loop-tool-verifier", "Tier1 tool check", toolAgent, maxTurns: 4);
        var selfVerifyTool = new SelfVerificationTool(
            new ScriptedVerificationHook("tool-hook", (ctx, _) =>
                ctx.CandidateOutput.Contains("\"status\":\"ok\"", StringComparison.Ordinal)
                    ? VerificationResult.Pass("Tool check passed", "tool-hook")
                    : VerificationResult.Fail("Missing ok status")));
        toolLoop.UseVerificationTool((IAgentTool)selfVerifyTool);

        string toolOutput = await toolLoop.RunAsync("Produce status JSON", llm, "t1-f14-01-tool");
        Assert.Equal("{\"status\":\"ok\",\"count\":1}", toolOutput);
        Assert.Equal(1, toolAgent.InvocationCount);
        Assert.True(toolLoop.LastVerificationResult!.Passed);
        Assert.False(toolLoop.LastVerificationState!.IsTripped);
    }

    [Fact]
    public async Task T1_F14_02_AgentExecutionEngine_VerificationHook_PassesOnFirstTry()
    {
        int plannerCalls = 0;
        using var engine = new AgentExecutionEngine("engine-t1-02", (ctx, _, _) =>
        {
            plannerCalls++;
            return ValueTask.FromResult(new AgentStepDecision("verified-artifact", IsCompleted: true));
        });

        var hook = new ScriptedVerificationHook("engine-hook", (ctx, _) =>
            VerificationResult.Pass($"Artifact '{ctx.CandidateOutput}' valid", "engine-hook"));

        engine.ConfigureVerification(new VerificationOptions { MaxConsecutiveVerificationRetries = 3 });
        engine.AttachVerificationHook(hook);

        var events = new List<AgentEvent>();
        engine.EventPublished += e => events.Add(e);

        const string sessionId = "t1-f14-02-session";
        var response = await engine.ExecuteLoopAsync(sessionId, "Generate verified artifact", maxSteps: 5);

        Assert.True(response.IsCompleted);
        Assert.Equal("verified-artifact", response.Output);
        Assert.Equal(VerificationStatus.Passed, response.VerificationStatus);
        Assert.False(response.IsVerificationCircuitBreakerTripped);
        Assert.NotNull(response.VerificationResult);
        Assert.True(response.VerificationResult!.Passed);
        Assert.Equal(1, plannerCalls);
        Assert.Equal(1, hook.CallCount);

        var breaker = engine.GetCircuitBreaker(sessionId);
        Assert.NotNull(breaker);
        Assert.False(breaker!.IsTripped);
        Assert.Equal(0, breaker.ConsecutiveFailures);
        Assert.Equal(1, breaker.TotalPasses);
        Assert.Equal(0, breaker.TotalFailures);
        Assert.Contains(events, e => e.EventType == "VerificationPassed");
    }

    [Fact]
    public async Task T1_F14_03_FailThenRecover_WithinMaxConsecutiveRetries_InLoopAgentAndEngine()
    {
        // 1. LoopAgent fail-twice-then-recover on attempt 3
        var loopMember = new ScriptedWorkflowAgent("loop-recover", (_, turn) => turn switch
        {
            1 => "draft-v1",
            2 => "draft-v2",
            _ => "final-valid-v3"
        });
        var loopHook = new ScriptedVerificationHook("recover-hook", (ctx, attempt) =>
            attempt < 3
                ? VerificationResult.Fail($"Attempt {attempt} incomplete", suggestedFix: "Emit final-valid-v3")
                : VerificationResult.Pass("Recovered on attempt 3", "recover-hook"));

        var loopOptions = new VerificationOptions
        {
            MaxConsecutiveVerificationRetries = 3,
            StopOnVerificationPass = true,
            AppendStructuredFeedbackToPrompt = true
        };
        var loop = new LoopAgent("loop-recover", "Recovery test", loopMember, loopOptions, loopHook, maxTurns: 6);
        using var llm = new StubLlmService();

        string loopResult = await loop.RunAsync("Write valid output", llm, "t1-f14-03-loop");

        Assert.Equal("final-valid-v3", loopResult);
        Assert.Equal(3, loopMember.InvocationCount);
        Assert.Equal(3, loopHook.CallCount);
        Assert.Contains("<|verification_failed|>", loopMember.ReceivedPrompts[1]);
        Assert.Contains("<|verification_failed|>", loopMember.ReceivedPrompts[2]);
        Assert.True(loop.LastVerificationResult!.Passed);
        Assert.False(loop.LastVerificationState!.IsTripped);
        Assert.Equal(0, loop.LastVerificationState.ConsecutiveFailures);
        Assert.Equal(2, loop.LastVerificationState.TotalFailures);
        Assert.Equal(1, loop.LastVerificationState.TotalPasses);

        // 2. AgentExecutionEngine fail-twice-then-recover on step 3
        var capturedPrompts = new List<string>();
        using var engine = new AgentExecutionEngine("engine-recover", (ctx, _, _) =>
        {
            capturedPrompts.Add(ctx.Prompt);
            string candidate = ctx.StepCount switch
            {
                1 => "bad-1",
                2 => "bad-2",
                _ => "good-3"
            };
            return ValueTask.FromResult(new AgentStepDecision(candidate, IsCompleted: true));
        });

        engine.ConfigureVerification(new VerificationOptions
        {
            MaxConsecutiveVerificationRetries = 3,
            AppendStructuredFeedbackToPrompt = true
        });
        engine.AttachVerificationHook("engine-recover-hook", (ctx, _) =>
            ValueTask.FromResult(ctx.CandidateOutput == "good-3"
                ? VerificationResult.Pass("Step 3 passed", "engine-recover-hook")
                : VerificationResult.Fail($"Rejected '{ctx.CandidateOutput}'", suggestedFix: "Output good-3")));

        const string sessionId = "t1-f14-03-engine";
        var engineResult = await engine.ExecuteLoopAsync(sessionId, "Produce good-3", maxSteps: 6);

        Assert.True(engineResult.IsCompleted);
        Assert.Equal("good-3", engineResult.Output);
        Assert.Equal(VerificationStatus.Passed, engineResult.VerificationStatus);
        Assert.False(engineResult.IsVerificationCircuitBreakerTripped);
        Assert.Equal(3, capturedPrompts.Count);
        Assert.Contains("<|verification_failed|>", capturedPrompts[1]);
        Assert.Contains("<|verification_failed|>", capturedPrompts[2]);

        var breaker = engine.GetCircuitBreaker(sessionId)!;
        Assert.False(breaker.IsTripped);
        Assert.Equal(0, breaker.ConsecutiveFailures);
        Assert.Equal(2, breaker.TotalFailures);
        Assert.Equal(1, breaker.TotalPasses);
    }

    [Fact]
    public async Task T1_F14_04_ConsecutiveFailures_TripCircuitBreakerCleanly_InLoopAgentAndEngine()
    {
        // MaxConsecutiveVerificationRetries = 2 allows 2 retries (3 consecutive failures trip the breaker).
        var loopMember = new ScriptedWorkflowAgent("loop-stuck", (_, turn) => $"stuck-output-{turn}");
        var loopHook = new ScriptedVerificationHook("always-fail", (ctx, attempt) =>
            VerificationResult.Fail($"Persistent failure #{attempt}", suggestedFix: "Cannot satisfy constraint"));

        var loopOptions = new VerificationOptions
        {
            MaxConsecutiveVerificationRetries = 2,
            HaltOnCircuitBreaker = true
        };
        var loop = new LoopAgent("loop-breaker", "Breaker trip test", loopMember, loopOptions, loopHook, maxTurns: 10);
        var loopEvents = new List<AgentEvent>();
        loop.EventPublished += e => loopEvents.Add(e);

        using var llm = new StubLlmService();
        string loopHalted = await loop.RunAsync("Hopeless task", llm, "t1-f14-04-loop");

        Assert.Equal(3, loopMember.InvocationCount);
        Assert.Equal(3, loopHook.CallCount);
        Assert.NotNull(loop.LastVerificationState);
        Assert.True(loop.LastVerificationState!.IsTripped);
        Assert.Equal(CircuitBreakerStatus.Open, loop.LastVerificationState.Status);
        Assert.Equal(3, loop.LastVerificationState.ConsecutiveFailures);
        Assert.NotNull(loop.LastVerificationResult);
        Assert.True(loop.LastVerificationResult!.CircuitTripped);
        Assert.Equal(VerificationStatus.CircuitBreakerTripped, loop.LastVerificationResult.Status);
        Assert.Single(loopEvents, e => e.EventType == "VerificationCircuitBreakerTripped");

        var parsedHalted = JsonSerializer.Deserialize(loopHalted, AdkJsonContext.Default.VerificationResult);
        Assert.NotNull(parsedHalted);
        Assert.True(parsedHalted!.CircuitTripped);
        Assert.Equal(VerificationStatus.CircuitBreakerTripped, parsedHalted.Status);

        // Engine consecutive failures also halt cleanly before maxSteps=10
        int engineSteps = 0;
        using var engine = new AgentExecutionEngine("engine-breaker", (ctx, _, _) =>
        {
            engineSteps++;
            return ValueTask.FromResult(new AgentStepDecision($"broken-{ctx.StepCount}", IsCompleted: true));
        });
        engine.ConfigureVerification(new VerificationOptions
        {
            MaxConsecutiveVerificationRetries = 2,
            HaltOnCircuitBreaker = true
        });
        engine.AttachVerificationHook("always-fail-engine", (ctx, _) =>
            ValueTask.FromResult(VerificationResult.Fail($"Engine failure at step {ctx.TurnOrStep}")));

        var engineEvents = new List<AgentEvent>();
        engine.EventPublished += e => engineEvents.Add(e);

        const string sessionId = "t1-f14-04-engine";
        var engineResponse = await engine.ExecuteLoopAsync(sessionId, "Hopeless engine task", maxSteps: 10);

        Assert.Equal(3, engineSteps);
        Assert.False(engineResponse.IsCompleted);
        Assert.True(engineResponse.IsVerificationCircuitBreakerTripped);
        Assert.Equal(VerificationStatus.CircuitBreakerTripped, engineResponse.VerificationStatus);
        Assert.NotNull(engineResponse.VerificationResult);
        Assert.True(engineResponse.VerificationResult!.CircuitTripped);
        Assert.Equal(3, engineResponse.VerificationResult.ConsecutiveFailures);
        Assert.Single(engineEvents, e => e.EventType == "VerificationCircuitBreakerTripped");

        var engineBreaker = engine.GetCircuitBreaker(sessionId)!;
        Assert.True(engineBreaker.IsTripped);
        Assert.Equal(3, engineBreaker.ConsecutiveFailures);
    }

    [Fact]
    public void T1_F14_05_AdkJsonContext_AotRoundTrip_VerificationAndBreakerTelemetry()
    {
        var options = new VerificationOptions
        {
            MaxConsecutiveVerificationRetries = 4,
            StopOnVerificationPass = false,
            RequireVerificationForStopCondition = true,
            TreatHookExceptionAsFailure = true,
            GuardToolExceptions = true,
            HaltOnCircuitBreaker = true,
            ThrowOnCircuitBreakerTripped = false,
            AppendStructuredFeedbackToPrompt = true,
            AutoInvokeRegisteredVerificationTool = true,
            VerificationToolName = "custom_verify"
        };
        options.VerificationToolNames.Add("lint_verify");

        var issues = new List<VerificationIssue>
        {
            new("missing_field", "Field 'summary' is required.", "summary", "Add a summary property.")
        };
        var failedResult = VerificationResult.Fail(
            "Schema validation failed.",
            issues,
            suggestedFix: "Include required fields.",
            verifierName: options.VerificationToolName);

        var breaker = new VerificationCircuitBreaker(options.MaxConsecutiveVerificationRetries);
        breaker.RecordFailure(failedResult);
        var breakerState = breaker.GetState();

        string resultJson = failedResult.ToJson();
        var roundTrippedResult = JsonSerializer.Deserialize(resultJson, AdkJsonContext.Default.VerificationResult);
        Assert.NotNull(roundTrippedResult);
        Assert.False(roundTrippedResult!.Passed);
        Assert.Equal(VerificationStatus.Failed, roundTrippedResult.Status);
        Assert.Equal("Schema validation failed.", roundTrippedResult.FailureReason);
        Assert.Equal(options.VerificationToolName, roundTrippedResult.VerifierName);
        Assert.Single(roundTrippedResult.Issues);
        Assert.Equal("missing_field", roundTrippedResult.Issues[0].Code);

        string indentedResultJson = JsonSerializer.Serialize(failedResult, AdkIndentedJsonContext.Default.VerificationResult);
        var roundTrippedIndented = JsonSerializer.Deserialize(indentedResultJson, AdkIndentedJsonContext.Default.VerificationResult);
        Assert.NotNull(roundTrippedIndented);
        Assert.Equal(failedResult.FailureReason, roundTrippedIndented!.FailureReason);

        string stateJson = JsonSerializer.Serialize(breakerState, AdkJsonContext.Default.VerificationCircuitBreakerState);
        var roundTrippedState = JsonSerializer.Deserialize(stateJson, AdkJsonContext.Default.VerificationCircuitBreakerState);
        Assert.NotNull(roundTrippedState);
        Assert.Equal(CircuitBreakerStatus.Closed, roundTrippedState!.Status);
        Assert.Equal(1, roundTrippedState.ConsecutiveFailures);
        Assert.Equal(4, roundTrippedState.MaxConsecutiveRetries);
        Assert.Equal(1, roundTrippedState.TotalFailures);
        Assert.False(roundTrippedState.IsTripped);

        var eventPayload = new VerificationEventPayload(
            AgentName: "agent-aot",
            SessionId: "sess-aot",
            TurnOrStep: 1,
            Status: VerificationStatus.Failed,
            Passed: false,
            ConsecutiveFailures: breakerState.ConsecutiveFailures,
            MaxConsecutiveRetries: options.MaxConsecutiveVerificationRetries,
            CircuitTripped: breakerState.IsTripped,
            VerifierName: options.VerificationToolName,
            Summary: failedResult.Summary,
            FailureReason: failedResult.FailureReason,
            ToolCallName: options.VerificationToolName);
        string eventJson = JsonSerializer.Serialize(eventPayload, AdkJsonContext.Default.VerificationEventPayload);
        var roundTrippedEvent = JsonSerializer.Deserialize(eventJson, AdkJsonContext.Default.VerificationEventPayload);
        Assert.Equal(eventPayload, roundTrippedEvent);

        var feedbackPayload = new VerificationFeedbackPayload(
            TurnOrStep: 1,
            ConsecutiveFailures: 1,
            MaxConsecutiveRetries: options.MaxConsecutiveVerificationRetries,
            VerifierName: options.VerificationToolName,
            FailureReason: failedResult.FailureReason!,
            SuggestedFix: failedResult.SuggestedFix,
            Issues: issues);
        string feedbackJson = JsonSerializer.Serialize(feedbackPayload, AdkJsonContext.Default.VerificationFeedbackPayload);
        var roundTrippedFeedback = JsonSerializer.Deserialize(feedbackJson, AdkJsonContext.Default.VerificationFeedbackPayload);
        Assert.NotNull(roundTrippedFeedback);
        Assert.Equal(feedbackPayload.FailureReason, roundTrippedFeedback!.FailureReason);
        Assert.Single(roundTrippedFeedback.Issues!);

        var toolArgs = new SelfVerificationToolArgs
        {
            Output = "{\"summary\":\"ok\"}",
            Prompt = "Check output",
            RequireValidJson = true,
            RequiredFields = new List<string> { "summary" },
            RequiredSubstrings = new List<string> { "ok" }
        };
        string toolArgsJson = JsonSerializer.Serialize(toolArgs, AdkJsonContext.Default.SelfVerificationToolArgs);
        var roundTrippedArgs = JsonSerializer.Deserialize(toolArgsJson, AdkJsonContext.Default.SelfVerificationToolArgs);
        Assert.NotNull(roundTrippedArgs);
        Assert.True(roundTrippedArgs!.RequireValidJson);
        Assert.Contains("summary", roundTrippedArgs.RequiredFields!);

        var toolNamesJson = JsonSerializer.Serialize(new List<string>(options.VerificationToolNames), AdkJsonContext.Default.ListString);
        var roundTrippedToolNames = JsonSerializer.Deserialize(toolNamesJson, AdkJsonContext.Default.ListString);
        Assert.Contains("lint_verify", roundTrippedToolNames!);

        var errorPayload = new ToolErrorPayload("error", "custom_verify", nameof(InvalidOperationException), "boom");
        string errJson = JsonSerializer.Serialize(errorPayload, AdkJsonContext.Default.ToolErrorPayload);
        Assert.Equal(errorPayload, JsonSerializer.Deserialize(errJson, AdkJsonContext.Default.ToolErrorPayload));
    }
}
