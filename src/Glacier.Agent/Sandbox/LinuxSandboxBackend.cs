namespace Glacier.Agent.Sandbox;

using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

[SupportedOSPlatform("linux")]
internal static unsafe class LinuxSandboxBackend
{
    private static readonly delegate* unmanaged[Cdecl]<
        LinuxNativeMethods.GlacierSandboxSpawnArgs*,
        LinuxNativeMethods.GlacierSandboxSpawnResult*,
        int> s_nativeSpawn;

    static LinuxSandboxBackend()
    {
        s_nativeSpawn = LoadNativeSpawn();

        // Only prepare managed fallback frames when libglacier_sandbox.so is unavailable (e.g. non-x64).
        if (s_nativeSpawn == null)
        {
            if (RuntimeFeature.IsDynamicCodeSupported)
            {
                RuntimeHelpers.PrepareMethod(((Action<int>)WriteChildErrnoAndExit).Method.MethodHandle);
                RuntimeHelpers.PrepareMethod(((Action<int>)LinuxNativeMethods.ChildExit).Method.MethodHandle);
            }

            // Pre-JIT ChildEntry and pre-resolve all blittable libc stubs before any clone3/fork.
            _ = ChildEntry(
                dryRun: true,
                useUnshareFallback: false,
                requestedUnshareFlags: 0,
                unshareReportWriteFd: -1,
                syncReadFd: -1,
                errWriteFd: -1,
                inReadFd: -1,
                outWriteFd: -1,
                errOutWriteFd: -1,
                hasMountNs: false,
                slashPtr: null,
                procFsPtr: null,
                procMountPtr: null,
                exePtr: null,
                argvPtr: null,
                envpPtr: null,
                workDirPtr: null,
                hasMemoryLimit: false,
                memoryLimitBytes: 0,
                hasMaxProcesses: false,
                maxProcesses: 0);
        }
    }

    private static delegate* unmanaged[Cdecl]<
        LinuxNativeMethods.GlacierSandboxSpawnArgs*,
        LinuxNativeMethods.GlacierSandboxSpawnResult*,
        int> LoadNativeSpawn()
    {
        if (NativeLibrary.TryLoad(
                "glacier_sandbox",
                typeof(LinuxSandboxBackend).Assembly,
                DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.ApplicationDirectory | DllImportSearchPath.SafeDirectories,
                out nint handle))
        {
            if (NativeLibrary.TryGetExport(handle, "glacier_sandbox_spawn", out nint exportPtr))
            {
                return (delegate* unmanaged[Cdecl]<LinuxNativeMethods.GlacierSandboxSpawnArgs*, LinuxNativeMethods.GlacierSandboxSpawnResult*, int>)exportPtr;
            }
        }

        string? rid = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "linux-x64",
            Architecture.Arm64 => "linux-arm64",
            _ => null
        };

        string[] probeDirs = new[]
        {
            AppContext.BaseDirectory,
            Path.GetDirectoryName(Environment.ProcessPath) ?? string.Empty
        };

        for (int i = 0; i < probeDirs.Length; i++)
        {
            string dir = probeDirs[i];
            if (string.IsNullOrEmpty(dir))
            {
                continue;
            }

            if (rid != null)
            {
                string ridPath = Path.Combine(dir, "runtimes", rid, "native", "libglacier_sandbox.so");
                if (File.Exists(ridPath) &&
                    NativeLibrary.TryLoad(ridPath, out nint ridHandle) &&
                    NativeLibrary.TryGetExport(ridHandle, "glacier_sandbox_spawn", out nint ridExport))
                {
                    return (delegate* unmanaged[Cdecl]<LinuxNativeMethods.GlacierSandboxSpawnArgs*, LinuxNativeMethods.GlacierSandboxSpawnResult*, int>)ridExport;
                }
            }

            string flatPath = Path.Combine(dir, "libglacier_sandbox.so");
            if (File.Exists(flatPath) &&
                NativeLibrary.TryLoad(flatPath, out nint flatHandle) &&
                NativeLibrary.TryGetExport(flatHandle, "glacier_sandbox_spawn", out nint flatExport))
            {
                return (delegate* unmanaged[Cdecl]<LinuxNativeMethods.GlacierSandboxSpawnArgs*, LinuxNativeMethods.GlacierSandboxSpawnResult*, int>)flatExport;
            }
        }

