namespace Glacier.Agent.Sandbox;

using System.Runtime.InteropServices;
using System.Runtime.Versioning;

[SupportedOSPlatform("windows")]
internal static unsafe partial class WindowsNativeMethods
{
    internal const uint TOKEN_ASSIGN_PRIMARY = 0x0001u;
    internal const uint TOKEN_DUPLICATE = 0x0002u;
    internal const uint TOKEN_QUERY = 0x0008u;
    internal const uint DISABLE_MAX_PRIVILEGE = 0x00000001u;

    internal const int JobObjectBasicUIRestrictions = 4;
    internal const int JobObjectExtendedLimitInformation = 9;

    internal const uint JOB_OBJECT_LIMIT_ACTIVE_PROCESS = 0x00000008u;
    internal const uint JOB_OBJECT_LIMIT_PROCESS_MEMORY = 0x00000100u;
    internal const uint JOB_OBJECT_LIMIT_JOB_MEMORY = 0x00000200u;
    internal const uint JOB_OBJECT_LIMIT_DIE_ON_UNHANDLED_EXCEPTION = 0x00000400u;
    internal const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000u;

    internal const uint JOB_OBJECT_UILIMIT_ALL = 0x000000FFu;

    internal const uint HANDLE_FLAG_INHERIT = 0x00000001u;
    internal const uint STARTF_USESTDHANDLES = 0x00000100u;

    internal const nuint PROC_THREAD_ATTRIBUTE_HANDLE_LIST = 0x00020002u;
    internal const nuint PROC_THREAD_ATTRIBUTE_SECURITY_CAPABILITY = 0x00020009u;
    internal const nuint PROC_THREAD_ATTRIBUTE_JOB_LIST = 0x0002000Du;

    internal const uint SDDL_REVISION_1 = 1u;
    internal const uint WINSTA_ALL_ACCESS_FULL = 0x000F037Fu;
    internal const uint DESKTOP_ALL_ACCESS_FULL = 0x000F01FFu;
    internal const int UOI_NAME = 2;

    internal const uint CREATE_SUSPENDED = 0x00000004u;
    internal const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400u;
    internal const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000u;
    internal const uint CREATE_NO_WINDOW = 0x08000000u;

    internal const uint WRITE_DAC = 0x00040000u;
    internal const uint DACL_SECURITY_INFORMATION = 0x00000004u;
    internal const uint FILE_SHARE_READ = 0x00000001u;
    internal const uint FILE_SHARE_WRITE = 0x00000002u;
    internal const uint FILE_SHARE_DELETE = 0x00000004u;
    internal const uint OPEN_EXISTING = 3u;
    internal const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000u;

