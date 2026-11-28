namespace Glacier.Agent.Tests;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
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
            Assert.Equal("WindowsJobObject+AppContainer+LowIL", result.IsolationMechanism);
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
                Arguments = new[] { "/c", "powershell.exe -NoProfile -Command Start-Sleep -Seconds 30" },
                WorkingDirectory = Environment.SystemDirectory,
                Timeout = TimeSpan.FromMilliseconds(400)
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
                ExecutablePath = "powershell.exe",
                Arguments = new[] { "-NoProfile", "-Command", "Start-Sleep -Seconds 30" },
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

    [Fact]
    public void LinuxSandboxPolicy_NamespaceIsolationMode_DefaultsToFailClosedUnlessOptInSet()
    {
        string? prior = Environment.GetEnvironmentVariable("GLACIER_SANDBOX_ALLOW_UNISOLATED_FALLBACK");
        try
        {
            Environment.SetEnvironmentVariable("GLACIER_SANDBOX_ALLOW_UNISOLATED_FALLBACK", null);
            Assert.Equal(0, LinuxSandboxPolicy.ResolveNativeNamespaceIsolationMode(false));
            Assert.Equal(1, LinuxSandboxPolicy.ResolveNativeNamespaceIsolationMode(true));

            Environment.SetEnvironmentVariable("GLACIER_SANDBOX_ALLOW_UNISOLATED_FALLBACK", "1");
            Assert.Equal(2, LinuxSandboxPolicy.ResolveNativeNamespaceIsolationMode(true));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GLACIER_SANDBOX_ALLOW_UNISOLATED_FALLBACK", prior);
        }
    }

    [Fact]
    public async Task ExecuteAsync_StripsHostSecretsFromEnvironment()
    {
        const string sentinel = "glacier-test-secret-gemini-key-99182";
        string? prior = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
        Environment.SetEnvironmentVariable("GEMINI_API_KEY", sentinel);
        try
        {
            SandboxOptions options = OperatingSystem.IsWindows()
                ? new SandboxOptions("cmd.exe", "/c", "set")
                : new SandboxOptions("/usr/bin/env");

            SandboxExecutionResult result = await _sandbox.ExecuteAsync(options);

            Assert.True(result.Succeeded, result.StandardError);
            Assert.DoesNotContain(sentinel, result.StandardOutput, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEMINI_API_KEY", prior);
        }
    }

    [Fact]
    public async Task ExecuteAsync_AllowNetworkFalse_BlocksLoopbackTcpOnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        SandboxExecutionResult netRes = await _sandbox.ExecuteAsync(new SandboxOptions(
            "powershell.exe",
            "-NoProfile",
            "-Command",
            $"try {{ $c = New-Object System.Net.Sockets.TcpClient('127.0.0.1', {port}); $c.Close(); exit 0 }} catch {{ exit 42 }}")
        {
            AllowNetwork = false,
            Timeout = TimeSpan.FromSeconds(5)
        });

        Assert.Equal("WindowsJobObject+AppContainer+LowIL", netRes.IsolationMechanism);
        Assert.NotEqual(0, netRes.ExitCode);
    }

    [Fact]
    public async Task ExecuteAsync_AllowNetworkTrue_UsesRestrictedTokenOnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        SandboxExecutionResult result = await _sandbox.ExecuteAsync(new SandboxOptions("cmd.exe", "/c", "echo net_allowed")
        {
            AllowNetwork = true,
            Timeout = TimeSpan.FromSeconds(10)
        });

        Assert.True(result.Succeeded, result.StandardError);
        Assert.Equal("WindowsJobObject+RestrictedToken", result.IsolationMechanism);
        Assert.Contains("net_allowed", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_AppContainer_AllowsScopedWorkingAndReadableWritablePaths_WhileBlockingNonAllowlistedHostPaths()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string root = Path.Combine(Path.GetTempPath(), $"glacier_ac_paths_{Guid.NewGuid():N}");
        string workDir = Path.Combine(root, "work");
        string readDir = Path.Combine(root, "ro");
        string writeDir = Path.Combine(root, "rw");
        string blockedDir = Path.Combine(root, "secret_host");
        Directory.CreateDirectory(workDir);
        Directory.CreateDirectory(readDir);
        Directory.CreateDirectory(writeDir);
        Directory.CreateDirectory(blockedDir);

        string readFile = Path.Combine(readDir, "input.txt");
        string blockedFile = Path.Combine(blockedDir, "secret.txt");
        await File.WriteAllTextAsync(readFile, "allowed_ro_content");
        await File.WriteAllTextAsync(blockedFile, "host_secret_must_stay_blocked");

        try
        {
            await File.WriteAllTextAsync(Path.Combine(workDir, "work.txt"), "work_rw_ok");

            SandboxExecutionResult cmdRes = await _sandbox.ExecuteAsync(new SandboxOptions(
                "cmd.exe",
                "/c",
                "type work.txt && echo child_wrote> work_child.txt")
            {
                WorkingDirectory = workDir,
                AllowNetwork = false,
                Timeout = TimeSpan.FromSeconds(10)
            });
            Assert.True(cmdRes.Succeeded, $"cmd Exit={cmdRes.ExitCode} Out={cmdRes.StandardOutput} Err={cmdRes.StandardError}");
            Assert.Contains("work_rw_ok", cmdRes.StandardOutput, StringComparison.Ordinal);

            string script =
                "$cwd = [Environment]::CurrentDirectory; " +
                "$wd = [System.IO.File]::ReadAllText((Join-Path $cwd 'work.txt')).Trim(); " +
                "$ro = (Get-Content -Raw '" + readFile + "' -ErrorAction Stop).Trim(); " +
                "Set-Content -Path '" + Path.Combine(writeDir, "out.txt") + "' -Value ($wd + '|' + $ro) -ErrorAction Stop; " +
                "$roWriteBlocked = $false; try { Set-Content -Path '" + Path.Combine(readDir, "denied.txt") + "' -Value 'x' -ErrorAction Stop } catch { $roWriteBlocked = $true }; " +
                "$hostBlocked = $false; try { Get-Content -Raw '" + blockedFile + "' -ErrorAction Stop | Out-Null } catch { $hostBlocked = $true }; " +
                "Write-Output (\"CWD=\" + $cwd + \";ROWRITE=\" + $roWriteBlocked + \";HOST=\" + $hostBlocked)";

            SandboxExecutionResult res = await _sandbox.ExecuteAsync(new SandboxOptions(
                "powershell.exe",
                "-NoProfile",
                "-Command",
                script)
            {
                WorkingDirectory = workDir,
                ReadablePaths = new[] { readDir },
                WritablePaths = new[] { writeDir },
                AllowNetwork = false,
                Timeout = TimeSpan.FromSeconds(10)
            });

            Assert.True(res.Succeeded, $"Exit={res.ExitCode} Out={res.StandardOutput} Err={res.StandardError}");
            Assert.Equal("WindowsJobObject+AppContainer+LowIL", res.IsolationMechanism);
            Assert.Contains("CWD=" + workDir, res.StandardOutput, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("ROWRITE=True;HOST=True", res.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("work_rw_ok|allowed_ro_content", await File.ReadAllTextAsync(Path.Combine(writeDir, "out.txt")), StringComparison.Ordinal);
            Assert.Contains("child_wrote", await File.ReadAllTextAsync(Path.Combine(workDir, "work_child.txt")), StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(readDir, "denied.txt")));

            // Verify temporary AppContainer ACEs were cleaned up in finally (no stale S-1-15-2- explicit ACEs left)
            var acl = new DirectoryInfo(workDir).GetAccessControl();
            foreach (System.Security.AccessControl.AuthorizationRule rule in acl.GetAccessRules(true, false, typeof(System.Security.Principal.SecurityIdentifier)))
            {
                Assert.DoesNotContain("S-1-15-2-", rule.IdentityReference.Value, StringComparison.Ordinal);
            }
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ExecuteAsync_LargeStdoutExceeding4MiB_IsTruncatedCleanly()
    {
        SandboxOptions options = OperatingSystem.IsWindows()
            ? new SandboxOptions(
                "powershell.exe",
                "-NoProfile",
                "-Command",
                "[Console]::Out.Write((New-Object string 'A', 4500000))")
            {
                Timeout = TimeSpan.FromSeconds(20)
            }
            : new SandboxOptions(
                "/bin/sh",
                "-c",
                "dd if=/dev/zero bs=4096 count=1100 2>/dev/null | tr '\\0' 'A'")
            {
                Timeout = TimeSpan.FromSeconds(20)
            };

        SandboxExecutionResult result = await _sandbox.ExecuteAsync(options);

        Assert.True(result.Succeeded, $"Exit={result.ExitCode} Err={result.StandardError}");
        const string marker = "\n[OUTPUT TRUNCATED]";
        Assert.EndsWith(marker, result.StandardOutput, StringComparison.Ordinal);
        Assert.Equal((4 * 1024 * 1024) + marker.Length, result.StandardOutput.Length);
    }

    [Fact]
    public async Task ExecuteAsync_ConcurrentOverlappingReadAndWriteScopes_PreservesWriteAccess()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string sharedDir = Path.Combine(Path.GetTempPath(), $"glacier_acl_overlap_{Guid.NewGuid():N}");
        Directory.CreateDirectory(sharedDir);
        string seedFile = Path.Combine(sharedDir, "seed.txt");
        string outFile = Path.Combine(sharedDir, "written.txt");
        await File.WriteAllTextAsync(seedFile, "seed_ok");

        try
        {
            Task<SandboxExecutionResult> writeTask = _sandbox.ExecuteAsync(new SandboxOptions(
                "powershell.exe",
                "-NoProfile",
                "-Command",
                "Start-Sleep -Milliseconds 500; [System.IO.File]::WriteAllText((Join-Path ([Environment]::CurrentDirectory) 'written.txt'), 'overlap_write_ok')")
            {
                WorkingDirectory = sharedDir,
                AllowNetwork = false,
                Timeout = TimeSpan.FromSeconds(15)
            }).AsTask();

            Task<SandboxExecutionResult> readTask = _sandbox.ExecuteAsync(new SandboxOptions(
                "cmd.exe",
                "/c",
                "type",
                seedFile)
            {
                ReadablePaths = new[] { sharedDir },
                AllowNetwork = false,
                Timeout = TimeSpan.FromSeconds(15)
            }).AsTask();

            SandboxExecutionResult[] results = await Task.WhenAll(writeTask, readTask);

            Assert.True(results[1].Succeeded, $"ReadTask failed: Exit={results[1].ExitCode} Err={results[1].StandardError}");
            Assert.Contains("seed_ok", results[1].StandardOutput, StringComparison.Ordinal);
            Assert.True(results[0].Succeeded, $"WriteTask failed: Exit={results[0].ExitCode} Err={results[0].StandardError}");
            Assert.True(File.Exists(outFile));
            Assert.Contains("overlap_write_ok", await File.ReadAllTextAsync(outFile), StringComparison.Ordinal);

            var acl = new DirectoryInfo(sharedDir).GetAccessControl();
            foreach (System.Security.AccessControl.AuthorizationRule rule in acl.GetAccessRules(true, false, typeof(System.Security.Principal.SecurityIdentifier)))
            {
                Assert.DoesNotContain("S-1-15-2-", rule.IdentityReference.Value, StringComparison.Ordinal);
            }
        }
        finally
        {
            try { Directory.Delete(sharedDir, recursive: true); } catch { }
        }
    }
}
