namespace Glacier.Agent.Tools;

using System;
using System.Threading;
using System.Threading.Tasks;
using Glacier.Agent.Sandbox;

/// <summary>
/// <see cref="IVerificationHook"/> backed by an <see cref="ISandboxExecutor"/> (such as <see cref="OsProcessSandbox"/>)
/// for running isolated code/compiler/test verification commands.
/// </summary>
public sealed class SandboxVerificationHook : IVerificationHook
{
    private readonly Func<VerificationContext, SandboxOptions> _optionsFactory;
    private readonly Func<SandboxExecutionResult, VerificationContext, VerificationResult>? _resultEvaluator;

    public string Name { get; }
    public ISandboxExecutor SandboxExecutor { get; }

    public SandboxVerificationHook(
        Func<VerificationContext, SandboxOptions> optionsFactory,
        Func<SandboxExecutionResult, VerificationContext, VerificationResult>? resultEvaluator = null,
        string name = "sandbox_verifier")
        : this(new OsProcessSandbox(), optionsFactory, resultEvaluator, name)
    {
    }

    public SandboxVerificationHook(
        ISandboxExecutor sandboxExecutor,
        Func<VerificationContext, SandboxOptions> optionsFactory,
        Func<SandboxExecutionResult, VerificationContext, VerificationResult>? resultEvaluator = null,
        string name = "sandbox_verifier")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        SandboxExecutor = sandboxExecutor ?? throw new ArgumentNullException(nameof(sandboxExecutor));
        _optionsFactory = optionsFactory ?? throw new ArgumentNullException(nameof(optionsFactory));
        _resultEvaluator = resultEvaluator;
        Name = name;
    }

    public async ValueTask<VerificationResult> VerifyAsync(VerificationContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        SandboxOptions options = _optionsFactory(context)
            ?? throw new InvalidOperationException("Sandbox options factory returned null.");

        SandboxExecutionResult execResult = await SandboxExecutor.ExecuteAsync(options, ct).ConfigureAwait(false);

        if (_resultEvaluator != null)
        {
            VerificationResult custom = _resultEvaluator(execResult, context)
                ?? throw new InvalidOperationException("Sandbox result evaluator returned null.");
            return string.IsNullOrWhiteSpace(custom.VerifierName)
                ? custom with { VerifierName = Name }
                : custom;
        }

        if (execResult.TimedOut)
        {
            return VerificationResult.Fail(
                $"Sandbox verification timed out after {options.Timeout.TotalMilliseconds:F0} ms.",
                new[]
                {
                    new VerificationIssue(
                        "sandbox_timeout",
                        $"Sandboxed command '{options.ExecutablePath}' exceeded timeout ({options.Timeout.TotalMilliseconds:F0} ms).",
                        "timeout",
                        "Reduce execution time or avoid infinite loops.")
                },
                "Reduce execution time or increase the sandbox timeout.",
                Name);
        }

        if (execResult.ExitCode != 0)
        {
            string diagnostic = !string.IsNullOrWhiteSpace(execResult.StandardError)
                ? execResult.StandardError.Trim()
                : (!string.IsNullOrWhiteSpace(execResult.StandardOutput)
                    ? execResult.StandardOutput.Trim()
                    : $"Command exited with code {execResult.ExitCode}.");

            return VerificationResult.Fail(
                $"Sandbox verification failed (exit code {execResult.ExitCode}): {diagnostic}",
                new[]
                {
                    new VerificationIssue(
                        "sandbox_exit_nonzero",
                        diagnostic,
                        "exitCode",
                        "Fix the compilation, syntax, or runtime error reported by the verifier.")
                },
                "Fix the errors reported by the sandboxed verification command.",
                Name);
        }

        return VerificationResult.Pass(
            $"Sandbox verification passed via {execResult.IsolationMechanism} in {execResult.TotalDuration.TotalMilliseconds:F1} ms (startup {execResult.StartupLatency.TotalMilliseconds:F2} ms).",
            Name);
    }
}
