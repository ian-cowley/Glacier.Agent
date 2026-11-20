using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Glacier.Agent.Llm;
using Glacier.Agent.Runtime;
using Glacier.Agent.Tools;
using Glacier.Agent.Workflows;
using Xunit;

namespace Glacier.Agent.Tests.E2E;

[Trait("Suite", "UpgradesE2E")]
[Trait("Tier", "Tier2")]
public sealed class VerificationCircuitBreakerE2ETests
{
    [Fact]
    public async Task T2_F14_01_MaxConsecutiveVerificationRetries_Zero_TripsOnFirstFailure()
    {
        var loopMember = new ScriptedWorkflowAgent("zero-retry-agent", (_, _) => "bad-draft");
        var loopHook = new ScriptedVerificationHook("strict-hook", (_, _) =>
            VerificationResult.Fail("Immediate rejection"));

        var loop = new LoopAgent(
            "zero-retry-loop",
            "0 retries boundary",
            loopMember,
            new VerificationOptions { MaxConsecutiveVerificationRetries = 0, HaltOnCircuitBreaker = true },
            loopHook,
            maxTurns: 5);

        using var llm = new StubLlmService();
        await loop.RunAsync("Run once", llm, "t2-f14-01-loop");

        Assert.Equal(1, loopMember.InvocationCount);
        Assert.Equal(1, loopHook.CallCount);
        Assert.True(loop.LastVerificationState!.IsTripped);
        Assert.Equal(1, loop.LastVerificationState.ConsecutiveFailures);
        Assert.Equal(1, loop.LastVerificationState.TotalFailures);
        Assert.Equal(0, loop.LastVerificationState.MaxConsecutiveRetries);

        int engineAttempts = 0;
        using var engine = new AgentExecutionEngine("zero-retry-engine", (_, _, _) =>
        {
            engineAttempts++;
            return ValueTask.FromResult(new AgentStepDecision("bad-engine-draft", IsCompleted: true));
        });
        engine.ConfigureVerification(new VerificationOptions { MaxConsecutiveVerificationRetries = 0, HaltOnCircuitBreaker = true });
        engine.AttachVerificationHook("strict-engine-hook", (_, _) =>
            ValueTask.FromResult(VerificationResult.Fail("Engine immediate rejection")));

        var response = await engine.ExecuteLoopAsync("t2-f14-01-engine", "Run once", maxSteps: 5);
        Assert.Equal(1, engineAttempts);
        Assert.False(response.IsCompleted);
        Assert.True(response.IsVerificationCircuitBreakerTripped);
        Assert.Equal(1, response.VerificationResult!.ConsecutiveFailures);
    }

