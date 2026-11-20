using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Glacier.Agent.Llm;
using Glacier.Agent.Runtime;
using Glacier.Agent.Serialization;
using Glacier.Agent.Tools;
using Glacier.Agent.Workflows;
using Xunit;

namespace Glacier.Agent.Tests.E2E;

[Trait("Suite", "UpgradesE2E")]
public sealed class AgentWorkflowScenarioE2ETests
{
    [Fact]
    [Trait("Tier", "Tier3")]
    public async Task T3_16_LoopAgent_Engine_LlmAgent_SharedVerificationAndAdkJsonContextIntegration()
    {
        var sharedOptions = new VerificationOptions
        {
            MaxConsecutiveVerificationRetries = 2,
            AutoInvokeRegisteredVerificationTool = true,
            HaltOnCircuitBreaker = true,
            AppendStructuredFeedbackToPrompt = true
        };

        // Part A: LoopAgent delegating to AgentExecutionEngine with shared VerificationOptions and SelfVerificationTool
        var sharedBreaker = new VerificationCircuitBreaker(sharedOptions.MaxConsecutiveVerificationRetries);
        var sharedHook = new ScriptedVerificationHook("shared-contract", (ctx, _) =>
            ctx.CandidateOutput.Contains("VERIFIED_OK", StringComparison.Ordinal)
                ? VerificationResult.Pass("Contract satisfied", "shared-contract")
                : VerificationResult.Fail("Missing VERIFIED_OK marker", suggestedFix: "Emit VERIFIED_OK"));
        var sharedVerifyTool = new SelfVerificationTool(sharedHook, sharedBreaker, name: "self_verify");

        int engineSteps = 0;
        using var engine = new AgentExecutionEngine("t3-engine", (ctx, _, _) =>
        {
            engineSteps++;
            return ValueTask.FromResult(new AgentStepDecision(
                Output: string.Empty,
                IsCompleted: false,
                ToolName: SelfVerificationTool.DefaultToolName,
                ToolArgumentsJson: $"{{\"output\":\"unverified-draft-{engineSteps}\"}}"));
        });
        engine.ConfigureVerification(sharedOptions);
        engine.RegisterVerificationTool(sharedVerifyTool);

        var capturedEvents = new List<AgentEvent>();
        engine.EventPublished += e => capturedEvents.Add(e);

        var engineBridge = new EngineBridgeWorkflowAgent(engine);
        var loopOverEngine = new LoopAgent(
            "t3-loop-engine",
            "LoopAgent delegating to AgentExecutionEngine",
            engineBridge,
            sharedOptions,
            verificationHook: null,
            maxTurns: 6,
            stopCondition: _ => engineBridge.LastResponse is { IsVerificationCircuitBreakerTripped: true });

        using var bridgeLlm = new StubLlmService();
        string loopEngineOutput = await loopOverEngine.RunAsync("Run engine-backed verification loop", bridgeLlm, "t3-16-shared-session");

        Assert.Equal(3, engineSteps);
        Assert.Equal(3, engineBridge.StepResponses.Count);
        Assert.True(engineBridge.LastResponse!.Value.IsVerificationCircuitBreakerTripped);
        Assert.True(sharedBreaker.IsTripped);
        Assert.Equal(3, sharedBreaker.ConsecutiveFailures);
        Assert.Equal(3, sharedBreaker.TotalFailures);
        Assert.Single(capturedEvents, e => e.EventType == "VerificationCircuitBreakerTripped");

        var loopEngineResult = JsonSerializer.Deserialize(loopEngineOutput, AdkJsonContext.Default.VerificationResult);
        Assert.NotNull(loopEngineResult);
        Assert.True(loopEngineResult!.CircuitTripped);
        Assert.Equal(VerificationStatus.CircuitBreakerTripped, loopEngineResult.Status);

        // Verify all emitted verification events deserialize cleanly via AdkJsonContext
        int parsedVerificationEvents = 0;
        foreach (var ev in capturedEvents)
        {
            if (ev.EventType.StartsWith("Verification", StringComparison.Ordinal))
            {
                var payload = JsonSerializer.Deserialize(ev.Payload, AdkJsonContext.Default.VerificationEventPayload);
                Assert.NotNull(payload);
                Assert.Equal("t3-engine", payload!.AgentName);
                Assert.Equal(sharedOptions.MaxConsecutiveVerificationRetries, payload.MaxConsecutiveRetries);
                parsedVerificationEvents++;
            }
        }
        Assert.True(parsedVerificationEvents >= 4);

        // Part B: LoopAgent wrapping LlmAgent with SelfVerificationTool sharing VerificationOptions
        var llmSelfVerify = new SelfVerificationTool(
            new ScriptedVerificationHook("llm-hook", (_, _) => VerificationResult.Fail("Failing tool output")),
            new VerificationCircuitBreaker(sharedOptions.MaxConsecutiveVerificationRetries));

        var llmAgent = new LlmAgent(
            "t3-llm-member",
            "local-qwen-mock",
            "Self-verifying LLM member",
            "Always verify output.",
            tools: new List<ITool> { llmSelfVerify })
        {
            VerificationOptions = sharedOptions
        };

        var infiniteVerifyLlm = new StubLlmService((_, callIdx) => new LlmResponse
        {
            FunctionCalls = new List<LlmFunctionCall>
            {
                new()
                {
                    Id = $"call-{callIdx}",
                    Name = SelfVerificationTool.DefaultToolName,
                    Args = JsonNode.Parse("{\"output\":\"stuck\"}")
                }
            }
        });

        var loopWithLlm = new LoopAgent(
            "t3-loop-llm",
            "LoopAgent + LlmAgent verification propagation",
            llmAgent,
            sharedOptions,
            verificationHook: null,
            maxTurns: 5);
        var loopEvents = new List<AgentEvent>();
        loopWithLlm.EventPublished += e => loopEvents.Add(e);

        string haltedJson = await loopWithLlm.RunAsync("Solve and verify", infiniteVerifyLlm, "t3-16-llm-session");

        Assert.Equal(3, infiniteVerifyLlm.CallCount);
        Assert.NotNull(loopWithLlm.LastVerificationState);
        Assert.True(loopWithLlm.LastVerificationState!.IsTripped);
        Assert.Equal(3, loopWithLlm.LastVerificationState.ConsecutiveFailures);
        Assert.Single(loopEvents, e => e.EventType == "VerificationCircuitBreakerTripped");

        var haltedResult = JsonSerializer.Deserialize(haltedJson, AdkJsonContext.Default.VerificationResult);
        Assert.NotNull(haltedResult);
        Assert.True(haltedResult!.CircuitTripped);
        Assert.Equal(VerificationStatus.CircuitBreakerTripped, haltedResult.Status);
    }