        return null;
    }

    public static ValueTask<SandboxExecutionResult> ExecuteAsync(SandboxOptions options, CancellationToken ct)
    {
        return new ValueTask<SandboxExecutionResult>(Task.Run(() => ExecuteCore(options, ct), CancellationToken.None));
    }

    private static SandboxExecutionResult ExecuteCore(SandboxOptions options, CancellationToken ct)
    {
        var totalSw = Stopwatch.StartNew();
        var startupSw = Stopwatch.StartNew();

        string resolvedExe = ResolveExecutablePath(options.ExecutablePath, options.EnvironmentVariables);

        using var pool = new UnmanagedPool();
        byte* exePtr = pool.AllocUtf8(resolvedExe);
        byte** argvPtr = BuildArgv(pool, resolvedExe, options.Arguments);
        byte** envpPtr = BuildEnvp(pool, options.EnvironmentVariables);
        byte* workDirPtr = string.IsNullOrEmpty(options.WorkingDirectory) ? null : pool.AllocUtf8(options.WorkingDirectory);
        byte* slashPtr = pool.AllocUtf8("/");
        byte* procFsPtr = pool.AllocUtf8("proc");
        byte* procMountPtr = pool.AllocUtf8("/proc");
        byte* devNullPathPtr = pool.AllocUtf8("/dev/null");

        int syncReadFd = -1, syncWriteFd = -1;
        int errReadFd = -1, errWriteFd = -1;
        int outReadFd = -1, outWriteFd = -1;
        int errOutReadFd = -1, errOutWriteFd = -1;
        int inReadFd = -1, inWriteFd = -1;
        int unshareReadFd = -1, unshareWriteFd = -1;
        int cancelReadFd = -1, cancelWriteFd = -1;
        int devNullFd = -1;
        int pidfd = -1;
        int childPid = -1;
        bool childReaped = false;
        Task? stdinTask = null;
        CancellationTokenSource? timeoutCts = null;
        CancellationTokenRegistration ctReg = default;

        try
        {
            CreatePipePair(out syncReadFd, out syncWriteFd, LinuxNativeMethods.O_CLOEXEC);
            CreatePipePair(out errReadFd, out errWriteFd, LinuxNativeMethods.O_CLOEXEC);
            CreatePipePair(out outReadFd, out outWriteFd, LinuxNativeMethods.O_CLOEXEC);
            CreatePipePair(out errOutReadFd, out errOutWriteFd, LinuxNativeMethods.O_CLOEXEC);
            CreatePipePair(out cancelReadFd, out cancelWriteFd, LinuxNativeMethods.O_CLOEXEC | LinuxNativeMethods.O_NONBLOCK);

            if (options.StandardInput != null)
            {
                CreatePipePair(out inReadFd, out inWriteFd, LinuxNativeMethods.O_CLOEXEC);
            }
            else
            {
                devNullFd = LinuxNativeMethods.Open(devNullPathPtr, LinuxNativeMethods.O_RDONLY | LinuxNativeMethods.O_CLOEXEC, 0);
                if (devNullFd < 0)
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "open(/dev/null) failed.");
                }
            }

            timeoutCts = options.Timeout > TimeSpan.Zero
                ? CancellationTokenSource.CreateLinkedTokenSource(ct)
                : null;
            if (timeoutCts != null)
            {
                timeoutCts.CancelAfter(options.Timeout);
            }

            CancellationToken effectiveCt = timeoutCts?.Token ?? ct;
            int capturedCancelWriteFd = cancelWriteFd;
            ctReg = effectiveCt.Register(() =>
            {
                byte b = 1;
                _ = LinuxNativeMethods.Write(capturedCancelWriteFd, &b, 1);
            });

            if (effectiveCt.IsCancellationRequested)
            {
                byte b = 1;
                _ = LinuxNativeMethods.Write(capturedCancelWriteFd, &b, 1);
            }

            int childInFd = options.StandardInput != null ? inReadFd : devNullFd;

            bool userNsActive = false;
            bool mountNsActive = false;
            bool usedForkFallback = false;
            string isolationMechanism;

            if (s_nativeSpawn != null)
            {
                var spawnArgs = new LinuxNativeMethods.GlacierSandboxSpawnArgs
                {
                    ExePath = exePtr,
                    Argv = argvPtr,
                    Envp = envpPtr,
                    WorkDir = workDirPtr,
                    MemoryLimitBytes = options.MemoryLimitBytes.GetValueOrDefault(),
                    EnableNamespaceIsolation = options.EnableNamespaceIsolation ? 1 : 0,
                    AllowNetwork = options.AllowNetwork ? 1 : 0,
                    HasMemoryLimit = options.MemoryLimitBytes.HasValue ? 1 : 0,
                    HasMaxProcesses = options.MaxProcesses.HasValue ? 1 : 0,
                    MaxProcesses = options.MaxProcesses.GetValueOrDefault(),
                    SyncReadFd = syncReadFd,
                    SyncWriteFd = syncWriteFd,
                    ErrReadFd = errReadFd,
                    ErrWriteFd = errWriteFd,
                    InReadFd = childInFd,
                    InWriteFd = inWriteFd,
                    OutReadFd = outReadFd,
                    OutWriteFd = outWriteFd,
                    ErrOutReadFd = errOutReadFd,
                    ErrOutWriteFd = errOutWriteFd,
                    CancelReadFd = cancelReadFd,
                    CancelWriteFd = cancelWriteFd,
                    TimeoutMs = ComputeRemainingTimeoutMs(totalSw, options.Timeout)
                };

                LinuxNativeMethods.GlacierSandboxSpawnResult spawnResult;
                int spawnRc = s_nativeSpawn(&spawnArgs, &spawnResult);
                childPid = spawnResult.ChildPid;
                pidfd = spawnResult.Pidfd;
                userNsActive = spawnResult.UserNsActive != 0;
                isolationMechanism = FormatIsolationMechanism(spawnResult.Mode, options.AllowNetwork);

                if (spawnRc == -2)
                {
                    if (childPid > 0)
                    {
                        KillProcessGroup(childPid, pidfd);
                        ReapChild(childPid, out _);
                        childReaped = true;
                    }

                    if (ct.IsCancellationRequested)
                    {
                        throw new OperationCanceledException(ct);
                    }

                    return new SandboxExecutionResult
                    {
                        ExitCode = -1,
                        StandardOutput = string.Empty,
                        StandardError = string.Empty,
                        StartupLatency = startupSw.Elapsed,
                        TotalDuration = totalSw.Elapsed,
                        TimedOut = true,
                        IsolationMechanism = isolationMechanism
                    };
                }

                if (spawnRc < 0)
                {
                    if (childPid > 0)
                    {
                        KillProcessGroup(childPid, pidfd);
                        ReapChild(childPid, out _);
                        childReaped = true;
                    }

                    throw new Win32Exception(
                        spawnResult.ErrNo,
                        $"glacier_sandbox_spawn failed (mode {spawnResult.Mode}) with errno {spawnResult.ErrNo}.");
                }
            }
            else
            {
                childPid = SpawnManagedFallback(
                    options,
                    totalSw,
                    effectiveCt,
                    ct,
                    syncReadFd,
                    syncWriteFd,
                    errReadFd,
                    errWriteFd,
                    childInFd,
                    inWriteFd,
                    outReadFd,
                    outWriteFd,
                    errOutReadFd,
                    errOutWriteFd,
                    cancelReadFd,
                    cancelWriteFd,
                    ref unshareReadFd,
                    ref unshareWriteFd,
                    exePtr,
                    argvPtr,
                    envpPtr,
                    workDirPtr,
                    slashPtr,
                    procFsPtr,
                    procMountPtr,
                    ref pidfd,
                    out userNsActive,
                    out mountNsActive,
                    out usedForkFallback,
                    out isolationMechanism,
                    out SandboxExecutionResult? earlyTimeoutResult);

                if (earlyTimeoutResult != null)
                {
                    childReaped = true;
                    return earlyTimeoutResult;
                }

                if (!usedForkFallback && childPid == 0)
                {
                    ChildCloseIfValid(syncWriteFd);
                    ChildCloseIfValid(errReadFd);
                    ChildCloseIfValid(outReadFd);
                    ChildCloseIfValid(errOutReadFd);
                    ChildCloseIfValid(inWriteFd);
                    ChildCloseIfValid(cancelReadFd);
                    ChildCloseIfValid(cancelWriteFd);

                    _ = ChildEntry(
                        dryRun: false,
                        useUnshareFallback: false,
                        requestedUnshareFlags: 0,
                        unshareReportWriteFd: -1,
                        syncReadFd: syncReadFd,
                        errWriteFd: errWriteFd,
                        inReadFd: childInFd,
                        outWriteFd: outWriteFd,
                        errOutWriteFd: errOutWriteFd,
                        hasMountNs: mountNsActive,
                        slashPtr: slashPtr,
                        procFsPtr: procFsPtr,
                        procMountPtr: procMountPtr,
                        exePtr: exePtr,
                        argvPtr: argvPtr,
                        envpPtr: envpPtr,
                        workDirPtr: workDirPtr,
                        hasMemoryLimit: options.MemoryLimitBytes.HasValue,
                        memoryLimitBytes: options.MemoryLimitBytes.GetValueOrDefault(),
                        hasMaxProcesses: options.MaxProcesses.HasValue,
                        maxProcesses: options.MaxProcesses.GetValueOrDefault());

                    LinuxNativeMethods.ChildExit(127);
                }
            }

            // Parent process
            CloseAndReset(ref syncReadFd);
            CloseAndReset(ref errWriteFd);
            CloseAndReset(ref outWriteFd);
            CloseAndReset(ref errOutWriteFd);
            CloseAndReset(ref inReadFd);
            CloseAndReset(ref devNullFd);
            CloseAndReset(ref unshareWriteFd);

            if (pidfd < 0)
            {
                int openedPidfd = LinuxNativeMethods.SyscallPidfdOpen(LinuxNativeMethods.__NR_pidfd_open, childPid, 0);
                if (openedPidfd >= 0)
                {
                    pidfd = openedPidfd;
                }
            }

            if (userNsActive)
            {
                SetupUserNamespaceMaps(childPid);
            }

            byte goByte = 1;
            WriteExact(syncWriteFd, &goByte, 1);
            CloseAndReset(ref syncWriteFd);

            SetNonBlocking(errReadFd);
            bool startupReadCompleted = TryReadStartupErrnoBounded(
                errReadFd,
                cancelReadFd,
                totalSw,
                options.Timeout,
                effectiveCt,
                out int errBytes,
                out int childErrno);
            CloseAndReset(ref errReadFd);

            if (!startupReadCompleted)
            {
                KillProcessGroup(childPid, pidfd);
                ReapChild(childPid, out _);
                childReaped = true;

                if (ct.IsCancellationRequested)
                {
                    throw new OperationCanceledException(ct);
                }

                return new SandboxExecutionResult
                {
                    ExitCode = -1,
                    StandardOutput = string.Empty,
                    StandardError = string.Empty,
                    StartupLatency = startupSw.Elapsed,
                    TotalDuration = totalSw.Elapsed,
                    TimedOut = true,
                    IsolationMechanism = isolationMechanism
                };
            }

            if (errBytes > 0)
            {
                ReapChild(childPid, out _);
                childReaped = true;
                throw new Win32Exception(childErrno, $"Child process execve failed for '{options.ExecutablePath}' (errno {childErrno}).");
            }

            TimeSpan startupLatency = startupSw.Elapsed;

            SetNonBlocking(outReadFd);
            SetNonBlocking(errOutReadFd);

            if (options.StandardInput != null && inWriteFd >= 0)
            {
                int capturedInWriteFd = inWriteFd;
                inWriteFd = -1;
                byte[] inputBytes = Encoding.UTF8.GetBytes(options.StandardInput);
                stdinTask = Task.Run(() => WriteStdinAndClose(capturedInWriteFd, inputBytes));
            }

            using var stdoutStream = new MemoryStream();
            using var stderrStream = new MemoryStream();
            bool outOpen = true;
            bool errOpen = true;
            bool exited = false;
            bool timedOut = false;
            int waitStatus = 0;

            byte[] readScratch = new byte[8192];
            LinuxNativeMethods.PollFd* pollFds = stackalloc LinuxNativeMethods.PollFd[4];

            fixed (byte* pScratch = readScratch)
            {
                while (outOpen || errOpen || !exited)
                {
                    if (effectiveCt.IsCancellationRequested)
                    {
                        KillProcessGroup(childPid, pidfd);
                        timedOut = !ct.IsCancellationRequested;
                        break;
                    }

                    int nfds = 0;
                    int outIdx = -1, errIdx = -1, pidIdx = -1, cancelIdx = -1;

                    if (outOpen)
                    {
                        outIdx = nfds;
                        pollFds[nfds++] = new LinuxNativeMethods.PollFd { Fd = outReadFd, Events = LinuxNativeMethods.POLLIN };
                    }
                    if (errOpen)
                    {
                        errIdx = nfds;
                        pollFds[nfds++] = new LinuxNativeMethods.PollFd { Fd = errOutReadFd, Events = LinuxNativeMethods.POLLIN };
                    }
                    if (!exited && pidfd >= 0)
                    {
                        pidIdx = nfds;
                        pollFds[nfds++] = new LinuxNativeMethods.PollFd { Fd = pidfd, Events = LinuxNativeMethods.POLLIN };
                    }
                    cancelIdx = nfds;
                    pollFds[nfds++] = new LinuxNativeMethods.PollFd { Fd = cancelReadFd, Events = LinuxNativeMethods.POLLIN };

                    int timeoutMs = -1;
                    bool hasTimeout = options.Timeout > TimeSpan.Zero;
                    if (hasTimeout)
                    {
                        double remainingMs = (options.Timeout - totalSw.Elapsed).TotalMilliseconds;
                        timeoutMs = remainingMs <= 0 ? 0 : Math.Min(int.MaxValue, (int)Math.Ceiling(remainingMs));
                    }

                    if (!exited && pidfd < 0)
                    {
                        int wpPre = LinuxNativeMethods.WaitPid(childPid, &waitStatus, LinuxNativeMethods.WNOHANG);
                        if (wpPre > 0)
                        {
                            exited = true;
                            childReaped = true;
                            continue;
                        }

                        // Without pidfd, poll() cannot observe process exit directly once stdio closes.
                        // Bound poll timeout so we detect exit promptly via WNOHANG instead of stalling.
                        const int maxFallbackPollMs = 5;
                        timeoutMs = timeoutMs < 0 ? maxFallbackPollMs : Math.Min(timeoutMs, maxFallbackPollMs);
                    }

                    int pollRes = LinuxNativeMethods.Poll(pollFds, (nuint)nfds, timeoutMs);
                    if (pollRes < 0)
                    {
                        int pollErr = Marshal.GetLastPInvokeError();
                        if (pollErr == LinuxNativeMethods.EINTR)
                        {
                            continue;
                        }
                        throw new Win32Exception(pollErr, "poll() failed.");
                    }

                    bool cancelSignaled = (pollFds[cancelIdx].Revents & LinuxNativeMethods.POLLIN) != 0 || effectiveCt.IsCancellationRequested;
                    bool hardTimeoutReached = hasTimeout && totalSw.Elapsed >= options.Timeout;
                    if (cancelSignaled || (pollRes == 0 && (pidfd >= 0 || hardTimeoutReached)))
                    {
                        KillProcessGroup(childPid, pidfd);
                        timedOut = !ct.IsCancellationRequested;
                        break;
                    }

                    if (outIdx >= 0 && pollFds[outIdx].Revents != 0)
                    {
                        outOpen = DrainNonBlocking(outReadFd, stdoutStream, pScratch, readScratch.Length);
                    }

                    if (errIdx >= 0 && pollFds[errIdx].Revents != 0)
                    {
                        errOpen = DrainNonBlocking(errOutReadFd, stderrStream, pScratch, readScratch.Length);
                    }

                    if (pidIdx >= 0 && (pollFds[pidIdx].Revents & (LinuxNativeMethods.POLLIN | LinuxNativeMethods.POLLERR | LinuxNativeMethods.POLLHUP)) != 0)
                    {
                        int wp = LinuxNativeMethods.WaitPid(childPid, &waitStatus, LinuxNativeMethods.WNOHANG);
                        if (wp > 0)
                        {
                            exited = true;
                            childReaped = true;
                        }
                    }
                    else if (!exited && pidfd < 0)
                    {
                        int wp = LinuxNativeMethods.WaitPid(childPid, &waitStatus, LinuxNativeMethods.WNOHANG);
                        if (wp > 0)
                        {
                            exited = true;
                            childReaped = true;
                        }
                    }
                }

                if (!childReaped)
                {
                    ReapChild(childPid, out waitStatus);
                    childReaped = true;
                }

                if (outOpen)
                {
                    _ = DrainNonBlocking(outReadFd, stdoutStream, pScratch, readScratch.Length);
                }
                if (errOpen)
                {
                    _ = DrainNonBlocking(errOutReadFd, stderrStream, pScratch, readScratch.Length);
                }
            }

            if (stdinTask != null)
            {
                stdinTask.GetAwaiter().GetResult();
            }

            if (!timedOut && ct.IsCancellationRequested)
            {
                throw new OperationCanceledException(ct);
            }

            int exitCode = timedOut ? -1 : DecodeWaitStatus(waitStatus);

            return new SandboxExecutionResult
            {
                ExitCode = exitCode,
                StandardOutput = Encoding.UTF8.GetString(stdoutStream.GetBuffer(), 0, (int)stdoutStream.Length),
                StandardError = Encoding.UTF8.GetString(stderrStream.GetBuffer(), 0, (int)stderrStream.Length),
                StartupLatency = startupLatency,
                TotalDuration = totalSw.Elapsed,
                TimedOut = timedOut,
                IsolationMechanism = isolationMechanism
            };
        }
        finally
        {
            ctReg.Dispose();
            timeoutCts?.Dispose();

            if (childPid > 0 && !childReaped)
            {
                KillProcessGroup(childPid, pidfd);
                ReapChild(childPid, out _);
            }

            CloseAndReset(ref syncReadFd);
            CloseAndReset(ref syncWriteFd);
            CloseAndReset(ref errReadFd);
            CloseAndReset(ref errWriteFd);
            CloseAndReset(ref outReadFd);
            CloseAndReset(ref outWriteFd);
            CloseAndReset(ref errOutReadFd);
            CloseAndReset(ref errOutWriteFd);
            CloseAndReset(ref inReadFd);
            CloseAndReset(ref inWriteFd);
            CloseAndReset(ref unshareReadFd);
            CloseAndReset(ref unshareWriteFd);
            CloseAndReset(ref cancelReadFd);
            CloseAndReset(ref cancelWriteFd);
            CloseAndReset(ref devNullFd);
            CloseAndReset(ref pidfd);
        }
    }

    private static int ComputeRemainingTimeoutMs(Stopwatch totalSw, TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
        {
            return -1;
        }

        double remainingMs = (timeout - totalSw.Elapsed).TotalMilliseconds;
        return remainingMs <= 0 ? 0 : Math.Min(int.MaxValue, (int)Math.Ceiling(remainingMs));
    }

    private static string FormatIsolationMechanism(int mode, bool allowNetwork)
    {
        return mode switch
        {
            LinuxNativeMethods.GLACIER_MODE_CLONE3_NS => allowNetwork
                ? "Linux:clone3(user,pid,mnt,ipc,uts)+prctl(NO_NEW_PRIVS)+rlimit"
                : "Linux:clone3(user,pid,mnt,ipc,uts,net)+prctl(NO_NEW_PRIVS)+rlimit",
            LinuxNativeMethods.GLACIER_MODE_CLONE3_USERNS_DENIED =>
                "Linux:clone3(no-namespaces,userns-denied)+prctl(NO_NEW_PRIVS)+rlimit",
            LinuxNativeMethods.GLACIER_MODE_CLONE3_NO_NS =>
                "Linux:clone3(no-namespaces)+prctl(NO_NEW_PRIVS)+rlimit",
            LinuxNativeMethods.GLACIER_MODE_FORK_UNSHARE_NS => allowNetwork
                ? "Linux:fork+unshare(user,mnt,ipc,uts)+prctl(NO_NEW_PRIVS)+rlimit"
                : "Linux:fork+unshare(user,mnt,ipc,uts,net)+prctl(NO_NEW_PRIVS)+rlimit",
            _ => "Linux:fork(no-namespaces)+prctl(NO_NEW_PRIVS)+rlimit"
        };
    }

    private static bool TryReadStartupErrnoBounded(
        int errReadFd,
        int cancelReadFd,
        Stopwatch totalSw,
        TimeSpan timeout,
        CancellationToken effectiveCt,
        out int errBytes,
        out int childErrno)
    {
        int localErrno = 0;
        int total = 0;
        LinuxNativeMethods.PollFd* pollFds = stackalloc LinuxNativeMethods.PollFd[2];

        while (total < 4)
        {
            if (effectiveCt.IsCancellationRequested)
            {
                errBytes = total;
                childErrno = localErrno;
                return false;
            }

            pollFds[0] = new LinuxNativeMethods.PollFd { Fd = errReadFd, Events = LinuxNativeMethods.POLLIN };
            pollFds[1] = new LinuxNativeMethods.PollFd { Fd = cancelReadFd, Events = LinuxNativeMethods.POLLIN };

            int timeoutMs = ComputeRemainingTimeoutMs(totalSw, timeout);
            int pollRes = LinuxNativeMethods.Poll(pollFds, 2, timeoutMs);
            if (pollRes < 0)
            {
                int pollErr = Marshal.GetLastPInvokeError();
                if (pollErr == LinuxNativeMethods.EINTR)
                {
                    continue;
                }
                throw new Win32Exception(pollErr, "poll() failed while waiting for child startup.");
            }

            if (pollRes == 0 ||
                (pollFds[1].Revents & (LinuxNativeMethods.POLLIN | LinuxNativeMethods.POLLHUP | LinuxNativeMethods.POLLERR)) != 0 ||
                effectiveCt.IsCancellationRequested)
            {
                errBytes = total;
                childErrno = localErrno;
                return false;
            }

            if ((pollFds[0].Revents & (LinuxNativeMethods.POLLIN | LinuxNativeMethods.POLLHUP | LinuxNativeMethods.POLLERR)) != 0)
            {
                bool eof = false;
                while (total < 4)
                {
                    nint r = LinuxNativeMethods.Read(errReadFd, ((byte*)&localErrno) + total, (nuint)(4 - total));
                    if (r == 0)
                    {
                        eof = true;
                        break;
                    }
                    if (r > 0)
                    {
                        total += (int)r;
                        continue;
                    }

                    int readErr = Marshal.GetLastPInvokeError();
                    if (readErr == LinuxNativeMethods.EINTR)
                    {
                        continue;
                    }
                    if (readErr == LinuxNativeMethods.EAGAIN)
                    {
                        break;
                    }

                    eof = true;
                    break;
                }

                if (eof)
                {
                    break;
                }
            }
        }

        errBytes = total;
        childErrno = localErrno;
        return true;
    }

    private static bool TryReadUnshareStatusBounded(
        int unshareReadFd,
        int cancelReadFd,
        Stopwatch totalSw,
        TimeSpan timeout,
        CancellationToken effectiveCt,
        out byte status,
        out nint bytesRead)
    {
        byte localStatus = 0;
        LinuxNativeMethods.PollFd* pollFds = stackalloc LinuxNativeMethods.PollFd[2];

        while (true)
        {
            if (effectiveCt.IsCancellationRequested)
            {
                status = 0;
                bytesRead = 0;
                return false;
            }

            pollFds[0] = new LinuxNativeMethods.PollFd { Fd = unshareReadFd, Events = LinuxNativeMethods.POLLIN };
            pollFds[1] = new LinuxNativeMethods.PollFd { Fd = cancelReadFd, Events = LinuxNativeMethods.POLLIN };

            int timeoutMs = ComputeRemainingTimeoutMs(totalSw, timeout);
            int pollRes = LinuxNativeMethods.Poll(pollFds, 2, timeoutMs);
            if (pollRes < 0)
            {
                int pollErr = Marshal.GetLastPInvokeError();
                if (pollErr == LinuxNativeMethods.EINTR)
                {
                    continue;
                }
                throw new Win32Exception(pollErr, "poll() failed while waiting for unshare handshake.");
            }

            if (pollRes == 0 ||
                (pollFds[1].Revents & (LinuxNativeMethods.POLLIN | LinuxNativeMethods.POLLHUP | LinuxNativeMethods.POLLERR)) != 0 ||
                effectiveCt.IsCancellationRequested)
            {
                status = 0;
                bytesRead = 0;
                return false;
            }

            if ((pollFds[0].Revents & (LinuxNativeMethods.POLLIN | LinuxNativeMethods.POLLHUP | LinuxNativeMethods.POLLERR)) != 0)
            {
                nint r = LinuxNativeMethods.Read(unshareReadFd, &localStatus, 1);
                if (r < 0)
                {
                    int readErr = Marshal.GetLastPInvokeError();
                    if (readErr == LinuxNativeMethods.EINTR || readErr == LinuxNativeMethods.EAGAIN)
                    {
                        continue;
                    }
                    r = 0;
                }
                status = localStatus;
                bytesRead = r;
                return true;
            }
        }
    }

    private static int SpawnManagedFallback(
        SandboxOptions options,
        Stopwatch totalSw,
        CancellationToken effectiveCt,
        CancellationToken ct,
        int syncReadFd,
        int syncWriteFd,
        int errReadFd,
        int errWriteFd,
        int childInFd,
        int inWriteFd,
        int outReadFd,
        int outWriteFd,
        int errOutReadFd,
        int errOutWriteFd,
        int cancelReadFd,
        int cancelWriteFd,
        ref int unshareReadFd,
        ref int unshareWriteFd,
        byte* exePtr,
        byte** argvPtr,
        byte** envpPtr,
        byte* workDirPtr,
        byte* slashPtr,
        byte* procFsPtr,
        byte* procMountPtr,
        ref int pidfd,
        out bool userNsActive,
        out bool mountNsActive,
        out bool usedForkFallback,
        out string isolationMechanism,
        out SandboxExecutionResult? earlyTimeoutResult)
    {
        earlyTimeoutResult = null;
        userNsActive = false;
        mountNsActive = false;
        usedForkFallback = false;

        int childPid;
        if (options.EnableNamespaceIsolation)
        {
            ulong nsFlags = LinuxNativeMethods.CLONE_NEWUSER
                | LinuxNativeMethods.CLONE_NEWPID
                | LinuxNativeMethods.CLONE_NEWNS
                | LinuxNativeMethods.CLONE_NEWIPC
                | LinuxNativeMethods.CLONE_NEWUTS
                | LinuxNativeMethods.CLONE_PIDFD;

            if (!options.AllowNetwork)
            {
                nsFlags |= LinuxNativeMethods.CLONE_NEWNET;
            }

            int localPidfd = -1;
            var cloneArgs = new LinuxNativeMethods.CloneArgs
            {
                Flags = nsFlags,
                Pidfd = (ulong)&localPidfd,
                ExitSignal = (ulong)LinuxNativeMethods.SIGCHLD
            };

            bool inNoGc = GC.TryStartNoGCRegion(64 * 1024);
            nint res = LinuxNativeMethods.ChildSyscallClone3(
                LinuxNativeMethods.__NR_clone3,
                &cloneArgs,
                (nuint)sizeof(LinuxNativeMethods.CloneArgs));
            int cloneErr = res < 0 ? *LinuxNativeMethods.ChildErrnoLocation() : 0;
            if (res != 0 && inNoGc)
            {
                GC.EndNoGCRegion();
            }

            if (res >= 0)
            {
                pidfd = localPidfd;
                childPid = (int)res;
                userNsActive = true;
                mountNsActive = true;
                isolationMechanism = options.AllowNetwork
                    ? "Linux:clone3(user,pid,mnt,ipc,uts)+prctl(NO_NEW_PRIVS)+rlimit"
                    : "Linux:clone3(user,pid,mnt,ipc,uts,net)+prctl(NO_NEW_PRIVS)+rlimit";
                return childPid;
            }

            if (cloneErr == LinuxNativeMethods.EPERM || cloneErr == LinuxNativeMethods.EACCES)
            {
                localPidfd = -1;
                var plainArgs = new LinuxNativeMethods.CloneArgs
                {
                    Flags = LinuxNativeMethods.CLONE_PIDFD,
                    Pidfd = (ulong)&localPidfd,
                    ExitSignal = (ulong)LinuxNativeMethods.SIGCHLD
                };

                inNoGc = GC.TryStartNoGCRegion(64 * 1024);
                nint plainRes = LinuxNativeMethods.ChildSyscallClone3(
                    LinuxNativeMethods.__NR_clone3,
                    &plainArgs,
                    (nuint)sizeof(LinuxNativeMethods.CloneArgs));
                int plainErr = plainRes < 0 ? *LinuxNativeMethods.ChildErrnoLocation() : 0;
                if (plainRes != 0 && inNoGc)
                {
                    GC.EndNoGCRegion();
                }

                if (plainRes >= 0)
                {
                    pidfd = localPidfd;
                    isolationMechanism = "Linux:clone3(no-namespaces,userns-denied)+prctl(NO_NEW_PRIVS)+rlimit";
                    return (int)plainRes;
                }

                if (plainErr == LinuxNativeMethods.ENOSYS || plainErr == LinuxNativeMethods.EPERM || plainErr == LinuxNativeMethods.EACCES)
                {
                    usedForkFallback = true;
                    CreatePipePair(out unshareReadFd, out unshareWriteFd, LinuxNativeMethods.O_CLOEXEC);
                    return ForkChild(
                        useUnshareFallback: true,
                        requestedUnshareFlags: (int)(nsFlags & ~LinuxNativeMethods.CLONE_PIDFD),
                        unshareReadFd, unshareWriteFd,
                        syncReadFd, syncWriteFd,
                        errReadFd, errWriteFd,
                        childInFd, inWriteFd,
                        outReadFd, outWriteFd,
                        errOutReadFd, errOutWriteFd,
                        cancelReadFd, cancelWriteFd,
                        exePtr, argvPtr, envpPtr, workDirPtr,
                        slashPtr, procFsPtr, procMountPtr,
                        options,
                        totalSw,
                        effectiveCt,
                        ct,
                        out userNsActive,
                        out mountNsActive,
                        out isolationMechanism,
                        out earlyTimeoutResult);
                }

                throw new Win32Exception(plainErr, "clone3 fallback failed.");
            }
            else if (cloneErr == LinuxNativeMethods.ENOSYS)
            {
                usedForkFallback = true;
                CreatePipePair(out unshareReadFd, out unshareWriteFd, LinuxNativeMethods.O_CLOEXEC);
                return ForkChild(
                    useUnshareFallback: true,
                    requestedUnshareFlags: (int)(nsFlags & ~LinuxNativeMethods.CLONE_PIDFD),
                    unshareReadFd, unshareWriteFd,
                    syncReadFd, syncWriteFd,
                    errReadFd, errWriteFd,
                    childInFd, inWriteFd,
                    outReadFd, outWriteFd,
                    errOutReadFd, errOutWriteFd,
                    cancelReadFd, cancelWriteFd,
                    exePtr, argvPtr, envpPtr, workDirPtr,
                    slashPtr, procFsPtr, procMountPtr,
                    options,
                    totalSw,
                    effectiveCt,
                    ct,
                    out userNsActive,
                    out mountNsActive,
                    out isolationMechanism,
                    out earlyTimeoutResult);
            }

            throw new Win32Exception(cloneErr, $"clone3 failed with errno {cloneErr}.");
        }
        else
        {
            int localPidfd = -1;
            var plainArgs = new LinuxNativeMethods.CloneArgs
            {
                Flags = LinuxNativeMethods.CLONE_PIDFD,
                Pidfd = (ulong)&localPidfd,
                ExitSignal = (ulong)LinuxNativeMethods.SIGCHLD
            };

            bool inNoGc = GC.TryStartNoGCRegion(64 * 1024);
            nint res = LinuxNativeMethods.ChildSyscallClone3(
                LinuxNativeMethods.__NR_clone3,
                &plainArgs,
                (nuint)sizeof(LinuxNativeMethods.CloneArgs));
            int cloneErr = res < 0 ? *LinuxNativeMethods.ChildErrnoLocation() : 0;
            if (res != 0 && inNoGc)
            {
                GC.EndNoGCRegion();
            }

            if (res >= 0)
            {
                pidfd = localPidfd;
                isolationMechanism = "Linux:clone3(no-namespaces)+prctl(NO_NEW_PRIVS)+rlimit";
                return (int)res;
            }

            if (cloneErr == LinuxNativeMethods.ENOSYS || cloneErr == LinuxNativeMethods.EPERM || cloneErr == LinuxNativeMethods.EACCES)
            {
                usedForkFallback = true;
                return ForkChild(
                    useUnshareFallback: false,
                    requestedUnshareFlags: 0,
                    unshareReadFd, unshareWriteFd,
                    syncReadFd, syncWriteFd,
                    errReadFd, errWriteFd,
                    childInFd, inWriteFd,
                    outReadFd, outWriteFd,
                    errOutReadFd, errOutWriteFd,
                    cancelReadFd, cancelWriteFd,
                    exePtr, argvPtr, envpPtr, workDirPtr,
                    slashPtr, procFsPtr, procMountPtr,
                    options,
                    totalSw,
                    effectiveCt,
                    ct,
                    out userNsActive,
                    out mountNsActive,
                    out isolationMechanism,
                    out earlyTimeoutResult);
            }

            throw new Win32Exception(cloneErr, $"clone3(no-namespaces) failed with errno {cloneErr}.");
        }
    }

    private static int ForkChild(
        bool useUnshareFallback,
        int requestedUnshareFlags,
        int unshareReadFd,
        int unshareWriteFd,
        int syncReadFd,
        int syncWriteFd,
        int errReadFd,
        int errWriteFd,
        int childInFd,
        int inWriteFd,
        int outReadFd,
        int outWriteFd,
        int errOutReadFd,
        int errOutWriteFd,
        int cancelReadFd,
        int cancelWriteFd,
        byte* exePtr,
        byte** argvPtr,
        byte** envpPtr,
        byte* workDirPtr,
        byte* slashPtr,
        byte* procFsPtr,
        byte* procMountPtr,
        SandboxOptions options,
        Stopwatch totalSw,
        CancellationToken effectiveCt,
        CancellationToken ct,
        out bool userNsActive,
        out bool mountNsActive,
        out string isolationMechanism,
        out SandboxExecutionResult? earlyTimeoutResult)
    {
        earlyTimeoutResult = null;
        bool inNoGc = GC.TryStartNoGCRegion(64 * 1024);
        int pid = LinuxNativeMethods.ChildFork();
        int forkErr = pid < 0 ? *LinuxNativeMethods.ChildErrnoLocation() : 0;
        if (pid != 0 && inNoGc)
        {
            GC.EndNoGCRegion();
        }

        if (pid < 0)
        {
            throw new Win32Exception(forkErr, "fork() failed.");
        }

        if (pid == 0)
        {
            ChildCloseIfValid(unshareReadFd);
            ChildCloseIfValid(syncWriteFd);
            ChildCloseIfValid(errReadFd);
            ChildCloseIfValid(outReadFd);
            ChildCloseIfValid(errOutReadFd);
            ChildCloseIfValid(inWriteFd);
            ChildCloseIfValid(cancelReadFd);
            ChildCloseIfValid(cancelWriteFd);

            _ = ChildEntry(
                dryRun: false,
                useUnshareFallback: useUnshareFallback,
                requestedUnshareFlags: requestedUnshareFlags,
                unshareReportWriteFd: unshareWriteFd,
                syncReadFd: syncReadFd,
                errWriteFd: errWriteFd,
                inReadFd: childInFd,
                outWriteFd: outWriteFd,
                errOutWriteFd: errOutWriteFd,
                hasMountNs: false,
                slashPtr: slashPtr,
                procFsPtr: procFsPtr,
                procMountPtr: procMountPtr,
                exePtr: exePtr,
                argvPtr: argvPtr,
                envpPtr: envpPtr,
                workDirPtr: workDirPtr,
                hasMemoryLimit: options.MemoryLimitBytes.HasValue,
                memoryLimitBytes: options.MemoryLimitBytes.GetValueOrDefault(),
                hasMaxProcesses: options.MaxProcesses.HasValue,
                maxProcesses: options.MaxProcesses.GetValueOrDefault());

            LinuxNativeMethods.ChildExit(127);
        }

        ChildCloseIfValid(unshareWriteFd);

        if (useUnshareFallback && unshareReadFd >= 0)
        {
            SetNonBlocking(unshareReadFd);
            if (!TryReadUnshareStatusBounded(
                    unshareReadFd,
                    cancelReadFd,
                    totalSw,
                    options.Timeout,
                    effectiveCt,
                    out byte status,
                    out nint n))
            {
                KillProcessGroup(pid, -1);
                ReapChild(pid, out _);

                if (ct.IsCancellationRequested)
                {
                    throw new OperationCanceledException(ct);
                }

                userNsActive = false;
                mountNsActive = false;
                isolationMechanism = "Linux:fork(no-namespaces)+prctl(NO_NEW_PRIVS)+rlimit";
                earlyTimeoutResult = new SandboxExecutionResult
                {
                    ExitCode = -1,
                    StandardOutput = string.Empty,
                    StandardError = string.Empty,
                    StartupLatency = totalSw.Elapsed,
                    TotalDuration = totalSw.Elapsed,
                    TimedOut = true,
                    IsolationMechanism = isolationMechanism
                };
                return pid;
            }

            userNsActive = n == 1 && status == 1;
            mountNsActive = userNsActive;
            isolationMechanism = userNsActive
                ? (options.AllowNetwork
                    ? "Linux:fork+unshare(user,mnt,ipc,uts)+prctl(NO_NEW_PRIVS)+rlimit"
                    : "Linux:fork+unshare(user,mnt,ipc,uts,net)+prctl(NO_NEW_PRIVS)+rlimit")
                : "Linux:fork(no-namespaces)+prctl(NO_NEW_PRIVS)+rlimit";
        }
        else
        {
            userNsActive = false;
            mountNsActive = false;
            isolationMechanism = "Linux:fork(no-namespaces)+prctl(NO_NEW_PRIVS)+rlimit";
        }

        return pid;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int ChildEntry(
        bool dryRun,
        bool useUnshareFallback,
        int requestedUnshareFlags,
        int unshareReportWriteFd,
        int syncReadFd,
        int errWriteFd,
        int inReadFd,
        int outWriteFd,
        int errOutWriteFd,
        bool hasMountNs,
        byte* slashPtr,
        byte* procFsPtr,
        byte* procMountPtr,
        byte* exePtr,
        byte** argvPtr,
        byte** envpPtr,
        byte* workDirPtr,
        bool hasMemoryLimit,
        long memoryLimitBytes,
        bool hasMaxProcesses,
        int maxProcesses)
    {
        if (dryRun)
        {
            _ = LinuxNativeMethods.ChildClose(-1);
            byte tmp = 0;
            _ = LinuxNativeMethods.ChildRead(-1, &tmp, 0);
            _ = LinuxNativeMethods.ChildWrite(-1, &tmp, 0);
            _ = LinuxNativeMethods.ChildSetPgid(-1, -1);
            _ = LinuxNativeMethods.ChildUnshare(0);
            _ = LinuxNativeMethods.ChildMount(null, null, null, 0, null);
            _ = LinuxNativeMethods.ChildDup2(-1, -1);
            _ = LinuxNativeMethods.ChildChdir(null);
            _ = LinuxNativeMethods.ChildPrctl(-1, 0, 0, 0, 0);
            var dummyLimit = new LinuxNativeMethods.RLimit();
            _ = LinuxNativeMethods.ChildSetRLimit(-1, &dummyLimit);
            _ = LinuxNativeMethods.ChildExecve(null, null, null);
            _ = LinuxNativeMethods.ChildErrnoLocation();
            return 0;
        }

        if (useUnshareFallback)
        {
            byte unshared = 0;
            if (requestedUnshareFlags != 0 && LinuxNativeMethods.ChildUnshare(requestedUnshareFlags) == 0)
            {
                unshared = 1;
                hasMountNs = true;
            }
            if (unshareReportWriteFd >= 0)
            {
                _ = LinuxNativeMethods.ChildWrite(unshareReportWriteFd, &unshared, 1);
                _ = LinuxNativeMethods.ChildClose(unshareReportWriteFd);
            }
        }

        // Wait for parent uid_map/gid_map setup before mount/execve.
        byte go = 0;
        while (true)
        {
            nint r = LinuxNativeMethods.ChildRead(syncReadFd, &go, 1);
            if (r > 0)
            {
                break;
            }
            if (r == 0)
            {
                _ = LinuxNativeMethods.ChildClose(syncReadFd);
                LinuxNativeMethods.ChildExit(127);
            }
            if (*LinuxNativeMethods.ChildErrnoLocation() != LinuxNativeMethods.EINTR)
            {
                break;
            }
        }
        _ = LinuxNativeMethods.ChildClose(syncReadFd);

        _ = LinuxNativeMethods.ChildSetPgid(0, 0);

        if (hasMountNs)
        {
            _ = LinuxNativeMethods.ChildMount(null, slashPtr, null, LinuxNativeMethods.MS_REC | LinuxNativeMethods.MS_PRIVATE, null);
            _ = LinuxNativeMethods.ChildMount(
                procFsPtr,
                procMountPtr,
                procFsPtr,
                LinuxNativeMethods.MS_NOSUID | LinuxNativeMethods.MS_NOEXEC | LinuxNativeMethods.MS_NODEV,
                null);
        }

        if (inReadFd >= 0 && inReadFd != 0)
        {
            if (LinuxNativeMethods.ChildDup2(inReadFd, 0) < 0)
            {
                WriteChildErrnoAndExit(errWriteFd);
            }
            _ = LinuxNativeMethods.ChildClose(inReadFd);
        }

        if (outWriteFd != 1)
        {
            if (LinuxNativeMethods.ChildDup2(outWriteFd, 1) < 0)
            {
                WriteChildErrnoAndExit(errWriteFd);
            }
            _ = LinuxNativeMethods.ChildClose(outWriteFd);
        }

        if (errOutWriteFd != 2)
        {
            if (LinuxNativeMethods.ChildDup2(errOutWriteFd, 2) < 0)
            {
                WriteChildErrnoAndExit(errWriteFd);
            }
            _ = LinuxNativeMethods.ChildClose(errOutWriteFd);
        }

        if (workDirPtr != null)
        {
            if (LinuxNativeMethods.ChildChdir(workDirPtr) != 0)
            {
                WriteChildErrnoAndExit(errWriteFd);
            }
        }

        _ = LinuxNativeMethods.ChildPrctl(LinuxNativeMethods.PR_SET_NO_NEW_PRIVS, 1, 0, 0, 0);

        var coreZero = new LinuxNativeMethods.RLimit { RlimCur = 0, RlimMax = 0 };
        _ = LinuxNativeMethods.ChildSetRLimit(LinuxNativeMethods.RLIMIT_CORE, &coreZero);

        if (hasMemoryLimit)
        {
            ulong mem = (ulong)memoryLimitBytes;
            var memLimit = new LinuxNativeMethods.RLimit { RlimCur = mem, RlimMax = mem };
            _ = LinuxNativeMethods.ChildSetRLimit(LinuxNativeMethods.RLIMIT_DATA, &memLimit);
        }

        if (hasMaxProcesses)
        {
            ulong procs = (ulong)maxProcesses;
            var procLimit = new LinuxNativeMethods.RLimit { RlimCur = procs, RlimMax = procs };
            _ = LinuxNativeMethods.ChildSetRLimit(LinuxNativeMethods.RLIMIT_NPROC, &procLimit);
        }

        _ = LinuxNativeMethods.ChildExecve(exePtr, argvPtr, envpPtr);
        WriteChildErrnoAndExit(errWriteFd);
        return 127;
    }

    private static void WriteChildErrnoAndExit(int errWriteFd)
    {
        int err = *LinuxNativeMethods.ChildErrnoLocation();
        _ = LinuxNativeMethods.ChildWrite(errWriteFd, &err, 4);
        _ = LinuxNativeMethods.ChildClose(errWriteFd);
        LinuxNativeMethods.ChildExit(127);
    }

    private static void SetupUserNamespaceMaps(int childPid)
    {
        uint euid = LinuxNativeMethods.GetEuid();
        uint egid = LinuxNativeMethods.GetEgid();

        WriteProcSingleWrite($"/proc/{childPid}/setgroups", "deny", ignoreEnoent: true);
        WriteProcSingleWrite($"/proc/{childPid}/uid_map", $"0 {euid} 1\n", ignoreEnoent: false);
        WriteProcSingleWrite($"/proc/{childPid}/gid_map", $"0 {egid} 1\n", ignoreEnoent: false);
    }

    private static void WriteProcSingleWrite(string path, string content, bool ignoreEnoent)
    {
        byte[] pathBytes = Encoding.UTF8.GetBytes(path + "\0");
        byte[] dataBytes = Encoding.UTF8.GetBytes(content);

        fixed (byte* pPath = pathBytes)
        fixed (byte* pData = dataBytes)
        {
            int fd = LinuxNativeMethods.Open(pPath, LinuxNativeMethods.O_WRONLY | LinuxNativeMethods.O_CLOEXEC, 0);
            if (fd < 0)
            {
                int err = Marshal.GetLastPInvokeError();
                if (ignoreEnoent && (err == LinuxNativeMethods.ENOENT || err == LinuxNativeMethods.EPERM || err == LinuxNativeMethods.EACCES))
                {
                    return;
                }
                throw new Win32Exception(err, $"Failed to open '{path}'.");
            }

            try
            {
                nint written = LinuxNativeMethods.Write(fd, pData, (nuint)dataBytes.Length);
                if (written < 0)
                {
                    int err = Marshal.GetLastPInvokeError();
                    if (ignoreEnoent && (err == LinuxNativeMethods.EPERM || err == LinuxNativeMethods.EACCES))
                    {
                        return;
                    }
                    throw new Win32Exception(err, $"Failed to write '{path}'.");
                }
            }
            finally
            {
                _ = LinuxNativeMethods.Close(fd);
            }
        }
    }

    private static string ResolveExecutablePath(string executablePath, IReadOnlyDictionary<string, string>? overrides)
    {
        if (executablePath.Contains('/'))
        {
            return executablePath;
        }

        string? pathEnv = null;
        if (overrides != null && overrides.TryGetValue("PATH", out string? overridePath))
        {
            pathEnv = overridePath;
        }
        else
        {
            pathEnv = Environment.GetEnvironmentVariable("PATH");
        }

        if (string.IsNullOrEmpty(pathEnv))
        {
            pathEnv = "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin";
        }

        string[] dirs = pathEnv.Split(':', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < dirs.Length; i++)
        {
            string candidate = Path.Combine(dirs[i], executablePath);
            if (IsExecutableFile(candidate))
            {
                return candidate;
            }
        }

        string? dotnetRoot = overrides != null && overrides.TryGetValue("DOTNET_ROOT", out string? overrideRoot)
            ? overrideRoot
            : Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrEmpty(dotnetRoot))
        {
            string candidate = Path.Combine(dotnetRoot, executablePath);
            if (IsExecutableFile(candidate))
            {
                return candidate;
            }
        }

        string? processDir = Path.GetDirectoryName(Environment.ProcessPath);
        if (!string.IsNullOrEmpty(processDir))
        {
            string candidate = Path.Combine(processDir, executablePath);
            if (IsExecutableFile(candidate))
            {
                return candidate;
            }
        }

        return executablePath;
    }

    private static bool IsExecutableFile(string candidate)
    {
        byte[] candidateBytes = Encoding.UTF8.GetBytes(candidate + "\0");
        fixed (byte* pCandidate = candidateBytes)
        {
            return LinuxNativeMethods.Access(pCandidate, LinuxNativeMethods.X_OK) == 0 && File.Exists(candidate);
        }
    }

    private static byte** BuildArgv(UnmanagedPool pool, string resolvedExe, IReadOnlyList<string> arguments)
    {
        int count = 1 + arguments.Count;
        byte** argv = (byte**)pool.AllocRaw((nuint)((count + 1) * sizeof(byte*)));
        argv[0] = pool.AllocUtf8(resolvedExe);
        for (int i = 0; i < arguments.Count; i++)
        {
            argv[i + 1] = pool.AllocUtf8(arguments[i] ?? string.Empty);
        }
        argv[count] = null;
        return argv;
    }

    private static byte** BuildEnvp(UnmanagedPool pool, IReadOnlyDictionary<string, string>? overrides)
    {
        var merged = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string k && entry.Value is string v)
            {
                merged[k] = v;
            }
        }

        if (overrides != null)
        {
            foreach (KeyValuePair<string, string> kvp in overrides)
            {
                if (!string.IsNullOrEmpty(kvp.Key))
                {
                    merged[kvp.Key] = kvp.Value ?? string.Empty;
                }
            }
        }

        byte** envp = (byte**)pool.AllocRaw((nuint)((merged.Count + 1) * sizeof(byte*)));
        int idx = 0;
        foreach (KeyValuePair<string, string> kvp in merged)
        {
            envp[idx++] = pool.AllocUtf8($"{kvp.Key}={kvp.Value}");
        }
        envp[idx] = null;
        return envp;
    }

    private static void CreatePipePair(out int readFd, out int writeFd, int flags)
    {
        int* fds = stackalloc int[2];
        if (LinuxNativeMethods.Pipe2(fds, flags) != 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "pipe2 failed.");
        }
        readFd = fds[0];
        writeFd = fds[1];
    }

    private static void SetNonBlocking(int fd)
    {
        int flags = LinuxNativeMethods.Fcntl(fd, LinuxNativeMethods.F_GETFL, 0);
        if (flags < 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "fcntl(F_GETFL) failed.");
        }
        if (LinuxNativeMethods.Fcntl(fd, LinuxNativeMethods.F_SETFL, flags | LinuxNativeMethods.O_NONBLOCK) < 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "fcntl(F_SETFL, O_NONBLOCK) failed.");
        }
    }

    private static void WriteExact(int fd, void* buf, nuint count)
    {
        byte* ptr = (byte*)buf;
        nuint remaining = count;
        while (remaining > 0)
        {
            nint written = LinuxNativeMethods.Write(fd, ptr, remaining);
            if (written < 0)
            {
                int err = Marshal.GetLastPInvokeError();
                if (err == LinuxNativeMethods.EINTR)
                {
                    continue;
                }
                throw new Win32Exception(err, "write() to sync pipe failed.");
            }
            ptr += written;
            remaining -= (nuint)written;
        }
    }

    private static void WriteStdinAndClose(int fd, byte[] data)
    {
        try
        {
            fixed (byte* pData = data)
            {
                nuint offset = 0;
                while (offset < (nuint)data.Length)
                {
                    nint written = LinuxNativeMethods.Write(fd, pData + offset, (nuint)data.Length - offset);
                    if (written < 0)
                    {
                        int err = Marshal.GetLastPInvokeError();
                        if (err == LinuxNativeMethods.EINTR)
                        {
                            continue;
                        }
                        break;
                    }
                    offset += (nuint)written;
                }
            }
        }
        finally
        {
            _ = LinuxNativeMethods.Close(fd);
        }
    }

    private static bool DrainNonBlocking(int fd, MemoryStream dest, byte* scratch, int scratchLen)
    {
        while (true)
        {
            nint r = LinuxNativeMethods.Read(fd, scratch, (nuint)scratchLen);
            if (r > 0)
            {
                dest.Write(new ReadOnlySpan<byte>(scratch, (int)r));
                continue;
            }
            if (r == 0)
            {
                return false;
            }
            int err = Marshal.GetLastPInvokeError();
            if (err == LinuxNativeMethods.EINTR)
            {
                continue;
            }
            if (err == LinuxNativeMethods.EAGAIN)
            {
                return true;
            }
            return false;
        }
    }

    private static void KillProcessGroup(int childPid, int pidfd)
    {
        if (pidfd >= 0)
        {
            _ = LinuxNativeMethods.SyscallPidfdSendSignal(LinuxNativeMethods.__NR_pidfd_send_signal, pidfd, LinuxNativeMethods.SIGKILL, 0, 0);
        }
        if (childPid > 0)
        {
            _ = LinuxNativeMethods.Kill(-childPid, LinuxNativeMethods.SIGKILL);
            _ = LinuxNativeMethods.Kill(childPid, LinuxNativeMethods.SIGKILL);
        }
    }

    private static void ReapChild(int childPid, out int status)
    {
        int localStatus = 0;
        while (true)
        {
            int wp = LinuxNativeMethods.WaitPid(childPid, &localStatus, 0);
            if (wp < 0 && Marshal.GetLastPInvokeError() == LinuxNativeMethods.EINTR)
            {
                continue;
            }
            break;
        }
        status = localStatus;
    }

    private static int DecodeWaitStatus(int status)
    {
        int sig = status & 0x7F;
        if (sig == 0)
        {
            return (status >> 8) & 0xFF;
        }
        return 128 + sig;
    }

    private static void ChildCloseIfValid(int fd)
    {
        if (fd >= 0)
        {
            _ = LinuxNativeMethods.ChildClose(fd);
        }
    }

    private static void CloseAndReset(ref int fd)
    {
        if (fd >= 0)
        {
            _ = LinuxNativeMethods.Close(fd);
            fd = -1;
        }
    }

    private sealed class UnmanagedPool : IDisposable
    {
        private readonly List<nint> _coTaskMem = new();
        private readonly List<nint> _nativeAlloc = new();

        public byte* AllocUtf8(string value)
        {
            nint ptr = Marshal.StringToCoTaskMemUTF8(value);
            _coTaskMem.Add(ptr);
            return (byte*)ptr;
        }

        public void* AllocRaw(nuint byteCount)
        {
            void* ptr = NativeMemory.AllocZeroed(byteCount);
            _nativeAlloc.Add((nint)ptr);
            return ptr;
        }

        public void Dispose()
        {
            for (int i = 0; i < _coTaskMem.Count; i++)
            {
                Marshal.ZeroFreeCoTaskMemUTF8(_coTaskMem[i]);
            }
            _coTaskMem.Clear();

            for (int i = 0; i < _nativeAlloc.Count; i++)
            {
                NativeMemory.Free((void*)_nativeAlloc[i]);
            }
            _nativeAlloc.Clear();
        }
    }
}
