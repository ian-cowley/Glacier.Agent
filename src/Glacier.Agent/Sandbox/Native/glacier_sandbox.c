#define _GNU_SOURCE
#include <errno.h>
#include <linux/sched.h>
#include <poll.h>
#include <signal.h>
#include <stddef.h>
#include <stdint.h>
#include <sys/mount.h>
#include <sys/prctl.h>
#include <sys/resource.h>
#include <sys/syscall.h>
#include <unistd.h>

#ifndef CLONE_PIDFD
#define CLONE_PIDFD 0x00001000UL
#endif
#ifndef CLONE_NEWNS
#define CLONE_NEWNS 0x00020000UL
#endif
#ifndef CLONE_NEWUTS
#define CLONE_NEWUTS 0x04000000UL
#endif
#ifndef CLONE_NEWIPC
#define CLONE_NEWIPC 0x08000000UL
#endif
#ifndef CLONE_NEWUSER
#define CLONE_NEWUSER 0x10000000UL
#endif
#ifndef CLONE_NEWPID
#define CLONE_NEWPID 0x20000000UL
#endif
#ifndef CLONE_NEWNET
#define CLONE_NEWNET 0x40000000UL
#endif

#ifndef PR_SET_NO_NEW_PRIVS
#define PR_SET_NO_NEW_PRIVS 38
#endif

#ifndef __NR_clone3
#define __NR_clone3 435
#endif

#define GLACIER_MODE_CLONE3_NS 1
#define GLACIER_MODE_CLONE3_USERNS_DENIED 2
#define GLACIER_MODE_CLONE3_NO_NS 3
#define GLACIER_MODE_FORK_UNSHARE_NS 4
#define GLACIER_MODE_FORK_NO_NS 5

typedef struct glacier_clone_args {
    uint64_t flags;
    uint64_t pidfd;
    uint64_t child_tid;
    uint64_t parent_tid;
    uint64_t exit_signal;
    uint64_t stack;
    uint64_t stack_size;
    uint64_t tls;
    uint64_t set_tid;
    uint64_t set_tid_size;
    uint64_t cgroup;
} glacier_clone_args_t;

typedef struct glacier_sandbox_spawn_args {
    const char* exe_path;
    char* const* argv;
    char* const* envp;
    const char* work_dir;
    int64_t memory_limit_bytes;
    int32_t enable_namespace_isolation; /* 0 = disabled, 1 = strict fail-closed, 2 = explicit unisolated fallback on EPERM/EACCES */
    int32_t allow_network;
    int32_t has_memory_limit;
    int32_t has_max_processes;
    int32_t max_processes;
    int32_t sync_read_fd;
    int32_t sync_write_fd;
    int32_t err_read_fd;
    int32_t err_write_fd;
    int32_t in_read_fd;
    int32_t in_write_fd;
    int32_t out_read_fd;
    int32_t out_write_fd;
    int32_t err_out_read_fd;
    int32_t err_out_write_fd;
    int32_t cancel_read_fd;
    int32_t cancel_write_fd;
    int32_t timeout_ms;
} glacier_sandbox_spawn_args_t;

typedef struct glacier_sandbox_spawn_result {
    int32_t child_pid;
    int32_t pidfd;
    int32_t mode;
    int32_t user_ns_active;
    int32_t err_no;
    int32_t timed_out_or_canceled;
} glacier_sandbox_spawn_result_t;

_Static_assert(sizeof(glacier_clone_args_t) == 88, "glacier_clone_args_t ABI size mismatch");
_Static_assert(sizeof(glacier_sandbox_spawn_args_t) == 112, "glacier_sandbox_spawn_args_t ABI size mismatch");
_Static_assert(sizeof(glacier_sandbox_spawn_result_t) == 24, "glacier_sandbox_spawn_result_t ABI size mismatch");

static inline void raw_close_if_valid(int fd) {
    if (fd >= 0) {
        (void)syscall(SYS_close, fd);
    }
}

static inline int raw_dup2(int oldfd, int newfd) {
#ifdef SYS_dup2
    return (int)syscall(SYS_dup2, oldfd, newfd);
#else
    if (oldfd == newfd) {
        return newfd;
    }
    return (int)syscall(SYS_dup3, oldfd, newfd, 0);
#endif
}

__attribute__((noreturn))
static void raw_exit_127(void) {
    (void)syscall(SYS_exit_group, 127);
    (void)syscall(SYS_exit, 127);
    for (;;) {
    }
}

