namespace Glacier.Agent.Sandbox;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Cross-platform, Native-AOT compatible OS process sandbox using Win32 Job Objects + AppContainer/LowIL
/// (or Restricted Tokens when network is allowed) on Windows, and <c>clone3</c>/<c>unshare</c> namespaces
/// + <c>pivot_root</c> + <c>seccomp-bpf</c> + <c>prctl</c>/<c>cgroups</c>/<c>setrlimit</c> on Linux.
/// </summary>
public sealed class OsProcessSandbox : ISandboxExecutor
{
    public ValueTask<SandboxExecutionResult> ExecuteAsync(SandboxOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ExecutablePath);

        if (options.Timeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.Timeout, "Timeout must be non-negative.");
        }

        if (options.MemoryLimitBytes is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.MemoryLimitBytes, "MemoryLimitBytes must be positive when specified.");
        }

        if (options.MaxProcesses is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.MaxProcesses, "MaxProcesses must be positive when specified.");
        }

        if (!string.IsNullOrEmpty(options.WorkingDirectory) && !Directory.Exists(options.WorkingDirectory))
        {
            throw new DirectoryNotFoundException($"Working directory not found: '{options.WorkingDirectory}'.");
        }

        ct.ThrowIfCancellationRequested();

        if (OperatingSystem.IsWindows())
        {
            return WindowsSandboxBackend.ExecuteAsync(options, ct);
        }

        if (OperatingSystem.IsLinux())
        {
            return LinuxSandboxBackend.ExecuteAsync(options, ct);
        }

        if (!options.EnableNamespaceIsolation)
        {
            return ExecuteFallbackProcessAsync(options, ct);
        }

        throw new PlatformNotSupportedException($"OS-isolated sandbox is not supported on {RuntimeInformation.OSDescription}.");
    }

    private const int MaxCapturedOutputChars = 4 * 1024 * 1024;

    private static readonly string[] s_fallbackEnvAllowlist =
    [
        "PATH",
        "LANG",
        "LC_ALL",
        "LC_CTYPE",
        "TERM",
        "TMPDIR",
        "TMP",
        "TEMP",
        "TZ",
        "HOME",
        "USERPROFILE",
        "LOCALAPPDATA",
        "APPDATA",
        "SYSTEMROOT",
        "WINDIR",
        "COMSPEC",
        "DOTNET_ROOT",
        "LD_LIBRARY_PATH"
    ];

    private static async ValueTask<SandboxExecutionResult> ExecuteFallbackProcessAsync(SandboxOptions options, CancellationToken ct)
    {
        var totalSw = Stopwatch.StartNew();
        var psi = new ProcessStartInfo
        {
            FileName = options.ExecutablePath,
            WorkingDirectory = string.IsNullOrEmpty(options.WorkingDirectory) ? Environment.CurrentDirectory : options.WorkingDirectory,
            RedirectStandardInput = options.StandardInput != null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (string arg in options.Arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        psi.Environment.Clear();
        foreach (string key in s_fallbackEnvAllowlist)
        {
            string? val = Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrEmpty(val))
            {
                psi.Environment[key] = val;
            }
        }

        if (options.EnvironmentVariables != null)
        {
            foreach (KeyValuePair<string, string> kvp in options.EnvironmentVariables)
            {
                if (!string.IsNullOrEmpty(kvp.Key))
                {
                    psi.Environment[kvp.Key] = kvp.Value ?? string.Empty;
                }
            }
        }

        using var process = new Process { StartInfo = psi };
        var startupSw = Stopwatch.StartNew();
        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start process '{options.ExecutablePath}'.");
        }

        TimeSpan startupLatency = startupSw.Elapsed;

        Task stdinTask = options.StandardInput != null
            ? WriteStandardInputAsync(process, options.StandardInput, ct)
            : Task.CompletedTask;
        Task<string> stdoutTask = ReadLimitedAsync(process.StandardOutput);
        Task<string> stderrTask = ReadLimitedAsync(process.StandardError);

        using var timeoutCts = options.Timeout > TimeSpan.Zero
            ? CancellationTokenSource.CreateLinkedTokenSource(ct)
            : null;
        if (timeoutCts != null)
        {
            timeoutCts.CancelAfter(options.Timeout);
        }

        CancellationToken effectiveCt = timeoutCts?.Token ?? ct;
        bool timedOut = false;

        try
        {
            await process.WaitForExitAsync(effectiveCt).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKillProcessTree(process);
            timedOut = !ct.IsCancellationRequested;
            if (!timedOut)
            {
                await Task.WhenAll(stdinTask, stdoutTask, stderrTask).ConfigureAwait(false);
                throw new OperationCanceledException(ct);
            }
        }

        await Task.WhenAll(stdinTask, stdoutTask, stderrTask).ConfigureAwait(false);

        return new SandboxExecutionResult
        {
            ExitCode = timedOut ? -1 : process.ExitCode,
            StandardOutput = stdoutTask.Result,
            StandardError = stderrTask.Result,
            StartupLatency = startupLatency,
            TotalDuration = totalSw.Elapsed,
            TimedOut = timedOut,
            IsolationMechanism = "ProcessFallback(no-namespace)"
        };
    }

    private static async Task<string> ReadLimitedAsync(StreamReader reader)
    {
        var sb = new StringBuilder();
        char[] buf = new char[4096];
        bool truncated = false;
        int read;
        while ((read = await reader.ReadAsync(buf.AsMemory()).ConfigureAwait(false)) > 0)
        {
            if (!truncated)
            {
                int remaining = MaxCapturedOutputChars - sb.Length;
                if (remaining <= 0)
                {
                    sb.Append("\n[OUTPUT TRUNCATED]");
                    truncated = true;
                }
                else if (read <= remaining)
                {
                    sb.Append(buf, 0, read);
                }
                else
                {
                    sb.Append(buf, 0, remaining);
                    sb.Append("\n[OUTPUT TRUNCATED]");
                    truncated = true;
                }
            }
        }
        return sb.ToString();
    }

    private static async Task WriteStandardInputAsync(Process process, string input, CancellationToken ct)
    {
        try
        {
            await process.StandardInput.WriteAsync(input.AsMemory(), ct).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (IOException)
        {
        }
        finally
        {
            try
            {
                process.StandardInput.Close();
            }
            catch (IOException)
            {
            }
        }
    }

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
        catch (NotSupportedException)
        {
        }
    }
}
