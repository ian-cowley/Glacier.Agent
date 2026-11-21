namespace Glacier.Agent.Sandbox;

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Configuration for executing a process inside an OS-isolated sandbox.
/// </summary>
public sealed class SandboxOptions
{
    public SandboxOptions()
    {
    }

    [SetsRequiredMembers]
    public SandboxOptions(string executablePath, params IReadOnlyList<string> arguments)
    {
        ExecutablePath = executablePath;
        Arguments = arguments ?? Array.Empty<string>();
    }

    /// <summary>
    /// Executable path or command name resolved via <c>PATH</c>.
    /// </summary>
    public required string ExecutablePath { get; init; } = string.Empty;

    /// <summary>
    /// Argument list passed to the executable.
    /// </summary>
    public IReadOnlyList<string> Arguments { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Optional working directory for the sandboxed process.
    /// </summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>
    /// Maximum wall-clock execution timeout. <see cref="TimeSpan.Zero"/> disables the timeout.
    /// </summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Optional memory limit in bytes enforced by Windows Job Objects or Linux <c>RLIMIT_DATA</c>.
    /// </summary>
    public long? MemoryLimitBytes { get; init; }

    /// <summary>
    /// Optional environment variable overrides merged onto the inherited environment.
    /// </summary>
    public IReadOnlyDictionary<string, string>? EnvironmentVariables { get; init; }

    /// <summary>
    /// When <see langword="false"/> (default), isolates network access via <c>CLONE_NEWNET</c> on Linux.
    /// </summary>
    public bool AllowNetwork { get; init; } = false;

    /// <summary>
    /// Optional process/task limit enforced via Windows Job Object active-process limit or Linux <c>RLIMIT_NPROC</c>.
    /// </summary>
    public int? MaxProcesses { get; init; }

    /// <summary>
    /// Enables OS namespace isolation on Linux (<c>clone3</c>/<c>unshare</c>). Defaults to <see langword="true"/>.
    /// </summary>
    public bool EnableNamespaceIsolation { get; init; } = true;

    /// <summary>
    /// Optional UTF-8 text fed to standard input of the sandboxed process.
    /// </summary>
    public string? StandardInput { get; init; }
}

/// <summary>
/// Structured outcome returned from an <see cref="ISandboxExecutor"/> execution.
/// </summary>
public sealed record SandboxExecutionResult
{
    public int ExitCode { get; init; }
    public string StandardOutput { get; init; } = string.Empty;
    public string StandardError { get; init; } = string.Empty;
    public TimeSpan StartupLatency { get; init; }
    public TimeSpan TotalDuration { get; init; }
    public bool TimedOut { get; init; }
    public string IsolationMechanism { get; init; } = string.Empty;
    public bool Succeeded => !TimedOut && ExitCode == 0;
}

/// <summary>
/// Executes commands inside a lightweight OS-isolated sandbox.
/// </summary>
public interface ISandboxExecutor
{
    ValueTask<SandboxExecutionResult> ExecuteAsync(SandboxOptions options, CancellationToken ct = default);
}