__attribute__((noreturn))
static void write_child_errno_and_exit(int err_write_fd, int err_num) {
    if (err_write_fd >= 0) {
        int32_t e = (int32_t)err_num;
        const uint8_t* p = (const uint8_t*)&e;
        size_t rem = sizeof(e);
        while (rem > 0) {
            long w = syscall(SYS_write, err_write_fd, p, rem);
            if (w < 0) {
                if (errno == EINTR) {
                    continue;
                }
                break;
            }
            if (w == 0) {
                break;
            }
            p += w;
            rem -= (size_t)w;
        }
        (void)syscall(SYS_close, err_write_fd);
    }
    raw_exit_127();
}

__attribute__((noinline, noreturn))
static void child_run(
    const glacier_sandbox_spawn_args_t* args,
    int use_unshare_fallback,
    uint64_t requested_unshare_flags,
    int unshare_read_fd,
    int unshare_write_fd,
    int has_mount_ns) {

    raw_close_if_valid(unshare_read_fd);
    raw_close_if_valid(args->sync_write_fd);
    raw_close_if_valid(args->err_read_fd);
    raw_close_if_valid(args->out_read_fd);
    raw_close_if_valid(args->err_out_read_fd);
    raw_close_if_valid(args->in_write_fd);
    raw_close_if_valid(args->cancel_read_fd);
    raw_close_if_valid(args->cancel_write_fd);

    if (use_unshare_fallback) {
        uint8_t unshared = 0;
        if (requested_unshare_flags != 0 &&
            syscall(SYS_unshare, (unsigned long)requested_unshare_flags) == 0) {
            unshared = 1;
            has_mount_ns = 1;
        }
        if (unshare_write_fd >= 0) {
            for (;;) {
                long w = syscall(SYS_write, unshare_write_fd, &unshared, 1);
                if (w < 0 && errno == EINTR) {
                    continue;
                }
                break;
            }
            (void)syscall(SYS_close, unshare_write_fd);
        }
    }

    /* Wait for parent uid_map/gid_map setup before mount/execve. */
    uint8_t go = 0;
    for (;;) {
        long r = syscall(SYS_read, args->sync_read_fd, &go, 1);
        if (r > 0) {
            break;
        }
        if (r == 0) {
            /* Parent aborted or closed sync_write_fd without signaling go. */
            raw_close_if_valid(args->sync_read_fd);
            raw_exit_127();
        }
        if (errno != EINTR) {
            int err_num = errno;
            raw_close_if_valid(args->sync_read_fd);
            write_child_errno_and_exit(args->err_write_fd, err_num);
        }
    }
    raw_close_if_valid(args->sync_read_fd);

    (void)syscall(SYS_setpgid, 0, 0);

    if (has_mount_ns) {
        (void)syscall(SYS_mount, NULL, "/", NULL, (unsigned long)(MS_REC | MS_PRIVATE), NULL);
        (void)syscall(
            SYS_mount,
            "proc",
            "/proc",
            "proc",
            (unsigned long)(MS_NOSUID | MS_NOEXEC | MS_NODEV),
            NULL);
    }

    if (args->in_read_fd >= 0 && args->in_read_fd != 0) {
        if (raw_dup2(args->in_read_fd, 0) < 0) {
            write_child_errno_and_exit(args->err_write_fd, errno);
        }
        (void)syscall(SYS_close, args->in_read_fd);
    }

    if (args->out_write_fd != 1) {
        if (raw_dup2(args->out_write_fd, 1) < 0) {
            write_child_errno_and_exit(args->err_write_fd, errno);
        }
        (void)syscall(SYS_close, args->out_write_fd);
    }

    if (args->err_out_write_fd != 2) {
        if (raw_dup2(args->err_out_write_fd, 2) < 0) {
            write_child_errno_and_exit(args->err_write_fd, errno);
        }
        (void)syscall(SYS_close, args->err_out_write_fd);
    }

    if (args->work_dir != NULL) {
        if (syscall(SYS_chdir, args->work_dir) != 0) {
            write_child_errno_and_exit(args->err_write_fd, errno);
        }
    }

    (void)syscall(SYS_prctl, PR_SET_NO_NEW_PRIVS, 1UL, 0UL, 0UL, 0UL);

    struct rlimit core_zero;
    core_zero.rlim_cur = 0;
    core_zero.rlim_max = 0;
    (void)syscall(SYS_setrlimit, RLIMIT_CORE, &core_zero);

    if (args->has_memory_limit) {
        uint64_t mem = (uint64_t)args->memory_limit_bytes;
        struct rlimit mem_limit;
        mem_limit.rlim_cur = mem;
        mem_limit.rlim_max = mem;
        (void)syscall(SYS_setrlimit, RLIMIT_DATA, &mem_limit);
    }

    if (args->has_max_processes) {
        uint64_t procs = (uint64_t)args->max_processes;
        struct rlimit proc_limit;
        proc_limit.rlim_cur = procs;
        proc_limit.rlim_max = procs;
        (void)syscall(SYS_setrlimit, RLIMIT_NPROC, &proc_limit);
    }

    (void)syscall(SYS_execve, args->exe_path, args->argv, args->envp);
    write_child_errno_and_exit(args->err_write_fd, errno);
}

