using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Glacier.Agent.Llm;
using Glacier.Agent.Runtime;
using Glacier.Agent.Serialization;
using Glacier.Agent.Tools;
using Glacier.Agent.Workflows;
using Xunit;

namespace Glacier.Agent.Tests;

public sealed class VerificationAndCircuitBreakerTests
{
    private sealed class ScriptedAgent : IAgent
    {
        private readonly Func<string, int, string> _handler;
        public List<string> ReceivedPrompts { get; } = new();

        public string Name { get; }
        public string Description => "Scripted test agent";

        public ScriptedAgent(string name, Func<string, int, string> handler)
        {
            Name = name;
            _handler = handler;
        }

        public Task<string> RunAsync(string prompt, ILlmService llmService, string? sessionId = null)
        {
            ReceivedPrompts.Add(prompt);
            return Task.FromResult(_handler(prompt, ReceivedPrompts.Count));
        }

        public async IAsyncEnumerable<string> RunStreamingAsync(string prompt, ILlmService llmService, string? sessionId = null)
        {
            var result = await RunAsync(prompt, llmService, sessionId).ConfigureAwait(false);
            yield return result;
        }
    }

    private sealed class StubLlmService : ILlmService
    {
        private readonly Queue<LlmResponse> _responses;

        public StubLlmService(params LlmResponse[] responses)
        {
            _responses = new Queue<LlmResponse>(responses);
        }

        public Task<LlmResponse> GenerateContentAsync(LlmRequest request)
        {
            if (_responses.Count == 0)
            {
                return Task.FromResult(new LlmResponse { Content = "fallback-done" });
            }
            return Task.FromResult(_responses.Dequeue());
        }

        public async IAsyncEnumerable<LlmResponse> StreamGenerateContentAsync(LlmRequest request)
        {
            yield return await GenerateContentAsync(request).ConfigureAwait(false);
        }
    }

    private sealed class DelegateAgentToolImpl : IAgentTool
    {
        private readonly Func<string, CancellationToken, ValueTask<string>> _handler;

        public string Name { get; }
        public string Description => "Delegate test tool";
        public string ParameterJsonSchema => "{\"type\":\"object\"}";

        public DelegateAgentToolImpl(string name, Func<string, CancellationToken, ValueTask<string>> handler)
        {
            Name = name;
            _handler = handler;
        }

        public ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
            => _handler(argumentsJson, ct);
    }

    private sealed class SimpleITool : ITool
    {
        private readonly Func<string, Task<string>> _handler;

        public string Name { get; }
        public string Description => "Simple ITool";

        public SimpleITool(string name, Func<string, Task<string>> handler)
        {
            Name = name;
            _handler = handler;
        }

        public JsonNode GetParametersSchema() => new JsonObject { ["type"] = "object" };

        public Task<string> ExecuteAsync(string arguments) => _handler(arguments);
    }

    [Fact]
    public void CircuitBreaker_RespectsMaxConsecutiveRetries_AndResetsOnSuccess()
    {
        var zeroRetryBreaker = new VerificationCircuitBreaker(maxConsecutiveRetries: 0);
        var state0 = zeroRetryBreaker.RecordFailure(VerificationResult.Fail("bad-1"));
        Assert.True(state0.IsTripped);
        Assert.Equal(CircuitBreakerStatus.Open, state0.Status);
        Assert.Equal(VerificationStatus.CircuitBreakerTripped, state0.LastResult!.Status);

        var breaker = new VerificationCircuitBreaker(maxConsecutiveRetries: 2);
        var s1 = breaker.RecordFailure(VerificationResult.Fail("fail-1"));
        Assert.False(s1.IsTripped);
        Assert.Equal(1, s1.ConsecutiveFailures);

        var s2 = breaker.RecordFailure(VerificationResult.Fail("fail-2"));
        Assert.False(s2.IsTripped);
        Assert.Equal(2, s2.ConsecutiveFailures);

        var pass = breaker.RecordSuccess(VerificationResult.Pass("ok"));
        Assert.False(pass.IsTripped);
        Assert.Equal(0, pass.ConsecutiveFailures);
        Assert.Equal(1, pass.TotalPasses);
        Assert.Equal(2, pass.TotalFailures);

        Assert.False(breaker.RecordFailure(VerificationResult.Fail("fail-3")).IsTripped);
        Assert.False(breaker.RecordFailure(VerificationResult.Fail("fail-4")).IsTripped);
        var tripped = breaker.RecordFailure(VerificationResult.Fail("fail-5"));
        Assert.True(tripped.IsTripped);
        Assert.Equal(3, tripped.ConsecutiveFailures);

        breaker.Reset();
        var resetState = breaker.GetState();
        Assert.False(resetState.IsTripped);
        Assert.Equal(0, resetState.ConsecutiveFailures);
        Assert.Equal(0, resetState.TotalPasses);
        Assert.Equal(0, resetState.TotalFailures);
    }