    [Fact]
    [Trait("Tier", "Tier4")]
    public async Task T4_05_AutonomousSelfVerifyingCodingWorkflow_RecoverThenTripBreaker()
    {
        var workflowOptions = new VerificationOptions
        {
            MaxConsecutiveVerificationRetries = 2,
            StopOnVerificationPass = true,
            AutoInvokeRegisteredVerificationTool = true,
            HaltOnCircuitBreaker = true,
            AppendStructuredFeedbackToPrompt = true
        };

        // Realistic coding output verifier: checks valid JSON, required fields, and unit test assertion marker.
        var codeQualityHook = new ScriptedVerificationHook("coding-ci-check", (ctx, _) =>
        {
            try
            {
                using var doc = JsonDocument.Parse(ctx.CandidateOutput);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    return VerificationResult.Fail("Output must be a JSON artifact.", verifierName: "coding-ci-check");
                }

                if (!root.TryGetProperty("code", out var codeEl) || codeEl.ValueKind != JsonValueKind.String)
                {
                    return VerificationResult.Fail(
                        "Missing 'code' field.",
                        new[] { new VerificationIssue("missing_code", "Artifact requires 'code'.", "code", "Add 'code' field.") },
                        "Include a C# 'code' property.",
                        "coding-ci-check");
                }

                string code = codeEl.GetString() ?? string.Empty;
                if (!code.Contains("Assert.", StringComparison.Ordinal))
                {
                    return VerificationResult.Fail(
                        "Generated test suite lacks assertions.",
                        new[] { new VerificationIssue("missing_assert", "No 'Assert.' call found in test code.", "code", "Add xUnit Assert.* check.") },
                        "Add at least one Assert.* verification statement.",
                        "coding-ci-check");
                }

                if (!root.TryGetProperty("testsPassed", out var passEl) || passEl.ValueKind != JsonValueKind.True)
                {
                    return VerificationResult.Fail(
                        "Tests have not passed yet.",
                        new[] { new VerificationIssue("tests_failed", "'testsPassed' must be true.", "testsPassed", "Fix code and re-run tests.") },
                        "Ensure testsPassed is true.",
                        "coding-ci-check");
                }

                return VerificationResult.Pass("CI build & unit tests passed.", "coding-ci-check");
            }
            catch (JsonException ex)
            {
                return VerificationResult.Fail(
                    $"Artifact is not valid JSON: {ex.Message}",
                    new[] { new VerificationIssue("invalid_json", ex.Message, "output", "Return a JSON object.") },
                    "Return valid JSON.",
                    "coding-ci-check");
            }
        });