static long raw_clone_sigchld(void) {
    /* Raw SYS_clone(SIGCHLD, 0, ...) avoids glibc pthread_atfork locks. */
    long pid = syscall(SYS_clone, (unsigned long)SIGCHLD, 0UL, 0UL, 0UL, 0UL);
    if (pid < 0 && errno == ENOSYS) {
#ifdef SYS_fork
        pid = syscall(SYS_fork);
#endif
    }
    return pid;
}

static int create_cloexec_pipe(int fds[2]) {
#ifdef SYS_pipe2
    return (int)syscall(SYS_pipe2, fds, 0x80000 /* O_CLOEXEC */);
#else
    return pipe2(fds, 0x80000);
#endif
}

static int spawn_via_fork_fallback(
    const glacier_sandbox_spawn_args_t* args,
    int use_unshare_fallback,
    uint64_t unshare_flags,
    glacier_sandbox_spawn_result_t* out) {

    int unshare_fds[2] = { -1, -1 };
    if (use_unshare_fallback) {
        if (create_cloexec_pipe(unshare_fds) != 0) {
            out->err_no = errno;
            out->mode = GLACIER_MODE_FORK_NO_NS;
            return -1;
        }
    }

    long pid = raw_clone_sigchld();
    if (pid < 0) {
        int saved_err = errno;
        raw_close_if_valid(unshare_fds[0]);
        raw_close_if_valid(unshare_fds[1]);
        out->err_no = saved_err;
        out->mode = GLACIER_MODE_FORK_NO_NS;
        return -1;
    }

    if (pid == 0) {
        child_run(
            args,
            use_unshare_fallback,
            unshare_flags,
            unshare_fds[0],
            unshare_fds[1],
            0);
    }

    raw_close_if_valid(unshare_fds[1]);

    if (use_unshare_fallback && unshare_fds[0] >= 0) {
        uint8_t status = 0;
        long total = 0;
        for (;;) {
            struct pollfd pfds[2];
            nfds_t nfds = 1;
            pfds[0].fd = unshare_fds[0];
            pfds[0].events = POLLIN;
            pfds[0].revents = 0;
            if (args->cancel_read_fd >= 0) {
                pfds[1].fd = args->cancel_read_fd;
                pfds[1].events = POLLIN;
                pfds[1].revents = 0;
                nfds = 2;
            }

            int poll_res = poll(pfds, nfds, args->timeout_ms);
            if (poll_res < 0) {
                if (errno == EINTR) {
                    continue;
                }
                int saved_err = errno;
                raw_close_if_valid(unshare_fds[0]);
                out->child_pid = (int32_t)pid;
                out->pidfd = -1;
                out->err_no = saved_err;
                out->mode = GLACIER_MODE_FORK_NO_NS;
                return -1;
            }

            if (poll_res == 0) {
                raw_close_if_valid(unshare_fds[0]);
                out->child_pid = (int32_t)pid;
                out->pidfd = -1;
                out->mode = GLACIER_MODE_FORK_NO_NS;
                out->timed_out_or_canceled = 1;
                return -2;
            }

            if (nfds == 2 && (pfds[1].revents & (POLLIN | POLLHUP | POLLERR)) != 0) {
                raw_close_if_valid(unshare_fds[0]);
                out->child_pid = (int32_t)pid;
                out->pidfd = -1;
                out->mode = GLACIER_MODE_FORK_NO_NS;
                out->timed_out_or_canceled = 2;
                return -2;
            }

            if ((pfds[0].revents & (POLLIN | POLLHUP | POLLERR)) != 0) {
                long r = syscall(SYS_read, unshare_fds[0], &status, 1);
                if (r < 0 && errno == EINTR) {
                    continue;
                }
                if (r > 0) {
                    total = r;
                }
                break;
            }
        }
        raw_close_if_valid(unshare_fds[0]);

        int user_ns_active = (total == 1 && status == 1) ? 1 : 0;
        out->child_pid = (int32_t)pid;
        out->pidfd = -1;
        out->user_ns_active = user_ns_active;
        out->mode = user_ns_active ? GLACIER_MODE_FORK_UNSHARE_NS : GLACIER_MODE_FORK_NO_NS;
        return 0;
    }

    out->child_pid = (int32_t)pid;
    out->pidfd = -1;
    out->user_ns_active = 0;
    out->mode = GLACIER_MODE_FORK_NO_NS;
    return 0;
}