    [Fact]
    public async Task T2_F14_02_MaxConsecutiveVerificationRetries_One_PassAndTripBoundaries()
    {
        // Case A: MaxConsecutiveVerificationRetries = 1 -> Fail on 1, Pass on 2 -> succeeds cleanly
        var passMember = new ScriptedWorkflowAgent("retry-1-pass", (_, turn) => turn == 1 ? "v1" : "v2-fixed");
        var passHook = new ScriptedVerificationHook("retry-1-hook", (ctx, _) =>
            ctx.CandidateOutput == "v2-fixed"
                ? VerificationResult.Pass("Fixed on retry 1")
                : VerificationResult.Fail("Initial failure"));

        var passLoop = new LoopAgent(
            "retry-1-pass-loop",
            "1 retry pass",
            passMember,
            new VerificationOptions { MaxConsecutiveVerificationRetries = 1, StopOnVerificationPass = true },
            passHook,
            maxTurns: 6);

        using var llm = new StubLlmService();
        string passed = await passLoop.RunAsync("Fix within 1 retry", llm, "t2-f14-02-pass");
        Assert.Equal("v2-fixed", passed);
        Assert.Equal(2, passMember.InvocationCount);
        Assert.False(passLoop.LastVerificationState!.IsTripped);
        Assert.Equal(0, passLoop.LastVerificationState.ConsecutiveFailures);

        // Case B: MaxConsecutiveVerificationRetries = 1 -> Fail on 1, Fail on 2 -> trips cleanly at attempt 2
        var tripMember = new ScriptedWorkflowAgent("retry-1-trip", (_, turn) => $"still-broken-{turn}");
        var tripHook = new ScriptedVerificationHook("retry-1-fail-hook", (_, attempt) =>
            VerificationResult.Fail($"Fail #{attempt}"));

        var tripLoop = new LoopAgent(
            "retry-1-trip-loop",
            "1 retry trip",
            tripMember,
            new VerificationOptions { MaxConsecutiveVerificationRetries = 1, HaltOnCircuitBreaker = true },
            tripHook,
            maxTurns: 8);

        await tripLoop.RunAsync("Cannot fix", llm, "t2-f14-02-trip");
        Assert.Equal(2, tripMember.InvocationCount);
        Assert.True(tripLoop.LastVerificationState!.IsTripped);
        Assert.Equal(2, tripLoop.LastVerificationState.ConsecutiveFailures);

        // Engine parity for Case B
        int engineCalls = 0;
        using var engine = new AgentExecutionEngine("retry-1-engine", (_, _, _) =>
        {
            engineCalls++;
            return ValueTask.FromResult(new AgentStepDecision("broken", IsCompleted: true));
        });
        engine.ConfigureVerification(new VerificationOptions { MaxConsecutiveVerificationRetries = 1, HaltOnCircuitBreaker = true });
        engine.AttachVerificationHook("engine-fail", (_, _) => ValueTask.FromResult(VerificationResult.Fail("Still broken")));

        var resp = await engine.ExecuteLoopAsync("t2-f14-02-engine", "Cannot fix", maxSteps: 8);
        Assert.Equal(2, engineCalls);
        Assert.True(resp.IsVerificationCircuitBreakerTripped);
        Assert.Equal(2, resp.VerificationResult!.ConsecutiveFailures);
    }

    [Fact]
    public async Task T2_F14_03_AlternatingFailPass_ResetsConsecutiveCounterWithoutTripping()
    {
        // H4: MaxConsecutiveVerificationRetries = 2; sequence Fail, Pass, Fail, Pass, Fail, Pass -> 3 total failures, max consecutive = 1 < 2.
        var loopMember = new ScriptedWorkflowAgent("alternating-agent", (_, turn) => $"turn-{turn}");
        var loopHook = new ScriptedVerificationHook("alternating-hook", (_, attempt) =>
            attempt % 2 == 1
                ? VerificationResult.Fail($"Odd attempt {attempt} failed")
                : VerificationResult.Pass($"Even attempt {attempt} passed"));

        var loop = new LoopAgent(
            "alternating-loop",
            "Resets consecutive counter",
            loopMember,
            new VerificationOptions
            {
                MaxConsecutiveVerificationRetries = 2,
                StopOnVerificationPass = false
            },
            loopHook,
            maxTurns: 6,
            stopCondition: output => output == "turn-6");

        using var llm = new StubLlmService();
        string finalOutput = await loop.RunAsync("Run 6 alternating turns", llm, "t2-f14-03-loop");

        Assert.Equal("turn-6", finalOutput);
        Assert.Equal(6, loopHook.CallCount);
        Assert.NotNull(loop.LastVerificationState);
        Assert.False(loop.LastVerificationState!.IsTripped);
        Assert.Equal(0, loop.LastVerificationState.ConsecutiveFailures);
        Assert.Equal(3, loop.LastVerificationState.TotalFailures);
        Assert.Equal(3, loop.LastVerificationState.TotalPasses);

        // Verify AgentExecutionEngine across 3 Fail->Pass tool/verification cycles in one session
        using var engine = new AgentExecutionEngine("alternating-engine", (ctx, _, _) =>
        {
            bool shouldPass = ctx.StepCount % 2 == 0;
            string payload = shouldPass
                ? VerificationResult.Pass($"Pass step {ctx.StepCount}", "alt-tool").ToJson()
                : VerificationResult.Fail($"Fail step {ctx.StepCount}", verifierName: "alt-tool").ToJson();

            return ValueTask.FromResult(new AgentStepDecision(
                Output: string.Empty,
                IsCompleted: ctx.StepCount >= 6,
                ToolName: "alt-tool",
                ToolArgumentsJson: payload));
        });

        engine.ConfigureVerification(new VerificationOptions { MaxConsecutiveVerificationRetries = 2 });
        engine.RegisterVerificationTool(new ScriptedVerificationAgentTool("alt-tool", (args, _) => args));

        const string sessionId = "t2-f14-03-engine";
        var finalResp = await engine.ExecuteLoopAsync(sessionId, "Run alternating steps", maxSteps: 6);

        Assert.True(finalResp.IsCompleted);
        Assert.False(finalResp.IsVerificationCircuitBreakerTripped);
        Assert.Equal(VerificationStatus.Passed, finalResp.VerificationStatus);

        var breaker = engine.GetCircuitBreaker(sessionId)!;
        Assert.False(breaker.IsTripped);
        Assert.Equal(0, breaker.ConsecutiveFailures);
        Assert.Equal(3, breaker.TotalFailures);
        Assert.Equal(3, breaker.TotalPasses);
    }