    [Fact]
    public async Task SelfVerificationTool_BuiltInChecks_AndCircuitBreakerEnforcement()
    {
        var breaker = new VerificationCircuitBreaker(maxConsecutiveRetries: 1);
        var tool = new SelfVerificationTool(circuitBreaker: breaker);
        ITool asITool = tool;
        IAgentTool asAgentTool = tool;

        string emptyRes = await asITool.ExecuteAsync("{\"output\":\"   \"}");
        var parsedEmpty = JsonSerializer.Deserialize(emptyRes, AdkJsonContext.Default.VerificationResult)!;
        Assert.False(parsedEmpty.Passed);
        Assert.Equal(1, parsedEmpty.ConsecutiveFailures);
        Assert.False(parsedEmpty.CircuitTripped);

        string invalidJsonRes = await asAgentTool.ExecuteAsync("{\"output\":\"{not-json\",\"requireValidJson\":true}");
        var parsedInvalidJson = JsonSerializer.Deserialize(invalidJsonRes, AdkJsonContext.Default.VerificationResult)!;
        Assert.False(parsedInvalidJson.Passed);
        Assert.True(parsedInvalidJson.CircuitTripped);
        Assert.Equal(VerificationStatus.CircuitBreakerTripped, parsedInvalidJson.Status);

        // Once tripped, tool returns Tripped immediately without running checks
        string validButTrippedRes = await asAgentTool.ExecuteAsync("{\"output\":\"{\\\"ok\\\":true}\",\"requireValidJson\":true}");
        var parsedTripped = JsonSerializer.Deserialize(validButTrippedRes, AdkJsonContext.Default.VerificationResult)!;
        Assert.False(parsedTripped.Passed);
        Assert.True(parsedTripped.CircuitTripped);

        // Fresh tool passes when structural checks succeed
        var freshTool = new SelfVerificationTool();
        string okRes = await ((IAgentTool)freshTool).ExecuteAsync(
            "{\"output\":\"{\\\"answer\\\":42,\\\"unit\\\":\\\"ms\\\"}\",\"requireValidJson\":true,\"requiredFields\":[\"answer\",\"unit\"],\"requiredSubstrings\":[\"42\"]}");
        var parsedOk = JsonSerializer.Deserialize(okRes, AdkJsonContext.Default.VerificationResult)!;
        Assert.True(parsedOk.Passed);
        Assert.Equal(VerificationStatus.Passed, parsedOk.Status);
    }

    [Fact]
    public async Task LoopAgent_RunAsync_PassesAfterRetry_AndResetsConsecutiveFailuresOnPass()
    {
        var member = new ScriptedAgent("worker", (prompt, turn) => turn switch
        {
            1 => "draft-bad",
            2 => "good-1",
            3 => "draft-bad-2",
            4 => "draft-bad-3",
            _ => "good-final"
        });

        int verifyCalls = 0;
        var hook = DelegateVerificationHook.Create("content-check", ctx =>
        {
            verifyCalls++;
            return ctx.CandidateOutput.StartsWith("good", StringComparison.Ordinal)
                ? VerificationResult.Pass("Accepted", "content-check")
                : VerificationResult.Fail($"Rejected '{ctx.CandidateOutput}'", suggestedFix: "Prefix with 'good'.", verifierName: "content-check");
        });

        var options = new VerificationOptions
        {
            MaxConsecutiveVerificationRetries = 2,
            StopOnVerificationPass = false,
            RequireVerificationForStopCondition = true
        };

        var loop = new LoopAgent(
            "loop-reset",
            "Loop test",
            member,
            options,
            hook,
            maxTurns: 6,
            stopCondition: output => output == "good-final");

        var events = new List<AgentEvent>();
        loop.EventPublished += events.Add;

        string finalOutput = await loop.RunAsync("Initial task", new StubLlmService(), sessionId: "sess-loop-1");

        Assert.Equal("good-final", finalOutput);
        Assert.Equal(5, verifyCalls);
        Assert.NotNull(loop.LastVerificationState);
        Assert.False(loop.LastVerificationState!.IsTripped);
        Assert.Equal(0, loop.LastVerificationState.ConsecutiveFailures);
        Assert.Equal(2, loop.LastVerificationState.TotalPasses);
        Assert.Equal(3, loop.LastVerificationState.TotalFailures);
        Assert.Contains("<|verification_failed|>", member.ReceivedPrompts[1]);
        Assert.Contains(events, e => e.EventType == "VerificationPassed");
        Assert.Contains(events, e => e.EventType == "VerificationFailed");
        Assert.DoesNotContain(events, e => e.EventType == "VerificationCircuitBreakerTripped");
    }

    [Fact]
    public async Task LoopAgent_RunAsync_TripsCircuitBreakerCleanly_AndAllowsFreshSubsequentRun()
    {
        var member = new ScriptedAgent("always-bad", (_, turn) => $"bad-turn-{turn}");
        var hook = DelegateVerificationHook.Create("strict-check", ctx =>
            VerificationResult.Fail($"Output '{ctx.CandidateOutput}' failed validation."));

        var options = new VerificationOptions
        {
            MaxConsecutiveVerificationRetries = 2,
            HaltOnCircuitBreaker = true,
            ThrowOnCircuitBreakerTripped = false
        };

        var loop = new LoopAgent("loop-trip", "Trips breaker", member, options, hook, maxTurns: 10);
        var events = new List<AgentEvent>();
        loop.EventPublished += events.Add;

        string output1 = await loop.RunAsync("Start", new StubLlmService(), sessionId: "sess-trip");
        Assert.Equal(3, member.ReceivedPrompts.Count); // 1 initial + 2 retries = 3 consecutive failures trips breaker
        Assert.NotNull(loop.LastVerificationState);
        Assert.True(loop.LastVerificationState!.IsTripped);
        Assert.Equal(3, loop.LastVerificationState.ConsecutiveFailures);
        Assert.Contains(events, e => e.EventType == "VerificationCircuitBreakerTripped");

        var parsedOut = JsonSerializer.Deserialize(output1, AdkJsonContext.Default.VerificationResult)!;
        Assert.Equal(VerificationStatus.CircuitBreakerTripped, parsedOut.Status);
        Assert.True(parsedOut.CircuitTripped);

        // Second run on same LoopAgent starts with a fresh per-run breaker
        var goodHook = DelegateVerificationHook.Create("pass-check", _ => VerificationResult.Pass("Now ok"));
        loop.VerificationHook = goodHook;
        string output2 = await loop.RunAsync("Retry fresh", new StubLlmService(), sessionId: "sess-trip-2");
        Assert.Equal("bad-turn-4", output2);
        Assert.False(loop.LastVerificationState!.IsTripped);
        Assert.Equal(0, loop.LastVerificationState.ConsecutiveFailures);
    }