int32_t glacier_sandbox_spawn(
    const glacier_sandbox_spawn_args_t* args,
    glacier_sandbox_spawn_result_t* out) {

    if (args == NULL || out == NULL) {
        if (out != NULL) {
            out->err_no = EINVAL;
        }
        return -1;
    }

    out->child_pid = -1;
    out->pidfd = -1;
    out->mode = 0;
    out->user_ns_active = 0;
    out->err_no = 0;
    out->timed_out_or_canceled = 0;

    if (args->enable_namespace_isolation) {
        uint64_t ns_flags = CLONE_NEWUSER
            | CLONE_NEWPID
            | CLONE_NEWNS
            | CLONE_NEWIPC
            | CLONE_NEWUTS
            | CLONE_PIDFD;
        if (!args->allow_network) {
            ns_flags |= CLONE_NEWNET;
        }

        int32_t pidfd = -1;
        glacier_clone_args_t clone_args;
        __builtin_memset(&clone_args, 0, sizeof(clone_args));
        clone_args.flags = ns_flags;
        clone_args.pidfd = (uint64_t)(uintptr_t)&pidfd;
        clone_args.exit_signal = (uint64_t)SIGCHLD;

        long res = syscall(__NR_clone3, &clone_args, (size_t)sizeof(clone_args));
        if (res == 0) {
            child_run(args, 0, 0, -1, -1, 1);
        }
        if (res > 0) {
            out->child_pid = (int32_t)res;
            out->pidfd = pidfd;
            out->user_ns_active = 1;
            out->mode = GLACIER_MODE_CLONE3_NS;
            return 0;
        }

        int clone_err = errno;
        if (clone_err == EPERM || clone_err == EACCES) {
            if (args->enable_namespace_isolation != 2) {
                out->err_no = clone_err;
                out->mode = GLACIER_MODE_CLONE3_USERNS_DENIED;
                return -1;
            }

            pidfd = -1;
            glacier_clone_args_t plain_args;
            __builtin_memset(&plain_args, 0, sizeof(plain_args));
            plain_args.flags = CLONE_PIDFD;
            plain_args.pidfd = (uint64_t)(uintptr_t)&pidfd;
            plain_args.exit_signal = (uint64_t)SIGCHLD;

            long plain_res = syscall(__NR_clone3, &plain_args, (size_t)sizeof(plain_args));
            if (plain_res == 0) {
                child_run(args, 0, 0, -1, -1, 0);
            }
            if (plain_res > 0) {
                out->child_pid = (int32_t)plain_res;
                out->pidfd = pidfd;
                out->user_ns_active = 0;
                out->mode = GLACIER_MODE_CLONE3_USERNS_DENIED;
                return 0;
            }

            int plain_err = errno;
            if (plain_err == ENOSYS || plain_err == EPERM || plain_err == EACCES) {
                return spawn_via_fork_fallback(
                    args,
                    1,
                    ns_flags & ~CLONE_PIDFD,
                    out);
            }

            out->err_no = plain_err;
            out->mode = GLACIER_MODE_CLONE3_USERNS_DENIED;
            return -1;
        }
        else if (clone_err == ENOSYS) {
            return spawn_via_fork_fallback(
                args,
                1,
                ns_flags & ~CLONE_PIDFD,
                out);
        }
        else {
            out->err_no = clone_err;
            out->mode = GLACIER_MODE_CLONE3_NS;
            return -1;
        }
    }
    else {
        int32_t pidfd = -1;
        glacier_clone_args_t plain_args;
        __builtin_memset(&plain_args, 0, sizeof(plain_args));
        plain_args.flags = CLONE_PIDFD;
        plain_args.pidfd = (uint64_t)(uintptr_t)&pidfd;
        plain_args.exit_signal = (uint64_t)SIGCHLD;

        long res = syscall(__NR_clone3, &plain_args, (size_t)sizeof(plain_args));
        if (res == 0) {
            child_run(args, 0, 0, -1, -1, 0);
        }
        if (res > 0) {
            out->child_pid = (int32_t)res;
            out->pidfd = pidfd;
            out->user_ns_active = 0;
            out->mode = GLACIER_MODE_CLONE3_NO_NS;
            return 0;
        }

        int clone_err = errno;
        if (clone_err == ENOSYS || clone_err == EPERM || clone_err == EACCES) {
            return spawn_via_fork_fallback(args, 0, 0, out);
        }

        out->err_no = clone_err;
        out->mode = GLACIER_MODE_CLONE3_NO_NS;
        return -1;
    }
}