    [Fact]
    public async Task T2_F14_04_VerificationToolAndHook_ExceptionsGuarded_AndCancellationPreserved()
    {
        // 1. Throwing IVerificationHook inside LoopAgent converts exception to Error -> trips breaker cleanly
        var loopMember = new ScriptedWorkflowAgent("loop-ex", (_, turn) => $"candidate-{turn}");
        var throwingHook = new ScriptedVerificationHook("boom-hook", (_, attempt) =>
            throw new InvalidOperationException($"Hook exploded #{attempt}"));

        var loop = new LoopAgent(
            "loop-exception-guard",
            "Guards hook exception",
            loopMember,
            new VerificationOptions
            {
                MaxConsecutiveVerificationRetries = 1,
                TreatHookExceptionAsFailure = true,
                HaltOnCircuitBreaker = true
            },
            throwingHook,
            maxTurns: 6);

        using var llm = new StubLlmService();
        await loop.RunAsync("Trigger hook throw", llm, "t2-f14-04-loop");

        Assert.Equal(2, throwingHook.CallCount);
        Assert.True(loop.LastVerificationState!.IsTripped);
        Assert.Equal(VerificationStatus.CircuitBreakerTripped, loop.LastVerificationResult!.Status);
        Assert.Contains("Hook exploded #2", loop.LastVerificationResult.FailureReason, StringComparison.Ordinal);

        // 2. Throwing IVerificationTool inside AgentExecutionEngine converts exception to Error -> trips breaker cleanly
        var throwingTool = new ThrowingVerificationTool("crash_verifier");
        using var engine = new AgentExecutionEngine("engine-ex", (ctx, _, _) =>
            ValueTask.FromResult(new AgentStepDecision(string.Empty, IsCompleted: false, ToolName: "crash_verifier", ToolArgumentsJson: "{}")));
        engine.ConfigureVerification(new VerificationOptions
        {
            MaxConsecutiveVerificationRetries = 1,
            GuardToolExceptions = true,
            TreatHookExceptionAsFailure = true,
            HaltOnCircuitBreaker = true
        });
        engine.RegisterVerificationTool(throwingTool);

        var response = await engine.ExecuteLoopAsync("t2-f14-04-engine", "Trigger tool throw", maxSteps: 6);

        Assert.Equal(2, throwingTool.CallCount);
        Assert.False(response.IsCompleted);
        Assert.True(response.IsVerificationCircuitBreakerTripped);
        Assert.Equal(VerificationStatus.CircuitBreakerTripped, response.VerificationStatus);
        Assert.Contains("InvalidOperationException", response.VerificationResult!.FailureReason, StringComparison.Ordinal);

        // 3. ThrowOnCircuitBreakerTripped = true throws VerificationCircuitBreakerException with populated State & LastResult
        using var throwingBreakerEngine = new AgentExecutionEngine("engine-throw-breaker", (_, _, _) =>
            ValueTask.FromResult(new AgentStepDecision("always-bad", IsCompleted: true)));
        throwingBreakerEngine.ConfigureVerification(new VerificationOptions
        {
            MaxConsecutiveVerificationRetries = 0,
            ThrowOnCircuitBreakerTripped = true
        });
        throwingBreakerEngine.AttachVerificationHook("fail-now", (_, _) =>
            ValueTask.FromResult(VerificationResult.Fail("Hard trip")));

        var breakerEx = await Assert.ThrowsAsync<VerificationCircuitBreakerException>(async () =>
            await throwingBreakerEngine.ExecuteLoopAsync("t2-f14-04-throw", "Hard trip test", maxSteps: 3));
        Assert.True(breakerEx.State.IsTripped);
        Assert.Equal("Hard trip", breakerEx.LastResult.FailureReason);

        // 4. Caller CancellationToken cancellation is NOT swallowed as a tool/hook failure
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        using var cancelEngine = new AgentExecutionEngine("engine-cancel", (_, _, _) =>
            ValueTask.FromResult(new AgentStepDecision("draft", IsCompleted: true)));
        cancelEngine.ConfigureVerification(new VerificationOptions { TreatHookExceptionAsFailure = true });
        cancelEngine.AttachVerificationHook("cancel-hook", (_, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            return ValueTask.FromResult(VerificationResult.Pass());
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await cancelEngine.ExecuteLoopAsync("t2-f14-04-cancel", "Cancelled run", maxSteps: 2, ct: cts.Token));
    }

    [Fact]
    public async Task T2_F14_05_NegativeRetryConfig_ThrowsArgumentOutOfRange_AndNullOrDisabledVerificationHandled()
    {
        var options = new VerificationOptions();
        Assert.Throws<ArgumentOutOfRangeException>(() => options.MaxConsecutiveVerificationRetries = -1);
        Assert.Throws<ArgumentOutOfRangeException>(() => new VerificationCircuitBreaker(-1));

        var member = new ScriptedWorkflowAgent("plain-agent", (_, _) => "done");
        Assert.Throws<ArgumentNullException>(() =>
            new LoopAgent("null-opts", "desc", member, verificationOptions: null!));

        using var engine = new AgentExecutionEngine("null-opts-engine", (_, _, _) =>
            ValueTask.FromResult(new AgentStepDecision("ok", IsCompleted: true)));
        Assert.Throws<ArgumentNullException>(() => engine.ConfigureVerification(null!));

        // Null hook on LoopAgent executes cleanly without verification blocking
        var unverifiedLoop = new LoopAgent("unverified-loop", "No hook", member, maxTurns: 3, stopCondition: s => s == "done");
        using var llm = new StubLlmService();
        string loopOut = await unverifiedLoop.RunAsync("hello", llm, "t2-f14-05-unverified");
        Assert.Equal("done", loopOut);
        Assert.Empty(unverifiedLoop.VerificationHistory);

        // LlmAgent with VerificationOptions == null and no verification tools executes normally
        var llmAgent = new LlmAgent("plain-llm", "mock-model", "desc", "Be concise.")
        {
            VerificationOptions = null
        };
        using var stubLlm = new StubLlmService(new LlmResponse { Content = "concise-reply" });
        string llmReply = await llmAgent.RunAsync("hi", stubLlm, "t2-f14-05-llm");
        Assert.Equal("concise-reply", llmReply);
        Assert.Null(llmAgent.LastVerificationState);
        Assert.Null(llmAgent.LastVerificationResult);
    }
}