        var codingTool = new SelfVerificationTool(codeQualityHook, name: SelfVerificationTool.DefaultToolName);

        // Task 1: Autonomous coding agent fails attempt 1 (plain text), fails attempt 2 (missing Assert.), self-corrects on attempt 3
        using var task1Engine = new AgentExecutionEngine("coding-agent-task1", (ctx, _, _) =>
        {
            string candidate = ctx.StepCount switch
            {
                1 => "public int Add(int a, int b) => a + b;", // Invalid: not JSON
                2 => "{\"language\":\"csharp\",\"code\":\"public void TestAdd() { var x = 1 + 1; }\",\"testsPassed\":false}", // Missing Assert. & testsPassed=false
                _ => "{\"language\":\"csharp\",\"code\":\"public void TestAdd() { Assert.Equal(2, 1 + 1); }\",\"testsPassed\":true}" // Self-corrected!
            };
            return ValueTask.FromResult(new AgentStepDecision(candidate, IsCompleted: true));
        });
        task1Engine.ConfigureVerification(workflowOptions);
        task1Engine.RegisterVerificationTool(codingTool);

        var task1Events = new List<AgentEvent>();
        task1Engine.EventPublished += e => task1Events.Add(e);

        var task1Response = await task1Engine.ExecuteLoopAsync(
            "t4-05-task1",
            "Implement Add() with xUnit test JSON artifact",
            maxSteps: 6);

        Assert.True(task1Response.IsCompleted);
        Assert.Equal(VerificationStatus.Passed, task1Response.VerificationStatus);
        Assert.False(task1Response.IsVerificationCircuitBreakerTripped);
        Assert.True(task1Response.VerificationResult!.Passed);
        Assert.Contains("Assert.Equal(2, 1 + 1)", task1Response.Output, StringComparison.Ordinal);

        var task1Breaker = task1Engine.GetCircuitBreaker("t4-05-task1")!;
        Assert.False(task1Breaker.IsTripped);
        Assert.Equal(0, task1Breaker.ConsecutiveFailures);
        Assert.Equal(2, task1Breaker.TotalFailures);
        Assert.Equal(1, task1Breaker.TotalPasses);

        // Task 2: Hopeless coding task wrapped in LoopAgent trips the consecutive circuit breaker cleanly
        var hopelessMember = new ScriptedWorkflowAgent("coding-agent-task2", (_, turn) =>
            $"{{\"language\":\"csharp\",\"code\":\"// broken draft {turn}\",\"testsPassed\":false}}");
        var task2Loop = new LoopAgent(
            "t4-05-task2-loop",
            "Hopeless coding workflow",
            hopelessMember,
            workflowOptions,
            codeQualityHook,
            maxTurns: 10);

        var task2Events = new List<AgentEvent>();
        task2Loop.EventPublished += e => task2Events.Add(e);

        using var llm = new StubLlmService();
        string task2Output = await task2Loop.RunAsync("Solve unsolvable constraint", llm, "t4-05-task2");

        Assert.Equal(3, hopelessMember.InvocationCount);
        Assert.NotNull(task2Loop.LastVerificationState);
        Assert.True(task2Loop.LastVerificationState!.IsTripped);
        Assert.Equal(CircuitBreakerStatus.Open, task2Loop.LastVerificationState.Status);
        Assert.Equal(3, task2Loop.LastVerificationState.ConsecutiveFailures);
        Assert.Single(task2Events, e => e.EventType == "VerificationCircuitBreakerTripped");

        // Validate AOT JSON telemetry across both Task 1 and Task 2
        var task2Result = JsonSerializer.Deserialize(task2Output, AdkJsonContext.Default.VerificationResult);
        Assert.NotNull(task2Result);
        Assert.True(task2Result!.CircuitTripped);
        Assert.Equal(VerificationStatus.CircuitBreakerTripped, task2Result.Status);

        var allEvents = new List<AgentEvent>();
        allEvents.AddRange(task1Events);
        allEvents.AddRange(task2Events);
        foreach (var ev in allEvents)
        {
            if (ev.EventType.StartsWith("Verification", StringComparison.Ordinal))
            {
                var telemetryPayload = JsonSerializer.Deserialize(ev.Payload, AdkJsonContext.Default.VerificationEventPayload);
                Assert.NotNull(telemetryPayload);
            }
        }
    }
}
