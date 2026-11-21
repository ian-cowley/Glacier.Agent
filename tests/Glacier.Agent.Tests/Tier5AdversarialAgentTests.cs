using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Glacier.Agent.Llm;
using Glacier.Agent.Runtime;
using Glacier.Agent.Sandbox;
using Glacier.Agent.Serialization;
using Glacier.Agent.Tools;
using Glacier.Agent.Workflows;
using Xunit;

namespace Glacier.Agent.Tests;

[Trait("Tier", "Tier5")]
public sealed class Tier5AdversarialAgentTests
{
    private sealed class StubLlmService : ILlmService
    {
        private readonly Queue<LlmResponse> _responses;
        public StubLlmService(params LlmResponse[] responses) => _responses = new(responses);
        public Task<LlmResponse> GenerateContentAsync(LlmRequest request)
            => Task.FromResult(_responses.Count > 0 ? _responses.Dequeue() : new LlmResponse { Content = "done" });
        public async IAsyncEnumerable<LlmResponse> StreamGenerateContentAsync(LlmRequest request)
        {
            yield return await GenerateContentAsync(request).ConfigureAwait(false);
        }
    }

    private sealed class ScriptedAgent : IAgent
    {
        private readonly Func<string, int, string> _fn;
        private int _turn;
        public string Name { get; }
        public string Description => "Scripted test agent";

        public ScriptedAgent(string name, Func<string, int, string> fn)
        {
            Name = name;
            _fn = fn;
        }

        public Task<string> RunAsync(string prompt, ILlmService llmService, string? sessionId = null)
            => Task.FromResult(_fn(prompt, ++_turn));

        public async IAsyncEnumerable<string> RunStreamingAsync(string prompt, ILlmService llmService, string? sessionId = null)
        {
            yield return _fn(prompt, ++_turn);
            await Task.CompletedTask;
        }
    }

    private sealed class HelperAgentTool : IAgentTool
    {
        public string Name => "helper";
        public string Description => "Helper";
        public string ParameterJsonSchema => "{\"type\":\"object\"}";
        public ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
            => ValueTask.FromResult("helper-out");
    }

    private sealed class PascalCaseVerifyTool : IAgentTool, IVerificationTool
    {
        public string Name => "pascal_verify";
        public string Description => "PascalCase verifier";
        public string ParameterJsonSchema => "{\"type\":\"object\"}";
        public ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
            => ValueTask.FromResult("{\"Passed\":true,\"Summary\":\"All good\"}");
    }

    [Fact]
    public async Task Tier5_ExecuteLoopAsync_MaxStepsExhaustedWithoutIsCompleted_MustNotBypassVerificationHooks()
    {
        int hookCalls = 0;
        using var engine = new AgentExecutionEngine("t5-bypass", (_, _, _) =>
            ValueTask.FromResult(new AgentStepDecision("step", IsCompleted: false, ToolName: "helper", ToolArgumentsJson: "{}")));
        engine.RegisterTool(new HelperAgentTool());
        engine.AttachVerificationHook("strict", (_, _) =>
        {
            hookCalls++;
            return ValueTask.FromResult(VerificationResult.Fail("must fail"));
        });

        var resp = await engine.ExecuteLoopAsync("s-bypass", "go", maxSteps: 2);

        Assert.True(hookCalls > 0);
        Assert.False(resp.IsCompleted);
        Assert.NotEqual(VerificationStatus.None, resp.VerificationStatus);
    }

    [Fact]
    public async Task Tier5_SelfVerificationTool_And_VerificationResultParser_AcceptPascalCaseJson()
    {
        var tool = new SelfVerificationTool();
        var argsRes = await tool.VerifyArgumentsAsync("{\"Output\":\"valid-text\"}");
        Assert.True(argsRes.Passed, $"PascalCase args rejected: {argsRes.FailureReason}");

        using var engine = new AgentExecutionEngine("t5-pascal", (_, _, _) =>
            ValueTask.FromResult(new AgentStepDecision("", IsCompleted: true, ToolName: "pascal_verify", ToolArgumentsJson: "{}")));
        engine.RegisterVerificationTool(new PascalCaseVerifyTool());

        var stepResp = await engine.ExecuteStepAsync(new AgentContext("s-pascal", "go", 1));
        Assert.Equal(VerificationStatus.Passed, stepResp.VerificationStatus);
    }

    [Fact]
    public async Task Tier5_LlmAgent_BeforeToolCallException_MustUpdateAttachedSelfVerificationBreaker()
    {
        var toolBreaker = new VerificationCircuitBreaker(maxConsecutiveRetries: 1);
        var verifyTool = new SelfVerificationTool(circuitBreaker: toolBreaker);
        var failCall = new LlmFunctionCall { Id = "1", Name = "self_verify", Args = new JsonObject { ["output"] = "" } };
        var llm = new StubLlmService(
            new LlmResponse { FunctionCalls = new List<LlmFunctionCall> { failCall } },
            new LlmResponse { FunctionCalls = new List<LlmFunctionCall> { failCall } },
            new LlmResponse { Content = "escaped!" });

        var agent = new LlmAgent("t5-desync", "m", "d", "i", new List<ITool> { verifyTool })
        {
            VerificationOptions = new VerificationOptions { MaxConsecutiveVerificationRetries = 1 }
        };
        int callNum = 0;
        agent.BeforeToolCall = (_, args) =>
        {
            callNum++;
            if (callNum == 2) throw new InvalidOperationException("interceptor boom");
            return Task.FromResult(args);
        };

        string result = await agent.RunAsync("hi", llm);
        Assert.NotEqual("escaped!", result);
        Assert.True(toolBreaker.IsTripped);
        Assert.True(agent.LastVerificationState?.IsTripped == true);
    }

