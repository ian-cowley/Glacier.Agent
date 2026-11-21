namespace Glacier.Agent.Tests;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Glacier.Agent.Sandbox;
using Xunit;

[Collection("ProcessAndTimingSensitive")]
public sealed class OsProcessSandboxTests
{
    private readonly OsProcessSandbox _sandbox = new();

    [Fact]
    public async Task ExecuteAsync_InvalidOptions_ThrowsExpectedExceptions()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => _sandbox.ExecuteAsync(null!).AsTask());

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _sandbox.ExecuteAsync(new SandboxOptions { ExecutablePath = "   " }).AsTask());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            _sandbox.ExecuteAsync(new SandboxOptions
            {
                ExecutablePath = "cmd.exe",
                Timeout = TimeSpan.FromMilliseconds(-1)
            }).AsTask());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            _sandbox.ExecuteAsync(new SandboxOptions
            {
                ExecutablePath = "cmd.exe",
                MemoryLimitBytes = 0
            }).AsTask());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            _sandbox.ExecuteAsync(new SandboxOptions
            {
                ExecutablePath = "cmd.exe",
                MaxProcesses = 0
            }).AsTask());

        string missingDir = Path.Combine(Path.GetTempPath(), $"glacier_missing_{Guid.NewGuid():N}");
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() =>
            _sandbox.ExecuteAsync(new SandboxOptions
            {
                ExecutablePath = "cmd.exe",
                WorkingDirectory = missingDir
            }).AsTask());
    }

    [Fact]
    public async Task ExecuteAsync_NormalCommand_CapturesStdoutStderrAndMechanism()
    {
        SandboxOptions options = OperatingSystem.IsWindows()
            ? new SandboxOptions("cmd.exe", "/c", "echo hello_stdout & echo hello_stderr 1>&2")
            : new SandboxOptions("/bin/sh", "-c", "echo hello_stdout; echo hello_stderr >&2");

        SandboxExecutionResult result = await _sandbox.ExecuteAsync(options);

        Assert.True(result.Succeeded);
        Assert.False(result.TimedOut);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("hello_stdout", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("hello_stderr", result.StandardError, StringComparison.Ordinal);
        Assert.True(result.TotalDuration > TimeSpan.Zero);

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal("WindowsJobObject+RestrictedToken", result.IsolationMechanism);
        }
        else if (OperatingSystem.IsLinux())
        {
            Assert.StartsWith("Linux:", result.IsolationMechanism, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ExecuteAsync_NonZeroExitCode_ReportsFailure()
    {
        SandboxOptions options = OperatingSystem.IsWindows()
            ? new SandboxOptions("cmd.exe", "/c", "exit /b 42")
            : new SandboxOptions("/bin/sh", "-c", "exit 42");

        SandboxExecutionResult result = await _sandbox.ExecuteAsync(options);

        Assert.False(result.Succeeded);
        Assert.False(result.TimedOut);
        Assert.Equal(42, result.ExitCode);
    }

    [Fact]
    public async Task ExecuteAsync_StandardInput_IsDeliveredToChild()
    {
        SandboxOptions options = OperatingSystem.IsWindows()
            ? new SandboxOptions
            {
                ExecutablePath = "findstr.exe",
                Arguments = new[] { "^" },
                StandardInput = "glacier_stdin_line\n"
            }
            : new SandboxOptions
            {
                ExecutablePath = "/bin/cat",
                StandardInput = "glacier_stdin_line\n"
            };

        SandboxExecutionResult result = await _sandbox.ExecuteAsync(options);

        Assert.True(result.Succeeded);
        Assert.Contains("glacier_stdin_line", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_WorkingDirectoryAndEnvironment_AreApplied()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"glacier_sbx_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var env = new Dictionary<string, string>
            {
                ["GLACIER_SANDBOX_ENV"] = "verified_value_987"
            };

            SandboxOptions options = OperatingSystem.IsWindows()
                ? new SandboxOptions
                {
                    ExecutablePath = "cmd.exe",
                    Arguments = new[] { "/c", "cd & echo %GLACIER_SANDBOX_ENV%" },
                    WorkingDirectory = tempDir,
                    EnvironmentVariables = env
                }
                : new SandboxOptions
                {
                    ExecutablePath = "/bin/sh",
                    Arguments = new[] { "-c", "pwd && echo \"$GLACIER_SANDBOX_ENV\"" },
                    WorkingDirectory = tempDir,
                    EnvironmentVariables = env
                };

            SandboxExecutionResult result = await _sandbox.ExecuteAsync(options);

            Assert.True(result.Succeeded);
            Assert.Contains(
                Path.GetFileName(tempDir),
                result.StandardOutput,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
            Assert.Contains("verified_value_987", result.StandardOutput, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ExecuteAsync_WarmStartupLatency_IsSub25Milliseconds()
    {
        SandboxOptions options = OperatingSystem.IsWindows()
            ? new SandboxOptions("cmd.exe", "/c", "echo ok")
            : new SandboxOptions("/bin/echo", "ok");

        // Warm-up runs to avoid one-time JIT / OS loader overhead.
        for (int i = 0; i < 2; i++)
        {
            SandboxExecutionResult warm = await _sandbox.ExecuteAsync(options);
            Assert.True(warm.Succeeded);
        }

        var latenciesMs = new List<double>(10);
        for (int i = 0; i < 10; i++)
        {
            SandboxExecutionResult run = await _sandbox.ExecuteAsync(options);
            Assert.True(run.Succeeded);
            latenciesMs.Add(run.StartupLatency.TotalMilliseconds);
        }

        latenciesMs.Sort();
        double medianMs = (latenciesMs[4] + latenciesMs[5]) / 2.0;
        Assert.True(
            medianMs < 25.0,
            $"Expected warm median startup latency < 25 ms, observed {medianMs:F2} ms (samples: {string.Join(", ", latenciesMs.Select(x => x.ToString("F2")))}).");
    }

    [Fact]
    public async Task ExecuteAsync_Timeout_TerminatesProcessTree()
    {
        SandboxOptions options = OperatingSystem.IsWindows()
            ? new SandboxOptions
            {
                ExecutablePath = "cmd.exe",
                Arguments = new[] { "/c", "ping -n 30 127.0.0.1 > nul" },
                Timeout = TimeSpan.FromMilliseconds(200)
            }
            : new SandboxOptions
            {
                ExecutablePath = "/bin/sh",
                Arguments = new[] { "-c", "sleep 30 & sleep 30" },
                Timeout = TimeSpan.FromMilliseconds(200)
            };

        var sw = Stopwatch.StartNew();
        SandboxExecutionResult result = await _sandbox.ExecuteAsync(options);
        sw.Stop();

        Assert.True(result.TimedOut);
        Assert.False(result.Succeeded);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"Timeout termination took too long: {sw.Elapsed}");
    }

    [Fact]
    public async Task ExecuteAsync_CallerCancellation_CancelsExecution()
    {
        SandboxOptions options = OperatingSystem.IsWindows()
            ? new SandboxOptions
            {
                ExecutablePath = "cmd.exe",
                Arguments = new[] { "/c", "ping -n 30 127.0.0.1 > nul" },
                Timeout = TimeSpan.FromSeconds(30)
            }
            : new SandboxOptions
            {
                ExecutablePath = "/bin/sh",
                Arguments = new[] { "-c", "sleep 30" },
                Timeout = TimeSpan.FromSeconds(30)
            };

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _sandbox.ExecuteAsync(options, cts.Token).AsTask());
    }

    [Fact]
    public async Task ExecuteAsync_MemoryAndProcessLimits_AreEnforced()
    {
        if (OperatingSystem.IsWindows())
        {
            // MaxProcesses = 1 allows cmd.exe itself, but blocks spawning a child cmd.exe process.
            SandboxExecutionResult procLimitResult = await _sandbox.ExecuteAsync(new SandboxOptions
            {
                ExecutablePath = "cmd.exe",
                Arguments = new[] { "/c", "cmd.exe /c echo child_spawned" },
                MaxProcesses = 1,
                Timeout = TimeSpan.FromSeconds(10)
            });

            Assert.DoesNotContain("child_spawned", procLimitResult.StandardOutput, StringComparison.Ordinal);

            // Sufficient memory limit permits dotnet --version; tiny commit limit fails allocation.
            SandboxExecutionResult dotnetOk = await _sandbox.ExecuteAsync(new SandboxOptions
            {
                ExecutablePath = "dotnet",
                Arguments = new[] { "--version" },
                MemoryLimitBytes = 512L * 1024 * 1024,
                Timeout = TimeSpan.FromSeconds(15)
            });
            Assert.True(dotnetOk.Succeeded);

            SandboxExecutionResult dotnetTooSmall = await _sandbox.ExecuteAsync(new SandboxOptions
            {
                ExecutablePath = "dotnet",
                Arguments = new[] { "--version" },
                MemoryLimitBytes = 4L * 1024 * 1024,
                Timeout = TimeSpan.FromSeconds(15)
            });
            Assert.False(dotnetTooSmall.Succeeded);
        }
        else if (OperatingSystem.IsLinux())
        {
            // 256 MB RLIMIT_DATA permits dotnet --version.
            SandboxExecutionResult dotnetOk = await _sandbox.ExecuteAsync(new SandboxOptions
            {
                ExecutablePath = "dotnet",
                Arguments = new[] { "--version" },
                MemoryLimitBytes = 256L * 1024 * 1024,
                Timeout = TimeSpan.FromSeconds(15)
            });
            Assert.True(dotnetOk.Succeeded, $"dotnet --version failed under 256MB RLIMIT_DATA: {dotnetOk.StandardError}");

            if (File.Exists("/usr/bin/python3"))
            {
                SandboxExecutionResult pyOom = await _sandbox.ExecuteAsync(new SandboxOptions
                {
                    ExecutablePath = "/usr/bin/python3",
                    Arguments = new[] { "-c", "bytearray(96 * 1024 * 1024)" },
                    MemoryLimitBytes = 32L * 1024 * 1024,
                    Timeout = TimeSpan.FromSeconds(10)
                });
                Assert.False(pyOom.Succeeded);
            }
        }
    }

    [Fact]
    public async Task ExecuteAsync_Linux_NamespacesAndFallback_BehaveAsExpected()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        SandboxExecutionResult isolated = await _sandbox.ExecuteAsync(new SandboxOptions
        {
            ExecutablePath = "/bin/sh",
            Arguments = new[] { "-c", "echo PID=$$; echo UID=$(id -u); cat /proc/net/dev" },
            EnableNamespaceIsolation = true,
            AllowNetwork = false
        });

        Assert.True(isolated.Succeeded, isolated.StandardError);
        if (isolated.IsolationMechanism.Contains("clone3(user,pid,mnt,ipc,uts,net)", StringComparison.Ordinal))
        {
            Assert.Contains("PID=1", isolated.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("UID=0", isolated.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("lo:", isolated.StandardOutput, StringComparison.Ordinal);
            Assert.DoesNotContain("eth0:", isolated.StandardOutput, StringComparison.Ordinal);
        }

        SandboxExecutionResult allowNet = await _sandbox.ExecuteAsync(new SandboxOptions
        {
            ExecutablePath = "/bin/sh",
            Arguments = new[] { "-c", "echo ok" },
            EnableNamespaceIsolation = true,
            AllowNetwork = true
        });
        Assert.True(allowNet.Succeeded);
        Assert.DoesNotContain(",net)", allowNet.IsolationMechanism, StringComparison.Ordinal);

        SandboxExecutionResult noNs = await _sandbox.ExecuteAsync(new SandboxOptions
        {
            ExecutablePath = "/bin/sh",
            Arguments = new[] { "-c", "echo PID=$$" },
            EnableNamespaceIsolation = false
        });
        Assert.True(noNs.Succeeded);
        Assert.Contains("no-namespaces", noNs.IsolationMechanism, StringComparison.Ordinal);
        Assert.DoesNotContain("PID=1\n", noNs.StandardOutput, StringComparison.Ordinal);
    }
}
