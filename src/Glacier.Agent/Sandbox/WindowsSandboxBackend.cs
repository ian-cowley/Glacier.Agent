namespace Glacier.Agent.Sandbox;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
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
                IsolationMechanism = state.IsolationMechanism
            };
        }
        finally
        {
            state.Dispose();
        }
    }

    private const string AppContainerProfileName = "Glacier.Agent.Sandbox.NoNet";
    private const int HResultErrorAlreadyExists = unchecked((int)0x800700B7);
    private static readonly Lock s_appContainerInitLock = new();
    private static AppContainerEnvironment? s_appContainerEnv;

    private static readonly string[] s_safeEnvironmentAllowlist =
    [
        "PATH",
        "SYSTEMROOT",
        "SYSTEMDRIVE",
        "WINDIR",
        "COMSPEC",
        "TEMP",
        "TMP",
        "PATHEXT",
        "OS",
        "NUMBER_OF_PROCESSORS",
        "PROCESSOR_ARCHITECTURE",
        "PROCESSOR_IDENTIFIER",
        "PROCESSOR_LEVEL",
        "PROCESSOR_REVISION",
        "USERPROFILE",
        "LOCALAPPDATA",
        "APPDATA",
        "ALLUSERSPROFILE",
        "ProgramData",
        "ProgramFiles",
        "ProgramFiles(x86)",
        "CommonProgramFiles",
        "CommonProgramFiles(x86)",
        "DOTNET_ROOT",
        "DOTNET_ROOT(x86)"
    ];

    private static unsafe AppContainerEnvironment GetOrCreateAppContainerEnvironment()
    {
        AppContainerEnvironment? existing = Volatile.Read(ref s_appContainerEnv);
        if (existing != null)
        {
            return existing;
        }

        lock (s_appContainerInitLock)
        {
            if (s_appContainerEnv != null)
            {
                return s_appContainerEnv;
            }

            int hr = WindowsNativeMethods.CreateAppContainerProfile(
                AppContainerProfileName,
                "Glacier Agent Sandbox",
                "Network-isolated Low-IL AppContainer sandbox",
                0,
                0,
                out nint pSid);
            if (hr == HResultErrorAlreadyExists)
            {
                hr = WindowsNativeMethods.DeriveAppContainerSidFromAppContainerName(AppContainerProfileName, out pSid);
            }
            if (hr < 0 || pSid == 0)
            {
                Marshal.ThrowExceptionForHR(hr < 0 ? hr : unchecked((int)0x80004005));
            }

            bool sidTransferred = false;
            nint pSidStr = 0;
            nint pSd = 0;
            try
            {
                if (!WindowsNativeMethods.ConvertSidToStringSidW(pSid, out pSidStr) || pSidStr == 0)
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "ConvertSidToStringSidW failed for AppContainer SID.");
                }

                string sidStr = Marshal.PtrToStringUni(pSidStr)
                    ?? throw new Win32Exception("ConvertSidToStringSidW returned null.");
                string sddl = $"D:(A;;GA;;;{sidStr})(A;;GA;;;IU)(A;;GA;;;BA)(A;;GA;;;SY)S:(ML;;NW;;;LW)";

                if (!WindowsNativeMethods.ConvertStringSecurityDescriptorToSecurityDescriptorW(
                        sddl,
                        WindowsNativeMethods.SDDL_REVISION_1,
                        out pSd,
                        0) || pSd == 0)
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "ConvertStringSecurityDescriptorToSecurityDescriptorW failed.");
                }

                var sa = new WindowsNativeMethods.SECURITY_ATTRIBUTES
                {
                    nLength = (uint)sizeof(WindowsNativeMethods.SECURITY_ATTRIBUTES),
                    lpSecurityDescriptor = pSd,
                    bInheritHandle = 0
                };

                const string desktopName = "GlacierSandboxDesktop";

                nint hWinSta = WindowsNativeMethods.CreateWindowStationW(
                    null,
                    0,
                    WindowsNativeMethods.WINSTA_ALL_ACCESS_FULL,
                    &sa);
                if (hWinSta == 0)
                {
                    int err = Marshal.GetLastPInvokeError();
                    throw new Win32Exception(err, $"CreateWindowStationW failed for isolated AppContainer WindowStation (err={err}).");
                }

                char* nameBuf = stackalloc char[256];
                if (!WindowsNativeMethods.GetUserObjectInformationW(
                        hWinSta,
                        WindowsNativeMethods.UOI_NAME,
                        nameBuf,
                        256u * sizeof(char),
                        out uint neededBytes) || neededBytes < sizeof(char))
                {
                    int err = Marshal.GetLastPInvokeError();
                    throw new Win32Exception(err, $"GetUserObjectInformationW(UOI_NAME) failed (err={err}).");
                }

                int nameChars = (int)(neededBytes / sizeof(char));
                string winStaName = new string(nameBuf, 0, nameChars).TrimEnd('\0');

                nint hOrigWinSta = WindowsNativeMethods.GetProcessWindowStation();
                if (!WindowsNativeMethods.SetProcessWindowStation(hWinSta))
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "SetProcessWindowStation failed.");
                }

                nint hDesktop;
                try
                {
                    hDesktop = WindowsNativeMethods.CreateDesktopW(
                        desktopName,
                        null,
                        0,
                        0,
                        WindowsNativeMethods.DESKTOP_ALL_ACCESS_FULL,
                        &sa);
                    if (hDesktop == 0)
                    {
                        throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateDesktopW failed for isolated AppContainer Desktop.");
                    }
                }
                finally
                {
                    if (hOrigWinSta != 0)
                    {
                        _ = WindowsNativeMethods.SetProcessWindowStation(hOrigWinSta);
                    }
                }

                char[] desktopPathChars = $"{winStaName}\\{desktopName}\0".ToCharArray();
                uint sidLen = WindowsNativeMethods.GetLengthSid(pSid);
                byte[] sidBytes = new byte[sidLen];
                Marshal.Copy(pSid, sidBytes, 0, (int)sidLen);
                var acSecurityIdentifier = new SecurityIdentifier(sidBytes, 0);

                var created = new AppContainerEnvironment(pSid, acSecurityIdentifier, hWinSta, hDesktop, desktopPathChars);
                sidTransferred = true;
                Volatile.Write(ref s_appContainerEnv, created);
                return created;
            }
            finally
            {
                if (pSidStr != 0)
                {
                    _ = WindowsNativeMethods.LocalFree(pSidStr);
                }
                if (pSd != 0)
                {
                    _ = WindowsNativeMethods.LocalFree(pSd);
                }
                if (!sidTransferred && pSid != 0)
                {
                    _ = WindowsNativeMethods.FreeSid(pSid);
                }
            }
        }
    }

    private static unsafe LaunchState LaunchProcess(SandboxOptions options)
    {
        var startupSw = Stopwatch.StartNew();

        bool useAppContainer = !options.AllowNetwork;
        AppContainerEnvironment? appContainerEnv = useAppContainer ? GetOrCreateAppContainerEnvironment() : null;
        string isolationMechanism = useAppContainer
            ? "WindowsJobObject+AppContainer+LowIL"
            : "WindowsJobObject+RestrictedToken";

        string resolvedExe = ResolveExecutablePath(options.ExecutablePath, options.EnvironmentVariables);
        AppContainerAclScope? aclScope = null;
        bool transferredAclScope = false;

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
            bool isAppContainerNode = useAppContainer && IsNodeExecutable(resolvedExe, options.ExecutablePath);
            if (useAppContainer)
            {
                aclScope = AppContainerAclScope.Create(appContainerEnv!.SecurityIdentifier, options, resolvedExe, isAppContainerNode);
            }
            else
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
            }

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

            uint attrCount = useAppContainer ? 3u : 2u;
            nuint attrSize = 0;
            WindowsNativeMethods.InitializeProcThreadAttributeList(0, attrCount, 0, ref attrSize);
            attrList = (nint)NativeMemory.Alloc(attrSize);
            if (!WindowsNativeMethods.InitializeProcThreadAttributeList(attrList, attrCount, 0, ref attrSize))
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

            var secCaps = new WindowsNativeMethods.SECURITY_CAPABILITIES();
            if (useAppContainer)
            {
                secCaps.AppContainerSid = appContainerEnv!.AppContainerSid;
                secCaps.Capabilities = 0;
                secCaps.CapabilityCount = 0;
                secCaps.Reserved = 0;

                if (!WindowsNativeMethods.UpdateProcThreadAttribute(
                        attrList,
                        0,
                        WindowsNativeMethods.PROC_THREAD_ATTRIBUTE_SECURITY_CAPABILITY,
                        &secCaps,
                        (nuint)sizeof(WindowsNativeMethods.SECURITY_CAPABILITIES),
                        0,
                        0))
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "UpdateProcThreadAttribute(SECURITY_CAPABILITY) failed.");
                }
            }

            var siEx = new WindowsNativeMethods.STARTUPINFOEXW();
            siEx.StartupInfo.cb = (uint)sizeof(WindowsNativeMethods.STARTUPINFOEXW);
            siEx.StartupInfo.dwFlags = WindowsNativeMethods.STARTF_USESTDHANDLES;
            siEx.StartupInfo.hStdInput = rIn;
            siEx.StartupInfo.hStdOutput = wOut;
            siEx.StartupInfo.hStdError = wErr;
            siEx.lpAttributeList = attrList;

            char[] cmdChars = BuildCommandLine(options.ExecutablePath, options.Arguments);
            char[] envChars = BuildEnvironmentBlock(options.EnvironmentVariables, isAppContainerNode);
            string? appName = Path.IsPathFullyQualified(resolvedExe) ? resolvedExe : null;
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
            fixed (char* pDesktop = appContainerEnv?.DesktopPathChars)
            {
                if (useAppContainer)
                {
                    siEx.StartupInfo.lpDesktop = (nint)pDesktop;
                    created = WindowsNativeMethods.CreateProcessW(
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
                }
                else
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
                }

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
                string apiName = useAppContainer ? "CreateProcessW" : "CreateProcessAsUserW";
                throw new Win32Exception(lastError, $"{apiName} failed for '{options.ExecutablePath}'.");
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
                AclScope = aclScope,
                StartupLatency = startupLatency,
                IsolationMechanism = isolationMechanism
            };

            transferredAclScope = true;
            hJob = 0;
            hProcess = 0;
            safeROut = null;
            safeRErr = null;
            safeWIn = null;
            return result;
        }
        finally
        {
            if (!transferredAclScope)
            {
                aclScope?.Dispose();
            }
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

    private static bool IsNodeExecutable(string resolvedExe, string rawExe)
    {
        string name = Path.GetFileNameWithoutExtension(resolvedExe);
        if (string.Equals(name, "node", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return string.Equals(Path.GetFileNameWithoutExtension(rawExe), "node", StringComparison.OrdinalIgnoreCase);
    }

    private static char[] BuildEnvironmentBlock(IReadOnlyDictionary<string, string>? overrides, bool isAppContainerNode)
    {
        var merged = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (string key in s_safeEnvironmentAllowlist)
        {
            string? val = Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrEmpty(val))
            {
                merged[key] = val;
            }
        }

        if (!merged.ContainsKey("LOCALAPPDATA"))
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrEmpty(localAppData))
            {
                merged["LOCALAPPDATA"] = localAppData;
            }
        }

        if (!merged.ContainsKey("SYSTEMROOT"))
        {
            string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (!string.IsNullOrEmpty(winDir))
            {
                merged["SYSTEMROOT"] = winDir;
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

        if (isAppContainerNode)
        {
            // Node.js Module._findPath calls realpathSync -> lstat("C:\\") unless both
            // --preserve-symlinks and --preserve-symlinks-main are enabled; Windows AppContainer
            // lacks lstat permission on C:\ root, while NTFS kernel ACLs still enforce target confinement.
            merged.TryGetValue("NODE_OPTIONS", out string? existingNodeOpts);
            string nodeOpts = existingNodeOpts ?? string.Empty;
            if (!nodeOpts.Contains("--preserve-symlinks-main", StringComparison.Ordinal))
            {
                nodeOpts = string.IsNullOrWhiteSpace(nodeOpts)
                    ? "--preserve-symlinks-main"
                    : nodeOpts + " --preserve-symlinks-main";
            }
            if (!nodeOpts.Contains("--preserve-symlinks ", StringComparison.Ordinal) &&
                !nodeOpts.EndsWith("--preserve-symlinks", StringComparison.Ordinal))
            {
                nodeOpts = nodeOpts + " --preserve-symlinks";
            }
            merged["NODE_OPTIONS"] = nodeOpts;
        }

        var sb = new StringBuilder();
        foreach (KeyValuePair<string, string> kvp in merged)
        {
            sb.Append(kvp.Key).Append('=').Append(kvp.Value).Append('\0');
        }
        if (sb.Length == 0)
        {
            sb.Append('\0');
        }
        sb.Append('\0');

        var buffer = new char[sb.Length];
        sb.CopyTo(0, buffer, 0, sb.Length);
        return buffer;
    }

    private static string ResolveExecutablePath(string executablePath, IReadOnlyDictionary<string, string>? overrides)
    {
        if (Path.IsPathFullyQualified(executablePath))
        {
            return Path.GetFullPath(executablePath);
        }

        if (executablePath.Contains(Path.DirectorySeparatorChar) || executablePath.Contains(Path.AltDirectorySeparatorChar))
        {
            string candidate = Path.GetFullPath(executablePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }
            return executablePath;
        }

        string? pathExtRaw = (overrides != null && overrides.TryGetValue("PATHEXT", out string? overrideExt))
            ? overrideExt
            : Environment.GetEnvironmentVariable("PATHEXT");
        string[] exts = Path.HasExtension(executablePath)
            ? [string.Empty]
            : (string.IsNullOrWhiteSpace(pathExtRaw) ? [".EXE", ".CMD", ".BAT", ".COM"] : pathExtRaw.Split(';', StringSplitOptions.RemoveEmptyEntries));

        string? pathEnv = (overrides != null && overrides.TryGetValue("PATH", out string? overridePath))
            ? overridePath
            : Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(pathEnv))
        {
            foreach (string rawDir in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                string dir = rawDir.Trim().Trim('"');
                if (string.IsNullOrEmpty(dir)) continue;

                foreach (string ext in exts)
                {
                    string candidate = Path.Combine(dir, executablePath + ext);
                    if (File.Exists(candidate))
                    {
                        return Path.GetFullPath(candidate);
                    }
                }
            }
        }

        string? dotnetRoot = (overrides != null && overrides.TryGetValue("DOTNET_ROOT", out string? overrideRoot))
            ? overrideRoot
            : Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrEmpty(dotnetRoot))
        {
            foreach (string ext in exts)
            {
                string candidate = Path.Combine(dotnetRoot, executablePath + ext);
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
        }

        return executablePath;
    }

    private static void CloseIfValid(ref nint handle)
    {
        if (handle != 0)
        {
            WindowsNativeMethods.CloseHandle(handle);
            handle = 0;
        }
    }

    private sealed class AppContainerEnvironment
    {
        public readonly nint AppContainerSid;
        public readonly SecurityIdentifier SecurityIdentifier;
        public readonly nint WindowStationHandle;
        public readonly nint DesktopHandle;
        public readonly char[] DesktopPathChars;

        public AppContainerEnvironment(
            nint appContainerSid,
            SecurityIdentifier securityIdentifier,
            nint windowStationHandle,
            nint desktopHandle,
            char[] desktopPathChars)
        {
            AppContainerSid = appContainerSid;
            SecurityIdentifier = securityIdentifier;
            WindowStationHandle = windowStationHandle;
            DesktopHandle = desktopHandle;
            DesktopPathChars = desktopPathChars;
        }
    }

    private sealed class AppContainerAclScope : IDisposable
    {
        private enum AclAccessMode : byte
        {
            Traverse = 0,
            Read = 1,
            Write = 2
        }

        private readonly record struct AclKey(string NormalizedPath, bool InheritSubtree);

        private sealed class AclRefEntry
        {
            public readonly Lock EntryLock = new();
            public int ActiveHolders;
            public int TraverseCount;
            public int ReadCount;
            public int WriteCount;
            public AclAccessMode? AppliedMode;

            public AclAccessMode? GetDesiredMode()
            {
                if (WriteCount > 0) return AclAccessMode.Write;
                if (ReadCount > 0) return AclAccessMode.Read;
                if (TraverseCount > 0) return AclAccessMode.Traverse;
                return null;
            }

            public void Increment(AclAccessMode mode)
            {
                switch (mode)
                {
                    case AclAccessMode.Traverse: TraverseCount++; break;
                    case AclAccessMode.Read: ReadCount++; break;
                    case AclAccessMode.Write: WriteCount++; break;
                }
            }

            public void Decrement(AclAccessMode mode)
            {
                switch (mode)
                {
                    case AclAccessMode.Traverse:
                        if (TraverseCount > 0) TraverseCount--;
                        break;
                    case AclAccessMode.Read:
                        if (ReadCount > 0) ReadCount--;
                        break;
                    case AclAccessMode.Write:
                        if (WriteCount > 0) WriteCount--;
                        break;
                }
            }
        }

        private static readonly Lock s_aclMapLock = new();
        private static readonly Dictionary<AclKey, AclRefEntry> s_activeAcls = new();
        private static readonly string[] s_standardSystemPrefixes = BuildStandardSystemPrefixes();

        private readonly SecurityIdentifier _sid;
        private readonly bool _isNodeRuntime;
        private readonly List<(AclKey Key, AclRefEntry Entry, bool IsDirectory, AclAccessMode Mode)> _acquired = new();

        private AppContainerAclScope(SecurityIdentifier sid, bool isNodeRuntime)
        {
            _sid = sid;
            _isNodeRuntime = isNodeRuntime;
        }

        public static AppContainerAclScope? Create(SecurityIdentifier sid, SandboxOptions options, string resolvedExe, bool isNodeRuntime)
        {
            var scope = new AppContainerAclScope(sid, isNodeRuntime);
            try
            {
                if (!string.IsNullOrWhiteSpace(options.WorkingDirectory) && Directory.Exists(options.WorkingDirectory))
                {
                    scope.AcquirePath(options.WorkingDirectory, AclAccessMode.Write, inheritSubtree: true);
                }

                if (options.WritablePaths != null)
                {
                    for (int i = 0; i < options.WritablePaths.Count; i++)
                    {
                        string path = options.WritablePaths[i];
                        if (!string.IsNullOrWhiteSpace(path))
                        {
                            scope.AcquirePath(path, AclAccessMode.Write, inheritSubtree: true);
                        }
                    }
                }

                if (options.ReadablePaths != null)
                {
                    for (int i = 0; i < options.ReadablePaths.Count; i++)
                    {
                        string path = options.ReadablePaths[i];
                        if (!string.IsNullOrWhiteSpace(path))
                        {
                            scope.AcquirePath(path, AclAccessMode.Read, inheritSubtree: true);
                        }
                    }
                }

                if (Path.IsPathFullyQualified(resolvedExe) && File.Exists(resolvedExe))
                {
                    string? exeDir = Path.GetDirectoryName(resolvedExe);
                    if (!string.IsNullOrEmpty(exeDir) && !IsUnderStandardSystemDirectory(exeDir))
                    {
                        scope.AcquireExecutableRuntimePaths(resolvedExe, exeDir);
                    }
                }

                if (scope._acquired.Count == 0)
                {
                    return null;
                }

                return scope;
            }
            catch
            {
                scope.Dispose();
                throw;
            }
        }

        private void AcquireExecutableRuntimePaths(string resolvedExe, string exeDir)
        {
            // Fast check: if directory has <= 256 direct entries and no massive conda/venv subtrees, grant recursive RX directly.
            bool hasLargeRuntimeSubtree = Directory.Exists(Path.Combine(exeDir, "pkgs"))
                || Directory.Exists(Path.Combine(exeDir, "envs"))
                || Directory.Exists(Path.Combine(exeDir, "Lib", "site-packages"));

            if (!hasLargeRuntimeSubtree)
            {
                int shallowCount = 0;
                foreach (string _ in Directory.EnumerateFileSystemEntries(exeDir))
                {
                    if (++shallowCount > 256) break;
                }

                if (shallowCount <= 256)
                {
                    AcquirePath(exeDir, AclAccessMode.Read, inheritSubtree: true);
                }
                else
                {
                    AcquireShallowDirAndFiles(exeDir);
                }
            }
            else
            {
                AcquireShallowDirAndFiles(exeDir);

                string dllsDir = Path.Combine(exeDir, "DLLs");
                if (Directory.Exists(dllsDir))
                {
                    AcquirePath(dllsDir, AclAccessMode.Read, inheritSubtree: true);
                }

                string libBinDir = Path.Combine(exeDir, "Library", "bin");
                if (Directory.Exists(libBinDir))
                {
                    AcquirePath(libBinDir, AclAccessMode.Read, inheritSubtree: true);
                }

                string libDir = Path.Combine(exeDir, "Lib");
                if (Directory.Exists(libDir))
                {
                    AcquireShallowDirAndFiles(libDir);
                    foreach (string subDir in Directory.EnumerateDirectories(libDir))
                    {
                        string name = Path.GetFileName(subDir);
                        if (string.Equals(name, "site-packages", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(name, "__pycache__", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(name, "test", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(name, "tests", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(name, "idlelib", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(name, "tkinter", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(name, "turtledemo", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(name, "ensurepip", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(name, "lib2to3", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(name, "venv", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(name, "pydoc_data", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }
                        AcquirePath(subDir, AclAccessMode.Read, inheritSubtree: true);
                    }
                }
            }

            // Support Python virtual environments whose pyvenv.cfg points to a base Python home outside system dirs.
            string? parentDir = Path.GetDirectoryName(exeDir);
            if (!string.IsNullOrEmpty(parentDir))
            {
                string pyvenvCfg = Path.Combine(parentDir, "pyvenv.cfg");
                if (File.Exists(pyvenvCfg))
                {
                    AcquirePath(pyvenvCfg, AclAccessMode.Read, inheritSubtree: false);
                    foreach (string line in File.ReadLines(pyvenvCfg))
                    {
                        int eq = line.IndexOf('=');
                        if (eq > 0 && line.AsSpan(0, eq).Trim().Equals("home", StringComparison.OrdinalIgnoreCase))
                        {
                            string baseHome = line[(eq + 1)..].Trim();
                            if (!string.IsNullOrEmpty(baseHome) &&
                                Directory.Exists(baseHome) &&
                                !IsUnderStandardSystemDirectory(baseHome))
                            {
                                AcquireExecutableRuntimePaths(Path.Combine(baseHome, Path.GetFileName(resolvedExe)), baseHome);
                            }
                            break;
                        }
                    }
                }
            }
        }

        private void AcquireShallowDirAndFiles(string dir)
        {
            AcquirePath(dir, AclAccessMode.Read, inheritSubtree: false);
            foreach (string file in Directory.EnumerateFiles(dir))
            {
                AcquirePathCore(file, AclAccessMode.Read, inheritSubtree: false);
            }
        }

        private void AcquirePath(string path, AclAccessMode mode, bool inheritSubtree)
        {
            string fullPath = NormalizePath(path);
            AcquireAncestors(fullPath);
            AcquirePathCore(fullPath, mode, inheritSubtree);
        }

        private void AcquireAncestors(string fullPath)
        {
            var stack = new Stack<string>();
            string? cursor = Path.GetDirectoryName(fullPath);
            while (!string.IsNullOrEmpty(cursor))
            {
                string normalized = NormalizePath(cursor);
                if (string.IsNullOrEmpty(normalized) || IsUnderStandardSystemDirectory(normalized))
                {
                    break;
                }
                stack.Push(normalized);
                string? next = Path.GetDirectoryName(normalized);
                if (string.Equals(next, cursor, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
                cursor = next;
            }

            while (stack.Count > 0)
            {
                string ancestor = stack.Pop();
                AcquirePathCore(ancestor, AclAccessMode.Traverse, inheritSubtree: false);
                if (_isNodeRuntime)
                {
                    // Node.js C++ getPackageScopeConfig walks upward checking package.json on ancestor
                    // directories; grant temporary read-only access solely to existing package.json
                    // manifests along the ancestor chain so module-type resolution does not EPERM.
                    string pkgJson = Path.Combine(ancestor, "package.json");
                    if (File.Exists(pkgJson))
                    {
                        AcquirePathCore(pkgJson, AclAccessMode.Read, inheritSubtree: false);
                    }
                }
            }
        }

        private static string NormalizePath(string path)
        {
            string full = Path.GetFullPath(path);
            string? root = Path.GetPathRoot(full);
            if (!string.IsNullOrEmpty(root) && string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
            {
                return full;
            }
            return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private void AcquirePathCore(string path, AclAccessMode mode, bool inheritSubtree)
        {
            string fullPath = NormalizePath(path);
            bool isDir = Directory.Exists(fullPath);
            bool isFile = !isDir && File.Exists(fullPath);
            if (!isDir && !isFile)
            {
                return;
            }

            bool effectiveInherit = isDir && inheritSubtree;
            var key = new AclKey(fullPath.ToUpperInvariant(), effectiveInherit);

            // Avoid duplicate acquisition for the same path+inheritance+mode within the same launch scope.
            for (int i = 0; i < _acquired.Count; i++)
            {
                if (_acquired[i].Key == key && _acquired[i].Mode == mode)
                {
                    return;
                }
            }

            AclRefEntry entry;
            lock (s_aclMapLock)
            {
                if (!s_activeAcls.TryGetValue(key, out entry!))
                {
                    entry = new AclRefEntry();
                    s_activeAcls[key] = entry;
                }
                entry.ActiveHolders++;
            }

            bool addedToAcquired = false;
            try
            {
                lock (entry.EntryLock)
                {
                    entry.Increment(mode);
                    AclAccessMode? desiredOpt = entry.GetDesiredMode();
                    if (desiredOpt is AclAccessMode desired &&
                        (entry.AppliedMode is null || desired > entry.AppliedMode.Value))
                    {
                        try
                        {
                            if (isDir)
                            {
                                var di = new DirectoryInfo(fullPath);
                                DirectorySecurity sec = di.GetAccessControl();
                                FileSystemAccessRule rule = BuildDirectoryRule(_sid, desired, effectiveInherit);
                                if (!AlreadyHasExplicitRule(sec, _sid, rule.FileSystemRights, rule.InheritanceFlags))
                                {
                                    sec.AddAccessRule(rule);
                                    if (!effectiveInherit)
                                    {
                                        if (TrySetNonPropagatingDacl(fullPath, sec))
                                        {
                                            entry.AppliedMode = desired;
                                        }
                                    }
                                    else
                                    {
                                        di.SetAccessControl(sec);
                                        entry.AppliedMode = desired;
                                    }
                                }
                            }
                            else
                            {
                                var fi = new FileInfo(fullPath);
                                FileSecurity sec = fi.GetAccessControl();
                                FileSystemAccessRule rule = BuildFileRule(_sid, desired);
                                if (!AlreadyHasExplicitRule(sec, _sid, rule.FileSystemRights, InheritanceFlags.None))
                                {
                                    sec.AddAccessRule(rule);
                                    fi.SetAccessControl(sec);
                                    entry.AppliedMode = desired;
                                }
                            }
                        }
                        catch (UnauthorizedAccessException)
                        {
                            // Ignore protected/read-only system targets that current user cannot modify.
                        }
                    }

                    _acquired.Add((key, entry, isDir, mode));
                    addedToAcquired = true;
                }
            }
            finally
            {
                if (!addedToAcquired)
                {
                    lock (s_aclMapLock)
                    {
                        entry.ActiveHolders--;
                        if (entry.ActiveHolders == 0)
                        {
                            s_activeAcls.Remove(key);
                        }
                    }
                }
            }
        }

        public void Dispose()
        {
            for (int i = _acquired.Count - 1; i >= 0; i--)
            {
                (AclKey key, AclRefEntry entry, bool isDir, AclAccessMode mode) = _acquired[i];
                lock (entry.EntryLock)
                {
                    entry.Decrement(mode);
                    AclAccessMode? desiredOpt = entry.GetDesiredMode();
                    AclAccessMode? appliedOpt = entry.AppliedMode;

                    if (appliedOpt is AclAccessMode applied && desiredOpt != applied)
                    {
                        try
                        {
                            if (isDir && Directory.Exists(key.NormalizedPath))
                            {
                                var di = new DirectoryInfo(key.NormalizedPath);
                                DirectorySecurity sec = di.GetAccessControl();
                                FileSystemAccessRule oldRule = BuildDirectoryRule(_sid, applied, key.InheritSubtree);
                                sec.RemoveAccessRuleSpecific(oldRule);

                                if (desiredOpt is AclAccessMode desired)
                                {
                                    FileSystemAccessRule newRule = BuildDirectoryRule(_sid, desired, key.InheritSubtree);
                                    sec.AddAccessRule(newRule);
                                }

                                if (!key.InheritSubtree)
                                {
                                    _ = TrySetNonPropagatingDacl(key.NormalizedPath, sec);
                                }
                                else
                                {
                                    di.SetAccessControl(sec);
                                }
                            }
                            else if (!isDir && File.Exists(key.NormalizedPath))
                            {
                                var fi = new FileInfo(key.NormalizedPath);
                                FileSecurity sec = fi.GetAccessControl();
                                FileSystemAccessRule oldRule = BuildFileRule(_sid, applied);
                                sec.RemoveAccessRuleSpecific(oldRule);

                                if (desiredOpt is AclAccessMode desired)
                                {
                                    FileSystemAccessRule newRule = BuildFileRule(_sid, desired);
                                    sec.AddAccessRule(newRule);
                                }

                                fi.SetAccessControl(sec);
                            }
                        }
                        catch (UnauthorizedAccessException)
                        {
                        }
                        catch (DirectoryNotFoundException)
                        {
                        }
                        catch (FileNotFoundException)
                        {
                        }
                        finally
                        {
                            entry.AppliedMode = desiredOpt;
                        }
                    }
                }

                lock (s_aclMapLock)
                {
                    entry.ActiveHolders--;
                    if (entry.ActiveHolders == 0)
                    {
                        s_activeAcls.Remove(key);
                    }
                }
            }
            _acquired.Clear();
        }

        private static FileSystemAccessRule BuildDirectoryRule(SecurityIdentifier sid, AclAccessMode mode, bool inheritSubtree)
        {
            FileSystemRights rights = mode switch
            {
                AclAccessMode.Write => FileSystemRights.Modify | FileSystemRights.Synchronize,
                AclAccessMode.Traverse => FileSystemRights.Traverse | FileSystemRights.ReadAttributes | FileSystemRights.Synchronize,
                _ => FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize
            };
            InheritanceFlags inheritance = inheritSubtree
                ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit
                : InheritanceFlags.None;
            return new FileSystemAccessRule(sid, rights, inheritance, PropagationFlags.None, AccessControlType.Allow);
        }

        private static FileSystemAccessRule BuildFileRule(SecurityIdentifier sid, AclAccessMode mode)
        {
            FileSystemRights rights = mode == AclAccessMode.Write
                ? FileSystemRights.Modify | FileSystemRights.Synchronize
                : FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize;
            return new FileSystemAccessRule(sid, rights, InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow);
        }

        private static unsafe bool TrySetNonPropagatingDacl(string directoryPath, DirectorySecurity sec)
        {
            nint hDir = WindowsNativeMethods.CreateFileW(
                directoryPath,
                WindowsNativeMethods.WRITE_DAC,
                WindowsNativeMethods.FILE_SHARE_READ | WindowsNativeMethods.FILE_SHARE_WRITE | WindowsNativeMethods.FILE_SHARE_DELETE,
                0,
                WindowsNativeMethods.OPEN_EXISTING,
                WindowsNativeMethods.FILE_FLAG_BACKUP_SEMANTICS,
                0);
            if (hDir == 0 || hDir == -1)
            {
                return false;
            }

            try
            {
                byte[] sdBytes = sec.GetSecurityDescriptorBinaryForm();
                fixed (byte* pSd = sdBytes)
                {
                    return WindowsNativeMethods.SetKernelObjectSecurity(
                        hDir,
                        WindowsNativeMethods.DACL_SECURITY_INFORMATION,
                        pSd);
                }
            }
            finally
            {
                _ = WindowsNativeMethods.CloseHandle(hDir);
            }
        }

        private static bool AlreadyHasExplicitRule(
            FileSystemSecurity sec,
            SecurityIdentifier sid,
            FileSystemRights requiredRights,
            InheritanceFlags requiredInheritance)
        {
            AuthorizationRuleCollection rules = sec.GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier));
            foreach (AuthorizationRule rawRule in rules)
            {
                if (rawRule is FileSystemAccessRule fsRule &&
                    fsRule.AccessControlType == AccessControlType.Allow &&
                    fsRule.IdentityReference.Equals(sid) &&
                    (fsRule.FileSystemRights & requiredRights) == requiredRights &&
                    (fsRule.InheritanceFlags & requiredInheritance) == requiredInheritance)
                {
                    return true;
                }
            }
            return false;
        }

        private static bool IsUnderStandardSystemDirectory(string path)
        {
            string full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            for (int i = 0; i < s_standardSystemPrefixes.Length; i++)
            {
                string prefix = s_standardSystemPrefixes[i];
                if (string.IsNullOrEmpty(prefix)) continue;
                if (full.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
                    (full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                     full.Length > prefix.Length &&
                     full[prefix.Length] == Path.DirectorySeparatorChar))
                {
                    return true;
                }
            }
            return false;
        }

        private static string[] BuildStandardSystemPrefixes()
        {
            var list = new List<string>(4);
            void AddFolder(Environment.SpecialFolder folder)
            {
                string p = Environment.GetFolderPath(folder);
                if (!string.IsNullOrEmpty(p))
                {
                    list.Add(Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                }
            }

            AddFolder(Environment.SpecialFolder.Windows);
            AddFolder(Environment.SpecialFolder.ProgramFiles);
            AddFolder(Environment.SpecialFolder.ProgramFilesX86);
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrEmpty(localAppData))
            {
                list.Add(Path.Combine(Path.GetFullPath(localAppData), "Microsoft", "WindowsApps"));
            }
            return list.ToArray();
        }
    }

    private sealed class LaunchState : IDisposable
    {
        public nint JobHandle;
        public nint ProcessHandle;
        public SafeFileHandle? SafeROut;
        public SafeFileHandle? SafeRErr;
        public SafeFileHandle? SafeWIn;
        public AppContainerAclScope? AclScope;
        public TimeSpan StartupLatency;
        public string IsolationMechanism = "WindowsJobObject+RestrictedToken";

        public void Dispose()
        {
            SafeWIn?.Dispose();
            SafeROut?.Dispose();
            SafeRErr?.Dispose();
            CloseIfValid(ref ProcessHandle);
            CloseIfValid(ref JobHandle);
            AclScope?.Dispose();
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
