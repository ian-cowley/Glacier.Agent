namespace Glacier.Agent.Sandbox;

using System.Runtime.InteropServices;
using System.Runtime.Versioning;

[SupportedOSPlatform("linux")]
internal static unsafe partial class LinuxNativeMethods
{
    internal const nint __NR_pidfd_send_signal = 424;
    internal const nint __NR_pidfd_open = 434;
    internal const nint __NR_clone3 = 435;

    internal const ulong CLONE_PIDFD = 0x00001000UL;
    internal const ulong CLONE_NEWNS = 0x00020000UL;
    internal const ulong CLONE_NEWUTS = 0x04000000UL;
    internal const ulong CLONE_NEWIPC = 0x08000000UL;
    internal const ulong CLONE_NEWUSER = 0x10000000UL;
    internal const ulong CLONE_NEWPID = 0x20000000UL;
    internal const ulong CLONE_NEWNET = 0x40000000UL;

    internal const int SIGKILL = 9;
    internal const int SIGTERM = 15;
    internal const int SIGCHLD = 17;

    internal const int WNOHANG = 1;

    internal const int PR_SET_NO_NEW_PRIVS = 38;

    internal const int RLIMIT_DATA = 2;
    internal const int RLIMIT_CORE = 4;
    internal const int RLIMIT_NPROC = 6;

    internal const int O_RDONLY = 0;
    internal const int O_WRONLY = 1;
    internal const int O_NONBLOCK = 0x800;
    internal const int O_CLOEXEC = 0x80000;

    internal const int F_GETFL = 3;
    internal const int F_SETFL = 4;

    internal const int X_OK = 1;

    internal const nuint MS_NOSUID = 2;
    internal const nuint MS_NODEV = 4;
    internal const nuint MS_NOEXEC = 8;
    internal const nuint MS_REC = 16384;
    internal const nuint MS_PRIVATE = 0x40000;

    internal const short POLLIN = 0x001;
    internal const short POLLERR = 0x008;
    internal const short POLLHUP = 0x010;
    internal const short POLLNVAL = 0x020;

    internal const int EPERM = 1;
    internal const int ENOENT = 2;
    internal const int EINTR = 4;
    internal const int EAGAIN = 11;
    internal const int EACCES = 13;
    internal const int EPIPE = 32;
    internal const int ENOSYS = 38;

    internal const int GLACIER_MODE_CLONE3_NS = 1;
    internal const int GLACIER_MODE_CLONE3_USERNS_DENIED = 2;
    internal const int GLACIER_MODE_CLONE3_NO_NS = 3;
    internal const int GLACIER_MODE_FORK_UNSHARE_NS = 4;
    internal const int GLACIER_MODE_FORK_NO_NS = 5;