    [Fact]
    public async Task Tier5_LoopAgent_MaxTurnsLessOrEqualMaxRetries_DoesNotSilentlyReturnUnverifiedOutput()
    {
        var member = new ScriptedAgent("always-bad", (_, _) => "bad-candidate");
        var hook = DelegateVerificationHook.Create("strict", _ => VerificationResult.Fail("validation failed"));
        var opts = new VerificationOptions
        {
            MaxConsecutiveVerificationRetries = 5,
            HaltOnCircuitBreaker = true
        };

        var loop = new LoopAgent("loop-maxturns", "Unverified halt check", member, opts, hook, maxTurns: 2);
        string result = await loop.RunAsync("go", new StubLlmService());

        Assert.NotEqual("bad-candidate", result);
        var parsed = JsonSerializer.Deserialize(result, AdkJsonContext.Default.VerificationResult);
        Assert.NotNull(parsed);
        Assert.False(parsed!.Passed);
        Assert.False(loop.LastVerificationState!.IsTripped);

        var streamLoop = new LoopAgent("loop-maxturns-stream", "Stream unverified check", new ScriptedAgent("bad", (_, _) => "bad-candidate"), opts, hook, maxTurns: 2);
        var chunks = new List<string>();
        await foreach (string chunk in streamLoop.RunStreamingAsync("go", new StubLlmService()))
        {
            chunks.Add(chunk);
        }
        Assert.Equal(2, chunks.Count(c => c.Contains("--- Verification Failed", StringComparison.Ordinal)));
    }

    [Fact]
    public void Tier5_ParseModelOrStructuredDirective_NullInput_DoesNotThrowNullReference()
    {
        var decision = AgentExecutionEngine.ParseModelOrStructuredDirective(null!);
        Assert.True(decision.IsCompleted);
    }

    [Fact]
    public async Task Tier5_AgentExecutionEngine_ConcurrentMultiSessionIsolation_NoCrossSessionContamination()
    {
        using var engine = new AgentExecutionEngine("t5-concurrent", (ctx, _, _) =>
            ValueTask.FromResult(new AgentStepDecision(
                ctx.StepCount < 3 ? $"bad-{ctx.SessionId}" : $"good-{ctx.SessionId}",
                IsCompleted: true)));
        engine.ConfigureVerification(new VerificationOptions { MaxConsecutiveVerificationRetries = 3 });
        engine.AttachVerificationHook("per-session", (ctx, _) => ValueTask.FromResult(
            ctx.CandidateOutput.StartsWith("good-", StringComparison.Ordinal)
                ? VerificationResult.Pass("ok")
                : VerificationResult.Fail($"fail {ctx.SessionId}")));

        var responses = await Task.WhenAll(
            Enumerable.Range(0, 12)
                .Select(i => engine.ExecuteLoopAsync($"sess-{i}", $"task-{i}", maxSteps: 5).AsTask()));

        Assert.All(responses, r =>
        {
            Assert.True(r.IsCompleted);
            Assert.Equal(VerificationStatus.Passed, r.VerificationStatus);
            Assert.False(r.IsVerificationCircuitBreakerTripped);
        });
    }

    [Fact]
    public async Task Tier5_SelfVerificationTool_MalformedAndNullJsonArguments_ReturnStructuredFailure()
    {
        var tool = new SelfVerificationTool();
        var nullRes = await tool.VerifyArgumentsAsync("null");
        Assert.False(nullRes.Passed);
        Assert.Contains(nullRes.Issues, i => i.Code == "empty_output");

        var arrayRes = await tool.VerifyArgumentsAsync("[1, 2, 3]");
        Assert.False(arrayRes.Passed);
        Assert.Contains(arrayRes.Issues, i => i.Code == "invalid_arguments");

        var overrideRes = await tool.VerifyArgumentsAsync(
            "{\"output\":\"plain text\",\"requireValidJson\":false,\"requiredFields\":[\"missing\"]}");
        Assert.True(overrideRes.Passed);
    }

