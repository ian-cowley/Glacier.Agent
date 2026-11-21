namespace Glacier.Agent.Tests;

using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Glacier.Agent.Sandbox;
using Glacier.Agent.Serialization;
using Glacier.Agent.Tools;
using Xunit;

public sealed class SandboxVerificationTests
{
    private static SandboxOptions CreateEchoCommand(string text, int exitCode = 0)
    {
        return OperatingSystem.IsWindows()
            ? new SandboxOptions("cmd.exe", "/c", $"echo {text} & exit /b {exitCode}")
            : new SandboxOptions("/bin/sh", "-c", $"echo {text}; exit {exitCode}");
    }

    [Fact]
    public async Task SandboxVerificationHook_PassingCommand_ReturnsPass()
    {
        var hook = new SandboxVerificationHook(_ => CreateEchoCommand("compile_ok", 0));

        var context = new VerificationContext(
            AgentName: "coder",
            OriginalPrompt: "Write valid code",
            CurrentPrompt: "Verify code",
            CandidateOutput: "Console.WriteLine(1);",
            TurnOrStep: 1);

        VerificationResult result = await hook.VerifyAsync(context);

        Assert.True(result.Passed);
        Assert.Equal(VerificationStatus.Passed, result.Status);
        Assert.Equal("sandbox_verifier", result.VerifierName);
        Assert.Contains("Startup", result.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SandboxVerificationHook_FailingCommand_ReturnsStructuredFailure()
    {
        var hook = new SandboxVerificationHook(_ => OperatingSystem.IsWindows()
            ? new SandboxOptions("cmd.exe", "/c", "echo CS0103 error 1>&2 & exit /b 1")
            : new SandboxOptions("/bin/sh", "-c", "echo CS0103 error >&2; exit 1"));

        var context = new VerificationContext("coder", "p", "p", "bad code", 1);

        VerificationResult result = await hook.VerifyAsync(context);

        Assert.False(result.Passed);
        Assert.Equal(VerificationStatus.Failed, result.Status);
        Assert.Contains("CS0103", result.FailureReason, StringComparison.Ordinal);
        Assert.Single(result.Issues);
        Assert.Equal("sandbox_exit_nonzero", result.Issues[0].Code);
    }

    [Fact]
    public async Task SandboxVerificationHook_TimedOutCommand_ReturnsTimeoutIssue()
    {
        var hook = new SandboxVerificationHook(_ => OperatingSystem.IsWindows()
            ? new SandboxOptions
            {
                ExecutablePath = "cmd.exe",
                Arguments = new[] { "/c", "ping -n 20 127.0.0.1 > nul" },
                Timeout = TimeSpan.FromMilliseconds(150)
            }
            : new SandboxOptions
            {
                ExecutablePath = "/bin/sh",
                Arguments = new[] { "-c", "sleep 20" },
                Timeout = TimeSpan.FromMilliseconds(150)
            });

        var context = new VerificationContext("coder", "p", "p", "infinite loop", 1);

        VerificationResult result = await hook.VerifyAsync(context);

        Assert.False(result.Passed);
        Assert.Equal(VerificationStatus.Failed, result.Status);
        Assert.Single(result.Issues);
        Assert.Equal("sandbox_timeout", result.Issues[0].Code);
    }

    [Fact]
    public async Task SandboxVerificationHook_CustomEvaluator_IsInvoked()
    {
        var hook = new SandboxVerificationHook(
            new OsProcessSandbox(),
            _ => CreateEchoCommand("CUSTOM_TOKEN", 0),
            (exec, _) => exec.StandardOutput.Contains("CUSTOM_TOKEN", StringComparison.Ordinal)
                ? VerificationResult.Pass("Custom evaluator matched.")
                : VerificationResult.Fail("Missing token."),
            name: "custom_sandbox");

        VerificationResult result = await hook.VerifyAsync(new VerificationContext("a", "p", "p", "out", 1));

        Assert.True(result.Passed);
        Assert.Equal("custom_sandbox", result.VerifierName);
        Assert.Equal("Custom evaluator matched.", result.Summary);
    }

    [Fact]
    public async Task SelfVerificationTool_CreateSandboxed_IntegratesWithCircuitBreaker()
    {
        var breaker = new VerificationCircuitBreaker(maxConsecutiveRetries: 1);
        int sandboxInvocations = 0;

        var tool = SelfVerificationTool.CreateSandboxed(
            ctx =>
            {
                Interlocked.Increment(ref sandboxInvocations);
                return ctx.CandidateOutput.Contains("VALID", StringComparison.Ordinal)
                    ? CreateEchoCommand("ok", 0)
                    : CreateEchoCommand("bad", 2);
            },
            circuitBreaker: breaker);

        Assert.NotNull(tool.SandboxExecutor);

        string payloadFail = JsonSerializer.Serialize(
            new SelfVerificationToolArgs { Output = "BROKEN_1" },
            AdkJsonContext.Default.SelfVerificationToolArgs);

        // 1st failure: recorded, breaker remains closed
        VerificationResult r1 = await tool.VerifyArgumentsAsync(payloadFail);
        Assert.False(r1.Passed);
        Assert.Equal(VerificationStatus.Failed, r1.Status);
        Assert.False(r1.CircuitTripped);
        Assert.Equal(1, r1.ConsecutiveFailures);
        Assert.Equal(1, sandboxInvocations);

        // 2nd failure: exceeds maxConsecutiveRetries=1, trips breaker
        VerificationResult r2 = await tool.VerifyArgumentsAsync(payloadFail);
        Assert.False(r2.Passed);
        Assert.Equal(VerificationStatus.CircuitBreakerTripped, r2.Status);
        Assert.True(r2.CircuitTripped);
        Assert.Equal(2, r2.ConsecutiveFailures);
        Assert.Equal(2, sandboxInvocations);

        // 3rd call while tripped: short-circuits without spawning sandbox
        VerificationResult r3 = await tool.VerifyArgumentsAsync(payloadFail);
        Assert.True(r3.CircuitTripped);
        Assert.Equal(2, sandboxInvocations);

        // Reset breaker and verify passing candidate via IAgentTool.ExecuteAsync
        breaker.Reset();
        string payloadPass = JsonSerializer.Serialize(
            new SelfVerificationToolArgs { Output = "VALID_CODE" },
            AdkJsonContext.Default.SelfVerificationToolArgs);

        string jsonOut = await ((IAgentTool)tool).ExecuteAsync(payloadPass);
        VerificationResult? r4 = JsonSerializer.Deserialize(jsonOut, AdkJsonContext.Default.VerificationResult);
        Assert.NotNull(r4);
        Assert.True(r4.Passed);
        Assert.Equal(0, r4.ConsecutiveFailures);
        Assert.Equal(3, sandboxInvocations);
    }

    [Fact]
    public void AdkJsonContext_SerializesAndDeserializesSandboxExecutionResult()
    {
        var original = new SandboxExecutionResult
        {
            ExitCode = 0,
            StandardOutput = "ok",
            StandardError = string.Empty,
            StartupLatency = TimeSpan.FromMilliseconds(3.25),
            TotalDuration = TimeSpan.FromMilliseconds(12.5),
            TimedOut = false,
            IsolationMechanism = "WindowsJobObject+RestrictedToken"
        };

        string json = JsonSerializer.Serialize(original, AdkJsonContext.Default.SandboxExecutionResult);
        SandboxExecutionResult? roundTrip = JsonSerializer.Deserialize(json, AdkJsonContext.Default.SandboxExecutionResult);

        Assert.NotNull(roundTrip);
        Assert.Equal(original.ExitCode, roundTrip.ExitCode);
        Assert.Equal(original.StandardOutput, roundTrip.StandardOutput);
        Assert.Equal(original.StartupLatency, roundTrip.StartupLatency);
        Assert.Equal(original.TotalDuration, roundTrip.TotalDuration);
        Assert.Equal(original.TimedOut, roundTrip.TimedOut);
        Assert.Equal(original.IsolationMechanism, roundTrip.IsolationMechanism);
        Assert.True(roundTrip.Succeeded);
    }
}
