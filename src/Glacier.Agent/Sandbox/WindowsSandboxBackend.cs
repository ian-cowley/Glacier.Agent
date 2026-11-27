namespace Glacier.Agent.Sandbox;

using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

[SupportedOSPlatform("windows")]
internal static class WindowsSandboxBackend
{
    public static async ValueTask<SandboxExecutionResult> ExecuteAsync(SandboxOptions options, CancellationToken ct)
    {
        var totalSw = Stopwatch.StartNew();
        LaunchState state = LaunchProcess(options);

        try
        {
            Task stdinTask = state.SafeWIn != null
                ? WriteStandardInputAsync(state.SafeWIn, options.StandardInput!)
                : Task.CompletedTask;
            state.SafeWIn = null;

            Task<string> stdoutTask = ReadPipeToEndAsync(state.SafeROut!);
            Task<string> stderrTask = ReadPipeToEndAsync(state.SafeRErr!);
            state.SafeROut = null;
            state.SafeRErr = null;

            var exitTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var waitHandle = new ProcessWaitHandle(state.ProcessHandle);
            RegisteredWaitHandle? regWait = null;
            bool timedOut = false;

            try
            {
                regWait = ThreadPool.RegisterWaitForSingleObject(
                    waitHandle,
                    static (s, _) => ((TaskCompletionSource)s!).TrySetResult(),
                    exitTcs,
                    Timeout.Infinite,
                    executeOnlyOnce: true);

                using var timeoutCts = options.Timeout > TimeSpan.Zero
                    ? CancellationTokenSource.CreateLinkedTokenSource(ct)
                    : null;
                if (timeoutCts != null)
                {
                    timeoutCts.CancelAfter(options.Timeout);
                }

                CancellationToken effectiveCt = timeoutCts?.Token ?? ct;

                try
                {
                    await exitTcs.Task.WaitAsync(effectiveCt).ConfigureAwait(false);
                    await Task.WhenAll(stdinTask, stdoutTask, stderrTask).WaitAsync(effectiveCt).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    WindowsNativeMethods.TerminateJobObject(state.JobHandle, 1);
                    timedOut = !ct.IsCancellationRequested;
                    await Task.WhenAll(stdinTask, stdoutTask, stderrTask).ConfigureAwait(false);
                    if (!timedOut)
                    {
                        throw new OperationCanceledException(ct);
                    }
                }
            }
            finally
            {
                if (regWait != null)
                {
                    using var unregisterWait = new ManualResetEvent(false);
                    if (regWait.Unregister(unregisterWait))
                    {
                        unregisterWait.WaitOne();
                    }
                }
            }

            int exitCode = -1;
            if (!timedOut && WindowsNativeMethods.GetExitCodeProcess(state.ProcessHandle, out uint rawExitCode))
            {
                exitCode = unchecked((int)rawExitCode);
            }

            return new SandboxExecutionResult
            {
                ExitCode = exitCode,
                StandardOutput = stdoutTask.Result,
                StandardError = stderrTask.Result,
                StartupLatency = state.StartupLatency,
                TotalDuration = totalSw.Elapsed,
                TimedOut = timedOut,
                IsolationMechanism = "WindowsJobObject+RestrictedToken"
            };
        }
        finally
        {
            state.Dispose();
        }
    }

    private static unsafe LaunchState LaunchProcess(SandboxOptions options)
    {
        var startupSw = Stopwatch.StartNew();

        nint hProcToken = 0;
        nint hRestrictedToken = 0;
        nint hJob = 0;
        nint wOut = 0;
        nint wErr = 0;
        nint rIn = 0;
        nint wIn = 0;
        SafeFileHandle? safeROut = null;
        SafeFileHandle? safeRErr = null;
        SafeFileHandle? safeWIn = null;
        nint attrList = 0;
        bool attrInitialized = false;
        nint hProcess = 0;
        nint hThread = 0;

        try
        {
            if (!WindowsNativeMethods.OpenProcessToken(
                    WindowsNativeMethods.GetCurrentProcess(),
                    WindowsNativeMethods.TOKEN_ASSIGN_PRIMARY | WindowsNativeMethods.TOKEN_DUPLICATE | WindowsNativeMethods.TOKEN_QUERY,
                    out hProcToken))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "OpenProcessToken failed.");
            }