    [Fact]
    public async Task Tier5_AgentExecutionEngine_SelfVerificationTool_HookThrowsWithTreatAsFalse_UpdatesExternalCircuitBreaker()
    {
        var extBreaker1 = new VerificationCircuitBreaker(maxConsecutiveRetries: 1);
        int callCount1 = 0;
        var throwingHook1 = DelegateVerificationHook.Create("throwing-1", _ =>
        {
            callCount1++;
            if (callCount1 == 1)
                throw new InvalidOperationException("hook threw on step 1");
            return VerificationResult.Fail("normal failure on step 2");
        });
        var tool1 = new SelfVerificationTool(throwingHook1, extBreaker1, treatHookExceptionAsFailure: false);

        using var engine1 = new AgentExecutionEngine("t5-ext-explicit", (_, _, _) =>
            ValueTask.FromResult(new AgentStepDecision("", IsCompleted: true, ToolName: "self_verify", ToolArgumentsJson: "{\"output\":\"x\"}")));
        engine1.ConfigureVerification(new VerificationOptions
        {
            MaxConsecutiveVerificationRetries = 1,
            HaltOnCircuitBreaker = true,
            TreatHookExceptionAsFailure = true
        });
        engine1.RegisterVerificationTool(tool1);

        var resp1 = await engine1.ExecuteLoopAsync("s-explicit", "go", maxSteps: 5);
        Assert.Equal(VerificationStatus.CircuitBreakerTripped, resp1.VerificationStatus);
        Assert.True(extBreaker1.IsTripped);
        Assert.Equal(2, extBreaker1.ConsecutiveFailures);

        var extBreaker2 = new VerificationCircuitBreaker(maxConsecutiveRetries: 1);
        var throwingHook2 = DelegateVerificationHook.Create("throwing-2", _ =>
            throw new InvalidOperationException("always throws"));
        var tool2 = new SelfVerificationTool(throwingHook2, extBreaker2, treatHookExceptionAsFailure: false);

        using var engine2 = new AgentExecutionEngine("t5-ext-auto", (_, _, _) =>
            ValueTask.FromResult(new AgentStepDecision("candidate", IsCompleted: true)));
        engine2.ConfigureVerification(new VerificationOptions
        {
            MaxConsecutiveVerificationRetries = 1,
            HaltOnCircuitBreaker = true,
            AutoInvokeRegisteredVerificationTool = true,
            TreatHookExceptionAsFailure = true
        });
        engine2.RegisterVerificationTool(tool2);

        var resp2 = await engine2.ExecuteLoopAsync("s-auto", "go", maxSteps: 5);
        Assert.Equal(VerificationStatus.CircuitBreakerTripped, resp2.VerificationStatus);
        Assert.True(extBreaker2.IsTripped);
        Assert.Equal(2, extBreaker2.ConsecutiveFailures);
    }
}

[CollectionDefinition("ProcessAndTimingSensitive", DisableParallelization = true)]
public sealed class ProcessAndTimingSensitiveCollection
{
}

[Trait("Tier", "Tier5")]
[Collection("ProcessAndTimingSensitive")]
public sealed class Tier5SandboxContentionTests
{
    [Fact]
    public async Task Tier5_OsProcessSandbox_ConcurrentExecutionUnderGcAndThreadPoolContention_NeverDeadlocks()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var sandbox = new OsProcessSandbox();
        string exe = File.Exists("/bin/true") ? "/bin/true" : "/bin/sh";
        string[] args = exe == "/bin/true" ? Array.Empty<string>() : new[] { "-c", "exit 0" };
        var options = new SandboxOptions
        {
            ExecutablePath = exe,
            Arguments = args,
            EnableNamespaceIsolation = true,
            AllowNetwork = false,
            Timeout = TimeSpan.FromSeconds(10)
        };

        using var stopGcCts = new CancellationTokenSource(TimeSpan.FromSeconds(25));

        // Aggressively induce GC suspension (g_SuspendMutex / SuspendEE) and ThreadPool churn
        // while concurrent clone3/fork sandbox spawns execute.
        Task[] gcChurnTasks = Enumerable.Range(0, 4).Select(workerId => Task.Run(async () =>
        {
            int iter = 0;
            while (!stopGcCts.IsCancellationRequested)
            {
                byte[] junk = new byte[16 * 1024 + (iter & 255)];
                junk[0] = (byte)(workerId + iter);
                if ((iter++ & 7) == 0)
                {
                    GC.Collect(iter % 3, GCCollectionMode.Forced, blocking: false);
                }
                await Task.Yield();
            }
        }, CancellationToken.None)).ToArray();

        try
        {
            const int concurrentWorkers = 4;
            const int spawnsPerWorker = 15;
            Task[] spawnTasks = Enumerable.Range(0, concurrentWorkers).Select(_ => Task.Run(async () =>
            {
                for (int i = 0; i < spawnsPerWorker; i++)
                {
                    SandboxExecutionResult result = await sandbox.ExecuteAsync(options, stopGcCts.Token);
                    Assert.True(result.Succeeded, $"Sandbox spawn failed under GC contention: exit={result.ExitCode}, err={result.StandardError}");
                    Assert.False(result.TimedOut);
                }
            }, stopGcCts.Token)).ToArray();

            await Task.WhenAll(spawnTasks);
        }
        finally
        {
            stopGcCts.Cancel();
            await Task.WhenAll(gcChurnTasks);
        }

        if (RuntimeInformation.ProcessArchitecture == Architecture.X64)
        {
            string maps = await File.ReadAllTextAsync("/proc/self/maps");
            Assert.Contains("libglacier_sandbox.so", maps, StringComparison.Ordinal);
        }
    }
}