    [Fact]
    public async Task LoopAgent_RunStreamingAsync_HaltsCleanlyOnCircuitBreaker_AndRequiresVerificationForStopCondition()
    {
        var member = new ScriptedAgent("stream-bad", (_, _) => "READY_UNVERIFIED");
        var hook = DelegateVerificationHook.Create("verify", _ =>
            VerificationResult.Fail("Schema invalid."));

        var options = new VerificationOptions
        {
            MaxConsecutiveVerificationRetries = 1,
            RequireVerificationForStopCondition = true,
            HaltOnCircuitBreaker = true
        };

        var loop = new LoopAgent(
            "loop-stream",
            "Streaming test",
            member,
            maxTurns: 8,
            stopCondition: s => s == "READY_UNVERIFIED",
            verificationHook: hook)
        {
            VerificationOptions = options
        };

        var chunks = new List<string>();
        await foreach (var chunk in loop.RunStreamingAsync("Go", new StubLlmService()))
        {
            chunks.Add(chunk);
        }

        Assert.Equal(2, member.ReceivedPrompts.Count); // 1 + 1 retry
        Assert.True(loop.LastVerificationState!.IsTripped);
        Assert.Contains(chunks, c => c.Contains("Verification Circuit Breaker Tripped", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LoopAgent_HookException_TreatedAsFailure_AndThrowOnTrippedOptionRespected()
    {
        var member = new ScriptedAgent("worker", (_, _) => "candidate");
        var throwingHook = DelegateVerificationHook.Create("exploding-hook", _ =>
            throw new InvalidOperationException("Verifier service unavailable"));

        var options = new VerificationOptions
        {
            MaxConsecutiveVerificationRetries = 0,
            TreatHookExceptionAsFailure = true,
            ThrowOnCircuitBreakerTripped = true
        };

        var loop = new LoopAgent("loop-ex", "Exception hook test", member, options, throwingHook, maxTurns: 5);

        var ex = await Assert.ThrowsAsync<VerificationCircuitBreakerException>(
            () => loop.RunAsync("Run", new StubLlmService()));

        Assert.True(ex.State.IsTripped);
        Assert.Equal(VerificationStatus.CircuitBreakerTripped, ex.LastResult.Status);
        Assert.Contains("Verifier service unavailable", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoopAgent_UseVerificationTool_AdaptsIToolCorrectly()
    {
        var member = new ScriptedAgent("worker", (_, turn) => turn == 1 ? "bad" : "{\"ok\":true}");
        var verifyTool = new SelfVerificationTool();
        var loop = new LoopAgent("loop-tool", "Tool hook", member, maxTurns: 4);
        loop.VerificationOptions.MaxConsecutiveVerificationRetries = 2;
        loop.UseVerificationTool((ITool)new SimpleITool("custom_verify", argsJson =>
        {
            using var doc = JsonDocument.Parse(argsJson);
            string output = doc.RootElement.GetProperty("output").GetString() ?? string.Empty;
            bool ok = output.Contains("\"ok\":true", StringComparison.Ordinal);
            return Task.FromResult(ok ? "{\"passed\":true,\"summary\":\"Good\"}" : "{\"passed\":false,\"failureReason\":\"Missing ok flag\"}");
        }));

        string result = await loop.RunAsync("Check", new StubLlmService());
        Assert.Equal("{\"ok\":true}", result);
        Assert.Equal(2, loop.VerificationHistory.Count);
        Assert.False(loop.VerificationHistory[0].Passed);
        Assert.True(loop.VerificationHistory[1].Passed);
    }

    [Fact]
    public async Task LlmAgent_BreaksInfiniteSelfVerifyToolLoop_InRunAndStreaming()
    {
        var failCall = new LlmFunctionCall
        {
            Id = "c1",
            Name = SelfVerificationTool.DefaultToolName,
            Args = new JsonObject { ["output"] = "" }
        };

        // Always returns failing self_verify call — would spin infinitely without circuit breaker
        var infiniteStub = new StubLlmService(
            Enumerable.Range(0, 20).Select(_ => new LlmResponse { FunctionCalls = new List<LlmFunctionCall> { failCall } }).ToArray());

        var selfVerifyTool = new SelfVerificationTool();
        var agent = new LlmAgent(
            "local-llm",
            "local-qwen",
            "Self verifying agent",
            "Verify your work",
            new List<ITool> { selfVerifyTool })
        {
            VerificationOptions = new VerificationOptions { MaxConsecutiveVerificationRetries = 2 }
        };

        string result = await agent.RunAsync("Produce output", infiniteStub);
        Assert.NotNull(agent.LastVerificationState);
        Assert.True(agent.LastVerificationState!.IsTripped);
        Assert.Equal(3, agent.LastVerificationState.ConsecutiveFailures);
        var parsed = JsonSerializer.Deserialize(result, AdkJsonContext.Default.VerificationResult)!;
        Assert.True(parsed.CircuitTripped);

        // Streaming path also breaks cleanly
        var streamStub = new StubLlmService(
            Enumerable.Range(0, 20).Select(_ => new LlmResponse { FunctionCalls = new List<LlmFunctionCall> { failCall } }).ToArray());
        var streamChunks = new List<string>();
        await foreach (var chunk in agent.RunStreamingAsync("Produce output", streamStub))
        {
            streamChunks.Add(chunk);
        }
        Assert.True(agent.LastVerificationState!.IsTripped);
        Assert.NotEmpty(streamChunks);
    }

    [Fact]
    public async Task AgentExecutionEngine_ExecuteLoopAsync_HookPassAndRetryPreservesHistory()
    {
        var observedPrompts = new List<string>();
        var engine = new AgentExecutionEngine("engine-retry", (ctx, tools, ct) =>
        {
            observedPrompts.Add(ctx.Prompt);
            if (ctx.StepCount == 1)
            {
                return ValueTask.FromResult(new AgentStepDecision("Invoke helper", IsCompleted: false, ToolName: "helper", ToolArgumentsJson: "{}"));
            }
            if (ctx.StepCount == 2)
            {
                return ValueTask.FromResult(new AgentStepDecision("draft-v1", IsCompleted: true));
            }
            return ValueTask.FromResult(new AgentStepDecision("final-verified", IsCompleted: true));
        });

        engine.RegisterTool(new DelegateAgentToolImpl("helper", (_, _) => ValueTask.FromResult("helper-data")));
        engine.AttachVerificationHook("output-check", (ctx, _) =>
        {
            if (ctx.CandidateOutput == "final-verified")
            {
                return ValueTask.FromResult(VerificationResult.Pass("Valid", "output-check"));
            }
            return ValueTask.FromResult(VerificationResult.Fail("Needs final-verified", suggestedFix: "Return final-verified", verifierName: "output-check"));
        });

        var response = await engine.ExecuteLoopAsync("s-retry", "Do work", maxSteps: 5);

        Assert.True(response.IsCompleted);
        Assert.Equal("final-verified", response.Output);
        Assert.Equal(VerificationStatus.Passed, response.VerificationStatus);
        Assert.Equal(3, observedPrompts.Count);
        Assert.Contains("helper-data", observedPrompts[1], StringComparison.Ordinal);
        Assert.Contains("helper-data", observedPrompts[2], StringComparison.Ordinal);
        Assert.Contains("<|verification_failed|>", observedPrompts[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task AgentExecutionEngine_NonVerificationToolCalls_DoNotResetConsecutiveVerificationFailures()
    {
        var engine = new AgentExecutionEngine("engine-no-reset", (ctx, tools, ct) => ctx.StepCount switch
        {
            1 => ValueTask.FromResult(new AgentStepDecision("verify 1", false, "self_verify", "{\"output\":\"\"}")),
            2 => ValueTask.FromResult(new AgentStepDecision("helper", false, "helper", "{}")),
            3 => ValueTask.FromResult(new AgentStepDecision("verify 2", false, "self_verify", "{\"output\":\"\"}")),
            _ => ValueTask.FromResult(new AgentStepDecision("verify 3", false, "self_verify", "{\"output\":\"\"}"))
        });

        engine.ConfigureVerification(new VerificationOptions { MaxConsecutiveVerificationRetries = 2 });
        engine.RegisterTool(new DelegateAgentToolImpl("helper", (_, _) => ValueTask.FromResult("ok")));
        engine.RegisterVerificationTool(new SelfVerificationTool());

        var events = new List<AgentEvent>();
        engine.EventPublished += events.Add;

        var response = await engine.ExecuteLoopAsync("s-no-reset", "Run", maxSteps: 10);

        Assert.False(response.IsCompleted);
        Assert.True(response.IsVerificationCircuitBreakerTripped);
        Assert.Equal(VerificationStatus.CircuitBreakerTripped, response.VerificationStatus);

        var breaker = engine.GetCircuitBreaker("s-no-reset")!;
        Assert.True(breaker.IsTripped);
        Assert.Equal(3, breaker.ConsecutiveFailures);
        Assert.Contains(events, e => e.EventType == "VerificationCircuitBreakerTripped");

        engine.ResetVerificationCircuitBreaker("s-no-reset");
        Assert.False(breaker.IsTripped);
        Assert.Equal(0, breaker.ConsecutiveFailures);
    }

    [Fact]
    public async Task AgentExecutionEngine_ThrowingToolIsGuarded_AndCancellationPropagates()
    {
        int stepCalls = 0;
        var engine = new AgentExecutionEngine("engine-guard", (ctx, tools, ct) =>
        {
            stepCalls++;
            if (stepCalls == 1)
            {
                return ValueTask.FromResult(new AgentStepDecision("call boom", false, "boom", "{}"));
            }
            return ValueTask.FromResult(new AgentStepDecision("recovered", true));
        });

        engine.RegisterTool(new DelegateAgentToolImpl("boom", (_, _) => throw new InvalidOperationException("disk full")));

        var events = new List<AgentEvent>();
        engine.EventPublished += events.Add;

        var resp = await engine.ExecuteLoopAsync("s-guard", "Go", maxSteps: 4);
        Assert.True(resp.IsCompleted);
        Assert.Equal("recovered", resp.Output);
        Assert.Contains(events, e => e.EventType == "ToolFailed" && e.Payload.Contains("disk full", StringComparison.Ordinal));

        // Caller CancellationToken cancellation must propagate rather than being swallowed
        var cancelEngine = new AgentExecutionEngine("engine-cancel", (_, _, _) =>
            ValueTask.FromResult(new AgentStepDecision("call slow", false, "slow", "{}")));
        cancelEngine.RegisterTool(new DelegateAgentToolImpl("slow", (_, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            return ValueTask.FromResult("done");
        }));

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => cancelEngine.ExecuteStepAsync(new AgentContext("s-cancel", "Go", 1), cts.Token).AsTask());
    }

    [Fact]
    public async Task AgentExecutionEngine_AutoInvokeRegisteredVerificationTool_OnCompletion()
    {
        int attempt = 0;
        var engine = new AgentExecutionEngine("engine-auto-verify", (ctx, _, _) =>
        {
            attempt++;
            return ValueTask.FromResult(new AgentStepDecision(attempt == 1 ? "" : "valid-answer", IsCompleted: true));
        });

        engine.ConfigureVerification(new VerificationOptions
        {
            AutoInvokeRegisteredVerificationTool = true,
            MaxConsecutiveVerificationRetries = 2
        });
        engine.RegisterVerificationTool(new SelfVerificationTool());

        var resp = await engine.ExecuteLoopAsync("s-auto", "Answer please", maxSteps: 4);
        Assert.True(resp.IsCompleted);
        Assert.Equal("valid-answer", resp.Output);
        Assert.Equal(VerificationStatus.Passed, resp.VerificationStatus);
    }

    [Fact]
    public void AdkJsonContext_RoundTripsAllVerificationDtos_AotSafely()
    {
        var issue = new VerificationIssue("code_1", "Missing field", "payload", "Add payload");
        var result = VerificationResult.Fail("Validation failed", new[] { issue }, "Fix it", "test-verifier");
        string resultJson = result.ToJson();
        var roundTrippedResult = JsonSerializer.Deserialize(resultJson, AdkJsonContext.Default.VerificationResult)!;
        Assert.False(roundTrippedResult.Passed);
        Assert.Equal(VerificationStatus.Failed, roundTrippedResult.Status);
        Assert.Single(roundTrippedResult.Issues);
        Assert.Equal("code_1", roundTrippedResult.Issues[0].Code);

        string indentedJson = JsonSerializer.Serialize(result, AdkIndentedJsonContext.Default.VerificationResult);
        Assert.Contains("\n", indentedJson, StringComparison.Ordinal);

        var breakerState = new VerificationCircuitBreakerState(CircuitBreakerStatus.Open, 4, 3, 1, 4, "Boom", result);
        string stateJson = JsonSerializer.Serialize(breakerState, AdkJsonContext.Default.VerificationCircuitBreakerState);
        var rtState = JsonSerializer.Deserialize(stateJson, AdkJsonContext.Default.VerificationCircuitBreakerState)!;
        Assert.True(rtState.IsTripped);
        Assert.Equal(4, rtState.ConsecutiveFailures);

        var evtPayload = new VerificationEventPayload("agent-x", "sess-1", 2, VerificationStatus.Failed, false, 1, 3, false, "hook", "summary", "reason", "self_verify");
        string evtJson = JsonSerializer.Serialize(evtPayload, AdkJsonContext.Default.VerificationEventPayload);
        var rtEvt = JsonSerializer.Deserialize(evtJson, AdkJsonContext.Default.VerificationEventPayload)!;
        Assert.Equal("agent-x", rtEvt.AgentName);

        var errPayload = new ToolErrorPayload("error", "boom", "InvalidOperationException", "bad");
        string errJson = JsonSerializer.Serialize(errPayload, AdkJsonContext.Default.ToolErrorPayload);
        var rtErr = JsonSerializer.Deserialize(errJson, AdkJsonContext.Default.ToolErrorPayload)!;
        Assert.Equal("boom", rtErr.Tool);

        var fbPayload = new VerificationFeedbackPayload(2, 1, 3, "hook", "bad", "fix", new List<VerificationIssue> { issue });
        string fbJson = JsonSerializer.Serialize(fbPayload, AdkJsonContext.Default.VerificationFeedbackPayload);
        var rtFb = JsonSerializer.Deserialize(fbJson, AdkJsonContext.Default.VerificationFeedbackPayload)!;
        Assert.Equal(2, rtFb.TurnOrStep);

        var toolArgs = new SelfVerificationToolArgs
        {
            Output = "hello",
            RequireValidJson = false,
            RequiredSubstrings = new List<string> { "hello" }
        };
        string argsJson = JsonSerializer.Serialize(toolArgs, AdkJsonContext.Default.SelfVerificationToolArgs);
        var rtArgs = JsonSerializer.Deserialize(argsJson, AdkJsonContext.Default.SelfVerificationToolArgs)!;
        Assert.Equal("hello", rtArgs.Output);

        var schema = new SelfVerificationTool().GetParametersSchema();
        Assert.Equal("object", schema["type"]?.GetValue<string>());
    }

    [Fact]
    public async Task Telemetry_EmitsVerificationCountersAndActivities_IsolatedByUniqueAgentTag()
    {
        string uniqueLoopAgent = $"loop-telemetry-{Guid.NewGuid():N}";
        string uniqueEngineAgent = $"engine-telemetry-{Guid.NewGuid():N}";

        long loopAttempts = 0, loopPasses = 0, loopFailures = 0, loopTrips = 0;
        long engineAttempts = 0, enginePasses = 0, engineFailures = 0, engineTrips = 0;
        var capturedActivities = new List<Activity>();

        using var meterListener = new MeterListener();
        meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter == Telemetry.Meter)
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        meterListener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
        {
            string? agentTag = null;
            foreach (var tag in tags)
            {
                if (tag.Key == "agent")
                {
                    agentTag = tag.Value?.ToString();
                    break;
                }
            }

            if (agentTag == uniqueLoopAgent)
            {
                if (instrument == Telemetry.VerificationAttemptsCounter) Interlocked.Add(ref loopAttempts, measurement);
                else if (instrument == Telemetry.VerificationPassesCounter) Interlocked.Add(ref loopPasses, measurement);
                else if (instrument == Telemetry.VerificationFailuresCounter) Interlocked.Add(ref loopFailures, measurement);
                else if (instrument == Telemetry.CircuitBreakerTripsCounter) Interlocked.Add(ref loopTrips, measurement);
            }
            else if (agentTag == uniqueEngineAgent)
            {
                if (instrument == Telemetry.VerificationAttemptsCounter) Interlocked.Add(ref engineAttempts, measurement);
                else if (instrument == Telemetry.VerificationPassesCounter) Interlocked.Add(ref enginePasses, measurement);
                else if (instrument == Telemetry.VerificationFailuresCounter) Interlocked.Add(ref engineFailures, measurement);
                else if (instrument == Telemetry.CircuitBreakerTripsCounter) Interlocked.Add(ref engineTrips, measurement);
            }
        });
        meterListener.Start();

        using var activityListener = new ActivityListener
        {
            ShouldListenTo = source => source == Telemetry.ActivitySource,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                var agentName = activity.GetTagItem("agent.name")?.ToString();
                if (agentName == uniqueLoopAgent || agentName == uniqueEngineAgent)
                {
                    lock (capturedActivities)
                    {
                        capturedActivities.Add(activity);
                    }
                }
            }
        };
        ActivitySource.AddActivityListener(activityListener);

        // 1. LoopAgent trips after 2 consecutive failures (MaxRetries = 1)
        var loopMember = new ScriptedAgent("m", (_, _) => "bad");
        var loop = new LoopAgent(
            uniqueLoopAgent,
            "telemetry loop",
            loopMember,
            new VerificationOptions { MaxConsecutiveVerificationRetries = 1 },
            DelegateVerificationHook.Create("h", _ => VerificationResult.Fail("nope")),
            maxTurns: 5);
        await loop.RunAsync("go", new StubLlmService(), sessionId: uniqueLoopAgent);

        // 2. AgentExecutionEngine fails once then passes
        int stepNum = 0;
        var engine = new AgentExecutionEngine(uniqueEngineAgent, (_, _, _) =>
        {
            stepNum++;
            return ValueTask.FromResult(new AgentStepDecision(stepNum == 1 ? "bad" : "good", IsCompleted: true));
        });
        engine.ConfigureVerification(new VerificationOptions { MaxConsecutiveVerificationRetries = 2 });
        engine.AttachVerificationHook("h2", (ctx, _) => ValueTask.FromResult(
            ctx.CandidateOutput == "good" ? VerificationResult.Pass() : VerificationResult.Fail("bad")));
        await engine.ExecuteLoopAsync(uniqueEngineAgent, "go", maxSteps: 4);

        Assert.Equal(2, Interlocked.Read(ref loopAttempts));
        Assert.Equal(0, Interlocked.Read(ref loopPasses));
        Assert.Equal(2, Interlocked.Read(ref loopFailures));
        Assert.Equal(1, Interlocked.Read(ref loopTrips));

        Assert.Equal(2, Interlocked.Read(ref engineAttempts));
        Assert.Equal(1, Interlocked.Read(ref enginePasses));
        Assert.Equal(1, Interlocked.Read(ref engineFailures));
        Assert.Equal(0, Interlocked.Read(ref engineTrips));

        Assert.Contains(capturedActivities, a => a.OperationName == $"Verify {uniqueLoopAgent}");
        Assert.Contains(capturedActivities, a => a.OperationName == $"Verify {uniqueEngineAgent}");
    }

    [Fact]
    public async Task AgentExecutionEngine_ExecuteStepAsync_WhenTrippedWithHaltFalse_ReportsCircuitBreakerTripped()
    {
        var engine = new AgentExecutionEngine("engine-step-no-halt", (_, _, _) =>
            ValueTask.FromResult(new AgentStepDecision("verify", IsCompleted: false, ToolName: "self_verify", ToolArgumentsJson: "{\"output\":\"\"}")));

        engine.ConfigureVerification(new VerificationOptions
        {
            MaxConsecutiveVerificationRetries = 1,
            HaltOnCircuitBreaker = false,
            ThrowOnCircuitBreakerTripped = false
        });
        engine.RegisterVerificationTool(new SelfVerificationTool());

        var events = new List<AgentEvent>();
        engine.EventPublished += events.Add;

        var step1 = await engine.ExecuteStepAsync(new AgentContext("s-step-no-halt", "Run", 1));
        Assert.False(step1.IsCompleted);
        Assert.Equal(VerificationStatus.Failed, step1.VerificationStatus);
        Assert.False(step1.IsVerificationCircuitBreakerTripped);
        Assert.NotNull(step1.VerificationResult);
        Assert.False(step1.VerificationResult!.CircuitTripped);

        var step2 = await engine.ExecuteStepAsync(new AgentContext("s-step-no-halt", "Run", 2));
        Assert.False(step2.IsCompleted);
        Assert.Equal(VerificationStatus.CircuitBreakerTripped, step2.VerificationStatus);
        Assert.True(step2.IsVerificationCircuitBreakerTripped);
        Assert.NotNull(step2.VerificationResult);
        Assert.Equal(VerificationStatus.CircuitBreakerTripped, step2.VerificationResult!.Status);
        Assert.True(step2.VerificationResult.CircuitTripped);
        Assert.Contains(events, e => e.EventType == "VerificationCircuitBreakerTripped");
    }

    [Fact]
    public async Task AgentExecutionEngine_ExecuteLoopAsync_WhenTrippedAtMaxStepsWithHaltFalse_ReportsCircuitBreakerTripped()
    {
        int stepsExecuted = 0;
        var engine = new AgentExecutionEngine("engine-loop-no-halt-max", (_, _, _) =>
        {
            stepsExecuted++;
            return ValueTask.FromResult(new AgentStepDecision(string.Empty, IsCompleted: true));
        });

        engine.ConfigureVerification(new VerificationOptions
        {
            MaxConsecutiveVerificationRetries = 1,
            AutoInvokeRegisteredVerificationTool = true,
            HaltOnCircuitBreaker = false,
            ThrowOnCircuitBreakerTripped = false
        });
        engine.RegisterVerificationTool(new SelfVerificationTool());

        var events = new List<AgentEvent>();
        engine.EventPublished += events.Add;

        var response = await engine.ExecuteLoopAsync("s-loop-no-halt-max", "Answer please", maxSteps: 2);

        Assert.Equal(2, stepsExecuted);
        Assert.False(response.IsCompleted);
        Assert.Equal(VerificationStatus.CircuitBreakerTripped, response.VerificationStatus);
        Assert.True(response.IsVerificationCircuitBreakerTripped);
        Assert.NotNull(response.VerificationResult);
        Assert.Equal(VerificationStatus.CircuitBreakerTripped, response.VerificationResult!.Status);
        Assert.True(response.VerificationResult.CircuitTripped);
        Assert.Contains(events, e => e.EventType == "VerificationCircuitBreakerTripped");
    }

    [Fact]
    public async Task AgentExecutionEngine_ExecuteLoopAsync_WhenTrippedWithHaltFalse_ContinuesRemainingStepsWithoutHaltingEarly()
    {
        var observedPrompts = new List<string>();
        var engine = new AgentExecutionEngine("engine-loop-no-halt-continue", (ctx, _, _) =>
        {
            observedPrompts.Add(ctx.Prompt);
            return ValueTask.FromResult(new AgentStepDecision(
                "verify",
                IsCompleted: false,
                ToolName: "self_verify",
                ToolArgumentsJson: "{\"output\":\"\"}"));
        });

        engine.ConfigureVerification(new VerificationOptions
        {
            MaxConsecutiveVerificationRetries = 1,
            HaltOnCircuitBreaker = false,
            ThrowOnCircuitBreakerTripped = false,
            AppendStructuredFeedbackToPrompt = true
        });
        engine.RegisterVerificationTool(new SelfVerificationTool());

        var response = await engine.ExecuteLoopAsync("s-loop-no-halt-continue", "Run", maxSteps: 3);

        // Step 2 trips the breaker, but HaltOnCircuitBreaker = false lets step 3 still run with feedback
        Assert.Equal(3, observedPrompts.Count);
        Assert.Contains("<|verification_failed|>", observedPrompts[2], StringComparison.Ordinal);
        Assert.False(response.IsCompleted);
        Assert.Equal(VerificationStatus.CircuitBreakerTripped, response.VerificationStatus);
        Assert.True(response.IsVerificationCircuitBreakerTripped);
        Assert.NotNull(response.VerificationResult);
        Assert.True(response.VerificationResult!.CircuitTripped);
    }

    [Fact]
    public async Task LoopAgent_UseVerificationTool_HonorsAttachedSelfVerificationToolCircuitBreaker_InRunAsync()
    {
        int hookCalls = 0;
        var member = new ScriptedAgent("worker", (_, turn) => $"bad-{turn}");
        var toolBreaker = new VerificationCircuitBreaker(maxConsecutiveRetries: 1);
        var verifyTool = new SelfVerificationTool(
            hook: DelegateVerificationHook.Create("strict-tool", _ =>
            {
                hookCalls++;
                return VerificationResult.Fail("Tool verification failed");
            }),
            circuitBreaker: toolBreaker);

        var options = new VerificationOptions
        {
            MaxConsecutiveVerificationRetries = 5,
            HaltOnCircuitBreaker = true,
            ThrowOnCircuitBreakerTripped = false
        };

        var loop = new LoopAgent("loop-ext-breaker", "External tool breaker test", member, options, maxTurns: 6);
        loop.UseVerificationTool((IAgentTool)verifyTool);

        var events = new List<AgentEvent>();
        loop.EventPublished += events.Add;

        string output = await loop.RunAsync("Start", new StubLlmService(), sessionId: "sess-ext-run");

        Assert.Equal(2, member.ReceivedPrompts.Count);
        Assert.Equal(2, hookCalls);
        Assert.True(toolBreaker.IsTripped);
        Assert.Equal(2, toolBreaker.ConsecutiveFailures);
        Assert.NotNull(loop.LastVerificationState);
        Assert.True(loop.LastVerificationState!.IsTripped);
        Assert.Equal(2, loop.LastVerificationState.ConsecutiveFailures);
        Assert.Equal(1, loop.LastVerificationState.MaxConsecutiveRetries);
        Assert.NotNull(loop.LastVerificationResult);
        Assert.True(loop.LastVerificationResult!.CircuitTripped);
        Assert.Equal(VerificationStatus.CircuitBreakerTripped, loop.LastVerificationResult.Status);
        Assert.Contains(events, e => e.EventType == "VerificationCircuitBreakerTripped");

        var parsedOut = JsonSerializer.Deserialize(output, AdkJsonContext.Default.VerificationResult)!;
        Assert.True(parsedOut.CircuitTripped);
        Assert.Equal(VerificationStatus.CircuitBreakerTripped, parsedOut.Status);
    }

    [Fact]
    public async Task LoopAgent_UseVerificationTool_HonorsAttachedSelfVerificationToolCircuitBreaker_InRunStreamingAsync()
    {
        int hookCalls = 0;
        var member = new ScriptedAgent("worker-stream", (_, turn) => $"bad-stream-{turn}");
        var toolBreaker = new VerificationCircuitBreaker(maxConsecutiveRetries: 1);
        var verifyTool = new SelfVerificationTool(
            hook: DelegateVerificationHook.Create("strict-stream-tool", _ =>
            {
                hookCalls++;
                return VerificationResult.Fail("Streaming tool verification failed");
            }),
            circuitBreaker: toolBreaker);

        var options = new VerificationOptions
        {
            MaxConsecutiveVerificationRetries = 5,
            HaltOnCircuitBreaker = true,
            ThrowOnCircuitBreakerTripped = false
        };

        var loop = new LoopAgent("loop-ext-stream", "External tool breaker streaming test", member, options, maxTurns: 6);
        loop.UseVerificationTool((ITool)verifyTool);

        var chunks = new List<string>();
        await foreach (var chunk in loop.RunStreamingAsync("Start", new StubLlmService(), sessionId: "sess-ext-stream"))
        {
            chunks.Add(chunk);
        }

        Assert.Equal(2, member.ReceivedPrompts.Count);
        Assert.Equal(2, hookCalls);
        Assert.True(toolBreaker.IsTripped);
        Assert.Equal(2, toolBreaker.ConsecutiveFailures);
        Assert.NotNull(loop.LastVerificationState);
        Assert.True(loop.LastVerificationState!.IsTripped);
        Assert.Equal(2, loop.LastVerificationState.ConsecutiveFailures);
        Assert.Equal(1, loop.LastVerificationState.MaxConsecutiveRetries);
        Assert.NotNull(loop.LastVerificationResult);
        Assert.True(loop.LastVerificationResult!.CircuitTripped);
        Assert.Equal(VerificationStatus.CircuitBreakerTripped, loop.LastVerificationResult.Status);
        Assert.Contains(chunks, c => c.Contains("Verification Circuit Breaker Tripped", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LoopAgent_UseVerificationTool_WithAttachedBreakerAndThrowOnTripped_ThrowsImmediately()
    {
        var member = new ScriptedAgent("worker-throw", (_, turn) => $"bad-{turn}");
        var toolBreaker = new VerificationCircuitBreaker(maxConsecutiveRetries: 1);
        var verifyTool = new SelfVerificationTool(
            hook: DelegateVerificationHook.Create("strict-throw-tool", _ => VerificationResult.Fail("Always fails")),
            circuitBreaker: toolBreaker);

        var options = new VerificationOptions
        {
            MaxConsecutiveVerificationRetries = 5,
            HaltOnCircuitBreaker = true,
            ThrowOnCircuitBreakerTripped = true
        };

        var loop = new LoopAgent("loop-ext-throw", "External tool breaker throw test", member, options, maxTurns: 6);
        loop.UseVerificationTool((IAgentTool)verifyTool);

        var ex = await Assert.ThrowsAsync<VerificationCircuitBreakerException>(
            () => loop.RunAsync("Start", new StubLlmService(), sessionId: "sess-ext-throw"));

        Assert.Equal(2, member.ReceivedPrompts.Count);
        Assert.True(ex.State.IsTripped);
        Assert.Equal(2, ex.State.ConsecutiveFailures);
        Assert.True(ex.LastResult.CircuitTripped);
        Assert.Equal(VerificationStatus.CircuitBreakerTripped, ex.LastResult.Status);
    }
}