    [StructLayout(LayoutKind.Sequential)]
    internal struct GlacierSandboxSpawnArgs
    {
        public byte* ExePath;
        public byte** Argv;
        public byte** Envp;
        public byte* WorkDir;
        public long MemoryLimitBytes;
        public int EnableNamespaceIsolation;
        public int AllowNetwork;
        public int HasMemoryLimit;
        public int HasMaxProcesses;
        public int MaxProcesses;
        public int SyncReadFd;
        public int SyncWriteFd;
        public int ErrReadFd;
        public int ErrWriteFd;
        public int InReadFd;
        public int InWriteFd;
        public int OutReadFd;
        public int OutWriteFd;
        public int ErrOutReadFd;
        public int ErrOutWriteFd;
        public int CancelReadFd;
        public int CancelWriteFd;
        public int TimeoutMs;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct GlacierSandboxSpawnResult
    {
        public int ChildPid;
        public int Pidfd;
        public int Mode;
        public int UserNsActive;
        public int ErrNo;
        public int TimedOutOrCanceled;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct CloneArgs
    {
        public ulong Flags;
        public ulong Pidfd;
        public ulong ChildTid;
        public ulong ParentTid;
        public ulong ExitSignal;
        public ulong Stack;
        public ulong StackSize;
        public ulong Tls;
        public ulong SetTid;
        public ulong SetTidSize;
        public ulong Cgroup;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RLimit
    {
        public ulong RlimCur;
        public ulong RlimMax;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PollFd
    {
        public int Fd;
        public short Events;
        public short Revents;
    }

    [LibraryImport("libc", EntryPoint = "syscall", SetLastError = true)]
    internal static partial nint SyscallClone3(nint number, CloneArgs* args, nuint size);

    [LibraryImport("libc", EntryPoint = "syscall", SetLastError = true)]
    internal static partial int SyscallPidfdOpen(nint number, int pid, uint flags);

    [LibraryImport("libc", EntryPoint = "syscall", SetLastError = true)]
    internal static partial int SyscallPidfdSendSignal(nint number, int pidfd, int sig, nint info, uint flags);

    [LibraryImport("libc", EntryPoint = "fork", SetLastError = true)]
    internal static partial int Fork();

    [LibraryImport("libc", EntryPoint = "unshare", SetLastError = true)]
    internal static partial int Unshare(int flags);

    [LibraryImport("libc", EntryPoint = "execve", SetLastError = true)]
    internal static partial int Execve(byte* pathname, byte** argv, byte** envp);

    [LibraryImport("libc", EntryPoint = "pipe2", SetLastError = true)]
    internal static partial int Pipe2(int* pipefd, int flags);

    [LibraryImport("libc", EntryPoint = "poll", SetLastError = true)]
    internal static partial int Poll(PollFd* fds, nuint nfds, int timeout);

    [LibraryImport("libc", EntryPoint = "waitpid", SetLastError = true)]
    internal static partial int WaitPid(int pid, int* status, int options);

    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    internal static partial int Kill(int pid, int sig);

    [LibraryImport("libc", EntryPoint = "prctl", SetLastError = true)]
    internal static partial int Prctl(int option, nuint arg2, nuint arg3, nuint arg4, nuint arg5);

    [LibraryImport("libc", EntryPoint = "setrlimit", SetLastError = true)]
    internal static partial int SetRLimit(int resource, RLimit* rlim);

    [LibraryImport("libc", EntryPoint = "mount", SetLastError = true)]
    internal static partial int Mount(byte* source, byte* target, byte* filesystemtype, nuint mountflags, void* data);

    [LibraryImport("libc", EntryPoint = "open", SetLastError = true)]
    internal static partial int Open(byte* pathname, int flags, uint mode);

    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    internal static partial int Close(int fd);

    [LibraryImport("libc", EntryPoint = "dup2", SetLastError = true)]
    internal static partial int Dup2(int oldfd, int newfd);

    [LibraryImport("libc", EntryPoint = "chdir", SetLastError = true)]
    internal static partial int Chdir(byte* path);

    [LibraryImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    internal static partial int Fcntl(int fd, int cmd, int arg);

    [LibraryImport("libc", EntryPoint = "read", SetLastError = true)]
    internal static partial nint Read(int fd, void* buf, nuint count);

    [LibraryImport("libc", EntryPoint = "write", SetLastError = true)]
    internal static partial nint Write(int fd, void* buf, nuint count);

    [LibraryImport("libc", EntryPoint = "_exit")]
    internal static partial void Exit(int status);

    [LibraryImport("libc", EntryPoint = "setpgid", SetLastError = true)]
    internal static partial int SetPgid(int pid, int pgid);

    [LibraryImport("libc", EntryPoint = "geteuid")]
    internal static partial uint GetEuid();

    [LibraryImport("libc", EntryPoint = "getegid")]
    internal static partial uint GetEgid();

    [LibraryImport("libc", EntryPoint = "access", SetLastError = true)]
    internal static partial int Access(byte* pathname, int mode);

    [LibraryImport("libc", EntryPoint = "__errno_location")]
    internal static partial int* ErrnoLocation();

    // SuppressGCTransition + SetLastError=false P/Invokes strictly for the non-x64
    // fallback path when libglacier_sandbox.so is unavailable.
    [SuppressGCTransition]
    [LibraryImport("libc", EntryPoint = "syscall", SetLastError = false)]
    internal static partial nint ChildSyscallClone3(nint number, CloneArgs* args, nuint size);

    [SuppressGCTransition]
    [LibraryImport("libc", EntryPoint = "fork", SetLastError = false)]
    internal static partial int ChildFork();

    [SuppressGCTransition]
    [LibraryImport("libc", EntryPoint = "unshare", SetLastError = false)]
    internal static partial int ChildUnshare(int flags);

    [SuppressGCTransition]
    [LibraryImport("libc", EntryPoint = "execve", SetLastError = false)]
    internal static partial int ChildExecve(byte* pathname, byte** argv, byte** envp);

    [SuppressGCTransition]
    [LibraryImport("libc", EntryPoint = "prctl", SetLastError = false)]
    internal static partial int ChildPrctl(int option, nuint arg2, nuint arg3, nuint arg4, nuint arg5);

    [SuppressGCTransition]
    [LibraryImport("libc", EntryPoint = "setrlimit", SetLastError = false)]
    internal static partial int ChildSetRLimit(int resource, RLimit* rlim);

    [SuppressGCTransition]
    [LibraryImport("libc", EntryPoint = "mount", SetLastError = false)]
    internal static partial int ChildMount(byte* source, byte* target, byte* filesystemtype, nuint mountflags, void* data);

    [SuppressGCTransition]
    [LibraryImport("libc", EntryPoint = "close", SetLastError = false)]
    internal static partial int ChildClose(int fd);

    [SuppressGCTransition]
    [LibraryImport("libc", EntryPoint = "dup2", SetLastError = false)]
    internal static partial int ChildDup2(int oldfd, int newfd);

    [SuppressGCTransition]
    [LibraryImport("libc", EntryPoint = "chdir", SetLastError = false)]
    internal static partial int ChildChdir(byte* path);

    [SuppressGCTransition]
    [LibraryImport("libc", EntryPoint = "read", SetLastError = false)]
    internal static partial nint ChildRead(int fd, void* buf, nuint count);

    [SuppressGCTransition]
    [LibraryImport("libc", EntryPoint = "write", SetLastError = false)]
    internal static partial nint ChildWrite(int fd, void* buf, nuint count);

    [SuppressGCTransition]
    [LibraryImport("libc", EntryPoint = "_exit", SetLastError = false)]
    internal static partial void ChildExit(int status);

    [SuppressGCTransition]
    [LibraryImport("libc", EntryPoint = "setpgid", SetLastError = false)]
    internal static partial int ChildSetPgid(int pid, int pgid);

    [SuppressGCTransition]
    [LibraryImport("libc", EntryPoint = "__errno_location", SetLastError = false)]
    internal static partial int* ChildErrnoLocation();
}