    [StructLayout(LayoutKind.Sequential)]
    internal struct SECURITY_CAPABILITIES
    {
        public nint AppContainerSid;
        public nint Capabilities;
        public uint CapabilityCount;
        public uint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct JOBOBJECT_BASIC_UI_RESTRICTIONS
    {
        public uint UIRestrictionsClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SECURITY_ATTRIBUTES
    {
        public uint nLength;
        public nint lpSecurityDescriptor;
        public int bInheritHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct STARTUPINFOW
    {
        public uint cb;
        public nint lpReserved;
        public nint lpDesktop;
        public nint lpTitle;
        public uint dwX;
        public uint dwY;
        public uint dwXSize;
        public uint dwYSize;
        public uint dwXCountChars;
        public uint dwYCountChars;
        public uint dwFillAttribute;
        public uint dwFlags;
        public ushort wShowWindow;
        public ushort cbReserved2;
        public nint lpReserved2;
        public nint hStdInput;
        public nint hStdOutput;
        public nint hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct STARTUPINFOEXW
    {
        public STARTUPINFOW StartupInfo;
        public nint lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PROCESS_INFORMATION
    {
        public nint hProcess;
        public nint hThread;
        public uint dwProcessId;
        public uint dwThreadId;
    }

    [LibraryImport("kernel32.dll")]
    internal static partial nint GetCurrentProcess();

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool OpenProcessToken(nint processHandle, uint desiredAccess, out nint tokenHandle);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CreateRestrictedToken(
        nint existingTokenHandle,
        uint flags,
        uint disableSidCount,
        nint sidsToDisable,
        uint deletePrivilegeCount,
        nint privilegesToDelete,
        uint restrictedSidCount,
        nint sidsToRestrict,
        out nint newTokenHandle);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint CreateJobObjectW(nint lpJobAttributes, string? lpName);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetInformationJobObject(
        nint hJob,
        int jobObjectInformationClass,
        void* lpJobObjectInformation,
        uint cbJobObjectInformationLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool TerminateJobObject(nint hJob, uint uExitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CreatePipe(
        out nint hReadPipe,
        out nint hWritePipe,
        SECURITY_ATTRIBUTES* lpPipeAttributes,
        uint nSize);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetHandleInformation(nint hObject, uint dwMask, uint dwFlags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool InitializeProcThreadAttributeList(
        nint lpAttributeList,
        uint dwAttributeCount,
        uint dwFlags,
        ref nuint lpSize);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UpdateProcThreadAttribute(
        nint lpAttributeList,
        uint dwFlags,
        nuint attribute,
        void* lpValue,
        nuint cbSize,
        nint lpPreviousValue,
        nint lpReturnSize);

    [LibraryImport("kernel32.dll")]
    internal static partial void DeleteProcThreadAttributeList(nint lpAttributeList);

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CreateProcessAsUserW(
        nint hToken,
        string? lpApplicationName,
        char* lpCommandLine,
        nint lpProcessAttributes,
        nint lpThreadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles,
        uint dwCreationFlags,
        void* lpEnvironment,
        string? lpCurrentDirectory,
        STARTUPINFOEXW* lpStartupInfo,
        PROCESS_INFORMATION* lpProcessInformation);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CreateProcessW(
        string? lpApplicationName,
        char* lpCommandLine,
        nint lpProcessAttributes,
        nint lpThreadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles,
        uint dwCreationFlags,
        void* lpEnvironment,
        string? lpCurrentDirectory,
        STARTUPINFOEXW* lpStartupInfo,
        PROCESS_INFORMATION* lpProcessInformation);

    [LibraryImport("userenv.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int CreateAppContainerProfile(
        string pszAppContainerName,
        string pszDisplayName,
        string pszDescription,
        nint pCapabilities,
        uint dwCapabilityCount,
        out nint ppSidAppContainerSid);

    [LibraryImport("userenv.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int DeriveAppContainerSidFromAppContainerName(
        string pszAppContainerName,
        out nint ppsidAppContainerSid);

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ConvertStringSecurityDescriptorToSecurityDescriptorW(
        string stringSecurityDescriptor,
        uint stringSDRevision,
        out nint securityDescriptor,
        nint securityDescriptorSize);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ConvertSidToStringSidW(nint sid, out nint stringSid);

    [LibraryImport("advapi32.dll")]
    internal static partial nint FreeSid(nint pSid);

    [LibraryImport("advapi32.dll")]
    internal static partial uint GetLengthSid(nint pSid);

    [LibraryImport("kernel32.dll")]
    internal static partial nint LocalFree(nint hMem);

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial nint GetProcessWindowStation();

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetProcessWindowStation(nint hWinSta);

    [LibraryImport("user32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint CreateWindowStationW(
        string? lpwinsta,
        uint dwFlags,
        uint dwDesiredAccess,
        SECURITY_ATTRIBUTES* lpsa);

    [LibraryImport("user32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint CreateDesktopW(
        string lpszDesktop,
        string? lpszDevice,
        nint pDevmode,
        uint dwFlags,
        uint dwDesiredAccess,
        SECURITY_ATTRIBUTES* lpsa);

    [LibraryImport("user32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetUserObjectInformationW(
        nint hObj,
        int nIndex,
        void* pvInfo,
        uint nLength,
        out uint lpnLengthNeeded);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial uint ResumeThread(nint hThread);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetExitCodeProcess(nint hProcess, out uint lpExitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(nint hObject);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        nint lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        nint hTemplateFile);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetKernelObjectSecurity(
        nint handle,
        uint securityInformation,
        byte* pSecurityDescriptor);
}