            if (!WindowsNativeMethods.CreateRestrictedToken(
                    hProcToken,
                    WindowsNativeMethods.DISABLE_MAX_PRIVILEGE,
                    0, 0, 0, 0, 0, 0,
                    out hRestrictedToken))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateRestrictedToken failed.");
            }

            CloseIfValid(ref hProcToken);

            hJob = WindowsNativeMethods.CreateJobObjectW(0, null);
            if (hJob == 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateJobObjectW failed.");
            }

            ConfigureJobObject(hJob, options);

            var sa = new WindowsNativeMethods.SECURITY_ATTRIBUTES
            {
                nLength = (uint)sizeof(WindowsNativeMethods.SECURITY_ATTRIBUTES),
                lpSecurityDescriptor = 0,
                bInheritHandle = 1
            };

            if (!WindowsNativeMethods.CreatePipe(out nint rawROut, out wOut, &sa, 0))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreatePipe(stdout) failed.");
            }
            safeROut = new SafeFileHandle(rawROut, ownsHandle: true);
            if (!WindowsNativeMethods.SetHandleInformation(rawROut, WindowsNativeMethods.HANDLE_FLAG_INHERIT, 0))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "SetHandleInformation(stdout) failed.");
            }

            if (!WindowsNativeMethods.CreatePipe(out nint rawRErr, out wErr, &sa, 0))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreatePipe(stderr) failed.");
            }
            safeRErr = new SafeFileHandle(rawRErr, ownsHandle: true);
            if (!WindowsNativeMethods.SetHandleInformation(rawRErr, WindowsNativeMethods.HANDLE_FLAG_INHERIT, 0))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "SetHandleInformation(stderr) failed.");
            }

            if (options.StandardInput != null)
            {
                if (!WindowsNativeMethods.CreatePipe(out rIn, out wIn, &sa, 0))
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreatePipe(stdin) failed.");
                }
                safeWIn = new SafeFileHandle(wIn, ownsHandle: true);
                wIn = 0;
                if (!WindowsNativeMethods.SetHandleInformation(safeWIn.DangerousGetHandle(), WindowsNativeMethods.HANDLE_FLAG_INHERIT, 0))
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "SetHandleInformation(stdin) failed.");
                }
            }

            nuint attrSize = 0;
            WindowsNativeMethods.InitializeProcThreadAttributeList(0, 2, 0, ref attrSize);
            attrList = (nint)NativeMemory.Alloc(attrSize);
            if (!WindowsNativeMethods.InitializeProcThreadAttributeList(attrList, 2, 0, ref attrSize))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "InitializeProcThreadAttributeList failed.");
            }
            attrInitialized = true;

            nint* inheritHandles = stackalloc nint[3];
            inheritHandles[0] = wOut;
            inheritHandles[1] = wErr;
            int handleCount = 2;
            if (rIn != 0)
            {
                inheritHandles[2] = rIn;
                handleCount = 3;
            }

            if (!WindowsNativeMethods.UpdateProcThreadAttribute(
                    attrList,
                    0,
                    WindowsNativeMethods.PROC_THREAD_ATTRIBUTE_HANDLE_LIST,
                    inheritHandles,
                    (nuint)(handleCount * sizeof(nint)),
                    0,
                    0))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "UpdateProcThreadAttribute(HANDLE_LIST) failed.");
            }

            nint* jobList = stackalloc nint[1];
            jobList[0] = hJob;
            if (!WindowsNativeMethods.UpdateProcThreadAttribute(
                    attrList,
                    0,
                    WindowsNativeMethods.PROC_THREAD_ATTRIBUTE_JOB_LIST,
                    jobList,
                    (nuint)sizeof(nint),
                    0,
                    0))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "UpdateProcThreadAttribute(JOB_LIST) failed.");
            }

            var siEx = new WindowsNativeMethods.STARTUPINFOEXW();
            siEx.StartupInfo.cb = (uint)sizeof(WindowsNativeMethods.STARTUPINFOEXW);
            siEx.StartupInfo.dwFlags = WindowsNativeMethods.STARTF_USESTDHANDLES;
            siEx.StartupInfo.hStdInput = rIn;
            siEx.StartupInfo.hStdOutput = wOut;
            siEx.StartupInfo.hStdError = wErr;
            siEx.lpAttributeList = attrList;

            char[] cmdChars = BuildCommandLine(options.ExecutablePath, options.Arguments);
            char[]? envChars = BuildEnvironmentBlock(options.EnvironmentVariables);
            string? appName = Path.IsPathFullyQualified(options.ExecutablePath) ? options.ExecutablePath : null;
            string? workDir = string.IsNullOrEmpty(options.WorkingDirectory) ? null : options.WorkingDirectory;

            var pi = new WindowsNativeMethods.PROCESS_INFORMATION();
            uint creationFlags = WindowsNativeMethods.CREATE_SUSPENDED
                | WindowsNativeMethods.CREATE_UNICODE_ENVIRONMENT
                | WindowsNativeMethods.EXTENDED_STARTUPINFO_PRESENT
                | WindowsNativeMethods.CREATE_NO_WINDOW;

            bool created;
            int lastError = 0;
            fixed (char* pCmd = cmdChars)
            fixed (char* pEnv = envChars)
            {
                created = WindowsNativeMethods.CreateProcessAsUserW(
                    hRestrictedToken,
                    appName,
                    pCmd,
                    0,
                    0,
                    bInheritHandles: true,
                    creationFlags,
                    pEnv,
                    workDir,
                    &siEx,
                    &pi);
                if (!created)
                {
                    lastError = Marshal.GetLastPInvokeError();
                }
            }

            CloseIfValid(ref wOut);
            CloseIfValid(ref wErr);
            CloseIfValid(ref rIn);

            if (attrInitialized)
            {
                WindowsNativeMethods.DeleteProcThreadAttributeList(attrList);
                attrInitialized = false;
            }
            if (attrList != 0)
            {
                NativeMemory.Free((void*)attrList);
                attrList = 0;
            }
            CloseIfValid(ref hRestrictedToken);

            if (!created)
            {
                throw new Win32Exception(lastError, $"CreateProcessAsUserW failed for '{options.ExecutablePath}'.");
            }

            hProcess = pi.hProcess;
            hThread = pi.hThread;

            if (WindowsNativeMethods.ResumeThread(hThread) == unchecked((uint)-1))
            {
                int resumeErr = Marshal.GetLastPInvokeError();
                WindowsNativeMethods.TerminateJobObject(hJob, 1);
                throw new Win32Exception(resumeErr, "ResumeThread failed.");
            }

            TimeSpan startupLatency = startupSw.Elapsed;
            CloseIfValid(ref hThread);

            var result = new LaunchState
            {
                JobHandle = hJob,
                ProcessHandle = hProcess,
                SafeROut = safeROut,
                SafeRErr = safeRErr,
                SafeWIn = safeWIn,
                StartupLatency = startupLatency
            };

            hJob = 0;
            hProcess = 0;
            safeROut = null;
            safeRErr = null;
            safeWIn = null;
            return result;
        }
        finally
        {
            CloseIfValid(ref wOut);
            CloseIfValid(ref wErr);
            CloseIfValid(ref rIn);
            CloseIfValid(ref wIn);
            safeWIn?.Dispose();
            safeROut?.Dispose();
            safeRErr?.Dispose();

            if (attrInitialized)
            {
                WindowsNativeMethods.DeleteProcThreadAttributeList(attrList);
            }
            if (attrList != 0)
            {
                NativeMemory.Free((void*)attrList);
            }

            CloseIfValid(ref hThread);
            CloseIfValid(ref hProcess);
            CloseIfValid(ref hJob);
            CloseIfValid(ref hRestrictedToken);
            CloseIfValid(ref hProcToken);
        }
    }

    private static unsafe void ConfigureJobObject(nint hJob, SandboxOptions options)
    {
        var ext = new WindowsNativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        uint flags = WindowsNativeMethods.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            | WindowsNativeMethods.JOB_OBJECT_LIMIT_DIE_ON_UNHANDLED_EXCEPTION;

        if (options.MaxProcesses is > 0)
        {
            flags |= WindowsNativeMethods.JOB_OBJECT_LIMIT_ACTIVE_PROCESS;
            ext.BasicLimitInformation.ActiveProcessLimit = (uint)options.MaxProcesses.Value;
        }

        if (options.MemoryLimitBytes is > 0)
        {
            flags |= WindowsNativeMethods.JOB_OBJECT_LIMIT_PROCESS_MEMORY
                | WindowsNativeMethods.JOB_OBJECT_LIMIT_JOB_MEMORY;
            nuint limit = checked((nuint)options.MemoryLimitBytes.Value);
            ext.ProcessMemoryLimit = limit;
            ext.JobMemoryLimit = limit;
        }

        ext.BasicLimitInformation.LimitFlags = flags;

        if (!WindowsNativeMethods.SetInformationJobObject(
                hJob,
                WindowsNativeMethods.JobObjectExtendedLimitInformation,
                &ext,
                (uint)sizeof(WindowsNativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION)))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "SetInformationJobObject(ExtendedLimitInformation) failed.");
        }

        var ui = new WindowsNativeMethods.JOBOBJECT_BASIC_UI_RESTRICTIONS
        {
            UIRestrictionsClass = WindowsNativeMethods.JOB_OBJECT_UILIMIT_ALL
        };

        if (!WindowsNativeMethods.SetInformationJobObject(
                hJob,
                WindowsNativeMethods.JobObjectBasicUIRestrictions,
                &ui,
                (uint)sizeof(WindowsNativeMethods.JOBOBJECT_BASIC_UI_RESTRICTIONS)))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "SetInformationJobObject(BasicUIRestrictions) failed.");
        }
    }

    private const int MaxCapturedOutputChars = 4 * 1024 * 1024;

    private static Task<string> ReadPipeToEndAsync(SafeFileHandle readHandle)
    {
        return Task.Run(() =>
        {
            using var fs = new FileStream(readHandle, FileAccess.Read, 4096, isAsync: false);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            var sb = new StringBuilder();
            char[] buf = new char[4096];
            bool truncated = false;
            int read;
            while ((read = reader.Read(buf, 0, buf.Length)) > 0)
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
        });
    }

    private static Task WriteStandardInputAsync(SafeFileHandle writeHandle, string input)
    {
        return Task.Run(() =>
        {
            using var fs = new FileStream(writeHandle, FileAccess.Write, 4096, isAsync: false);
            byte[] bytes = Encoding.UTF8.GetBytes(input);
            try
            {
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush();
            }
            catch (IOException)
            {
            }
        });
    }

    internal static char[] BuildCommandLine(string executablePath, IReadOnlyList<string> arguments)
    {
        var sb = new StringBuilder();
        AppendEscapedArgument(sb, executablePath);
        for (int i = 0; i < arguments.Count; i++)
        {
            sb.Append(' ');
            AppendEscapedArgument(sb, arguments[i] ?? string.Empty);
        }
        sb.Append('\0');

        var buffer = new char[sb.Length];
        sb.CopyTo(0, buffer, 0, sb.Length);
        return buffer;
    }

    private static void AppendEscapedArgument(StringBuilder sb, string arg)
    {
        if (arg.Length > 0 && !NeedsQuoting(arg))
        {
            sb.Append(arg);
            return;
        }

        sb.Append('"');
        int backslashes = 0;
        for (int i = 0; i < arg.Length; i++)
        {
            char c = arg[i];
            if (c == '\\')
            {
                backslashes++;
            }
            else if (c == '"')
            {
                sb.Append('\\', backslashes * 2 + 1);
                sb.Append('"');
                backslashes = 0;
            }
            else
            {
                if (backslashes > 0)
                {
                    sb.Append('\\', backslashes);
                    backslashes = 0;
                }
                sb.Append(c);
            }
        }

        if (backslashes > 0)
        {
            sb.Append('\\', backslashes * 2);
        }

        sb.Append('"');
    }

    private static bool NeedsQuoting(string arg)
    {
        for (int i = 0; i < arg.Length; i++)
        {
            char c = arg[i];
            if (char.IsWhiteSpace(c) || c == '"')
            {
                return true;
            }
        }
        return false;
    }

    private static char[]? BuildEnvironmentBlock(IReadOnlyDictionary<string, string>? overrides)
    {
        if (overrides is null or { Count: 0 })
        {
            return null;
        }

        var merged = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string k && entry.Value is string v)
            {
                merged[k] = v;
            }
        }

        foreach (KeyValuePair<string, string> kvp in overrides)
        {
            if (!string.IsNullOrEmpty(kvp.Key))
            {
                merged[kvp.Key] = kvp.Value ?? string.Empty;
            }
        }

        var sb = new StringBuilder();
        foreach (KeyValuePair<string, string> kvp in merged)
        {
            sb.Append(kvp.Key).Append('=').Append(kvp.Value).Append('\0');
        }
        sb.Append('\0');

        var buffer = new char[sb.Length];
        sb.CopyTo(0, buffer, 0, sb.Length);
        return buffer;
    }

    private static void CloseIfValid(ref nint handle)
    {
        if (handle != 0)
        {
            WindowsNativeMethods.CloseHandle(handle);
            handle = 0;
        }
    }

    private sealed class LaunchState : IDisposable
    {
        public nint JobHandle;
        public nint ProcessHandle;
        public SafeFileHandle? SafeROut;
        public SafeFileHandle? SafeRErr;
        public SafeFileHandle? SafeWIn;
        public TimeSpan StartupLatency;

        public void Dispose()
        {
            SafeWIn?.Dispose();
            SafeROut?.Dispose();
            SafeRErr?.Dispose();
            CloseIfValid(ref ProcessHandle);
            CloseIfValid(ref JobHandle);
        }
    }

    private sealed class ProcessWaitHandle : WaitHandle
    {
        public ProcessWaitHandle(nint handle)
        {
            SafeWaitHandle = new SafeWaitHandle(handle, ownsHandle: false);
        }
    }
}
