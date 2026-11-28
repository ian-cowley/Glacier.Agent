#define _GNU_SOURCE
#include <errno.h>
#include <fcntl.h>
#include <limits.h>
#include <linux/audit.h>
#include <linux/filter.h>
#include <linux/sched.h>
#include <linux/seccomp.h>
#include <poll.h>
#include <signal.h>
#include <stddef.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#include <sys/mount.h>
#include <sys/prctl.h>
#include <sys/resource.h>
#include <sys/stat.h>
#include <sys/statvfs.h>
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
#ifndef PR_SET_SECCOMP
#define PR_SET_SECCOMP 22
#endif
#ifndef SECCOMP_MODE_FILTER
#define SECCOMP_MODE_FILTER 2
#endif
#ifndef SECCOMP_RET_ALLOW
#define SECCOMP_RET_ALLOW 0x7fff0000U
#endif
#ifndef SECCOMP_RET_ERRNO
#define SECCOMP_RET_ERRNO 0x00050000U
#endif

#ifndef MNT_DETACH
#define MNT_DETACH 2
#endif

#ifndef __NR_clone3
#define __NR_clone3 435
#endif
#ifndef __NR_pivot_root
#define __NR_pivot_root 155
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

static int join_paths(char* out, size_t out_cap, const char* a, const char* b) {
    size_t la = strlen(a);
    size_t lb = strlen(b);
    if (la == 0 || lb == 0) {
        return -1;
    }
    int need_slash = (a[la - 1] != '/' && b[0] != '/') ? 1 : 0;
    size_t skip_b = (a[la - 1] == '/' && b[0] == '/') ? 1 : 0;
    size_t total = la + (size_t)need_slash + (lb - skip_b);
    if (total + 1 > out_cap) {
        return -1;
    }
    memcpy(out, a, la);
    size_t pos = la;
    if (need_slash) {
        out[pos++] = '/';
    }
    memcpy(out + pos, b + skip_b, lb - skip_b);
    out[total] = '\0';
    return 0;
}

static int mkdir_p(const char* path, mode_t mode) {
    char tmp[PATH_MAX];
    size_t len = strlen(path);
    if (len == 0 || len >= sizeof(tmp)) {
        errno = ENAMETOOLONG;
        return -1;
    }
    memcpy(tmp, path, len + 1);
    for (size_t i = 1; i < len; i++) {
        if (tmp[i] == '/') {
            tmp[i] = '\0';
            if (mkdir(tmp, mode) != 0 && errno != EEXIST) {
                return -1;
            }
            tmp[i] = '/';
        }
    }
    if (mkdir(tmp, mode) != 0 && errno != EEXIST) {
        return -1;
    }
    return 0;
}

static int mkdir_p_parent(const char* path, mode_t mode) {
    char tmp[PATH_MAX];
    size_t len = strlen(path);
    if (len == 0 || len >= sizeof(tmp)) {
        errno = ENAMETOOLONG;
        return -1;
    }
    memcpy(tmp, path, len + 1);
    for (size_t i = len; i > 1; i--) {
        if (tmp[i - 1] == '/') {
            tmp[i - 1] = '\0';
            return mkdir_p(tmp, mode);
        }
    }
    return 0;
}

static unsigned long preserve_mount_flags(const char* path) {
    struct statvfs sv;
    if (statvfs(path, &sv) != 0) {
        return 0;
    }
    unsigned long flags = 0;
    if (sv.f_flag & ST_NOSUID) flags |= MS_NOSUID;
    if (sv.f_flag & ST_NODEV) flags |= MS_NODEV;
    if (sv.f_flag & ST_NOEXEC) flags |= MS_NOEXEC;
#ifdef ST_NOATIME
    if (sv.f_flag & ST_NOATIME) flags |= MS_NOATIME;
#endif
#ifdef ST_NODIRATIME
    if (sv.f_flag & ST_NODIRATIME) flags |= MS_NODIRATIME;
#endif
#ifdef ST_RELATIME
    if (sv.f_flag & ST_RELATIME) flags |= MS_RELATIME;
#endif
    return flags;
}

static int bind_mount_entry(const char* stage, const char* src, int read_only, int* err_out) {
    struct stat st;
    if (lstat(src, &st) != 0) {
        if (errno == ENOENT) {
            return 0;
        }
        *err_out = errno;
        return -1;
    }

    char dst[PATH_MAX];
    if (join_paths(dst, sizeof(dst), stage, src) != 0) {
        *err_out = ENAMETOOLONG;
        return -1;
    }

    if (S_ISLNK(st.st_mode)) {
        char target[PATH_MAX];
        ssize_t n = readlink(src, target, sizeof(target) - 1);
        if (n < 0) {
            *err_out = errno;
            return -1;
        }
        target[n] = '\0';
        if (mkdir_p_parent(dst, 0755) != 0) {
            *err_out = errno;
            return -1;
        }
        if (symlink(target, dst) != 0 && errno != EEXIST) {
            *err_out = errno;
            return -1;
        }
        return 0;
    }

    if (!S_ISDIR(st.st_mode)) {
        return 0;
    }

    if (mkdir_p(dst, 0755) != 0) {
        *err_out = errno;
        return -1;
    }

    if (syscall(SYS_mount, src, dst, NULL, (unsigned long)(MS_BIND | MS_REC), NULL) != 0) {
        *err_out = errno;
        return -1;
    }

    if (read_only) {
        unsigned long preserved = preserve_mount_flags(src);
        unsigned long ro_flags = MS_BIND | MS_REMOUNT | MS_RDONLY | preserved;
        if (syscall(SYS_mount, NULL, dst, NULL, ro_flags, NULL) != 0) {
            *err_out = errno;
            return -1;
        }
    }

    return 0;
}

static int bind_mount_dev_node(const char* stage, const char* dev_path, int* err_out) {
    struct stat st;
    if (stat(dev_path, &st) != 0) {
        if (errno == ENOENT) {
            return 0;
        }
        *err_out = errno;
        return -1;
    }

    char dst[PATH_MAX];
    if (join_paths(dst, sizeof(dst), stage, dev_path) != 0) {
        *err_out = ENAMETOOLONG;
        return -1;
    }

    int fd = (int)syscall(SYS_openat, AT_FDCWD, dst, O_CREAT | O_WRONLY | O_CLOEXEC, 0666);
    if (fd < 0) {
        *err_out = errno;
        return -1;
    }
    raw_close_if_valid(fd);

    if (syscall(SYS_mount, dev_path, dst, NULL, (unsigned long)MS_BIND, NULL) != 0) {
        *err_out = errno;
        return -1;
    }

    return 0;
}

static int is_under_system_ro_dir(const char* dir) {
    static const char* const ro_prefixes[] = { "/bin", "/lib", "/lib64", "/usr", "/etc", "/sbin", "/opt" };
    for (size_t i = 0; i < sizeof(ro_prefixes) / sizeof(ro_prefixes[0]); i++) {
        size_t len = strlen(ro_prefixes[i]);
        if (strncmp(dir, ro_prefixes[i], len) == 0 && (dir[len] == '\0' || dir[len] == '/')) {
            return 1;
        }
    }
    return 0;
}

static int is_path_prefix(const char* prefix, const char* path) {
    if (prefix == NULL || path == NULL) {
        return 0;
    }
    size_t plen = strlen(prefix);
    if (plen == 0) {
        return 0;
    }
    return strncmp(path, prefix, plen) == 0 && (path[plen] == '\0' || path[plen] == '/');
}

static int setup_pivoted_rootfs(const glacier_sandbox_spawn_args_t* args, int* err_out) {
    static const char* const stage_candidates[] = { "/mnt", "/media", "/srv" };
    const char* stage = NULL;
    char tmp_stage_buf[] = "/tmp/.glacier_pivot_XXXXXX";
    struct stat st;
    for (size_t i = 0; i < sizeof(stage_candidates) / sizeof(stage_candidates[0]); i++) {
        if (is_path_prefix(stage_candidates[i], args->work_dir) ||
            is_path_prefix(stage_candidates[i], args->exe_path)) {
            continue;
        }
        if (lstat(stage_candidates[i], &st) == 0 && S_ISDIR(st.st_mode)) {
            stage = stage_candidates[i];
            break;
        }
    }
    if (stage == NULL) {
        if (!is_path_prefix("/tmp/.glacier_pivot", args->work_dir) &&
            !is_path_prefix("/tmp/.glacier_pivot", args->exe_path) &&
            mkdir_p("/tmp/.glacier_pivot", 0700) == 0 &&
            lstat("/tmp/.glacier_pivot", &st) == 0 &&
            S_ISDIR(st.st_mode)) {
            stage = "/tmp/.glacier_pivot";
        } else if (mkdtemp(tmp_stage_buf) != NULL) {
            stage = tmp_stage_buf;
        } else {
            *err_out = errno;
            return -1;
        }
    }

    if (syscall(SYS_mount, "tmpfs", stage, "tmpfs", (unsigned long)(MS_NOSUID | MS_NODEV), "size=64m,mode=0755") != 0) {
        *err_out = errno;
        return -1;
    }

    static const char* const ro_dirs[] = { "/bin", "/lib", "/lib64", "/usr", "/etc", "/sbin", "/opt" };
    for (size_t i = 0; i < sizeof(ro_dirs) / sizeof(ro_dirs[0]); i++) {
        if (bind_mount_entry(stage, ro_dirs[i], 1, err_out) != 0) {
            (void)umount2(stage, MNT_DETACH);
            return -1;
        }
    }

    char tmp_dir[PATH_MAX];
    if (join_paths(tmp_dir, sizeof(tmp_dir), stage, "/tmp") != 0 || mkdir_p(tmp_dir, 01777) != 0) {
        *err_out = errno;
        (void)umount2(stage, MNT_DETACH);
        return -1;
    }
    (void)chmod(tmp_dir, 01777);

    if (args->work_dir != NULL && args->work_dir[0] == '/') {
        if (bind_mount_entry(stage, args->work_dir, 0, err_out) != 0) {
            (void)umount2(stage, MNT_DETACH);
            return -1;
        }
    }

    if (args->exe_path != NULL && args->exe_path[0] == '/') {
        char exe_dir[PATH_MAX];
        size_t elen = strlen(args->exe_path);
        if (elen < sizeof(exe_dir)) {
            memcpy(exe_dir, args->exe_path, elen + 1);
            char* last_slash = strrchr(exe_dir, '/');
            if (last_slash != NULL && last_slash != exe_dir) {
                *last_slash = '\0';
                if (!is_under_system_ro_dir(exe_dir) &&
                    (args->work_dir == NULL || strcmp(exe_dir, args->work_dir) != 0)) {
                    /* Only mount exe_dir RO if it is not already covered by work_dir. */
                    int covered_by_work = 0;
                    if (args->work_dir != NULL) {
                        size_t wlen = strlen(args->work_dir);
                        if (strncmp(exe_dir, args->work_dir, wlen) == 0 &&
                            (exe_dir[wlen] == '\0' || exe_dir[wlen] == '/')) {
                            covered_by_work = 1;
                        }
                    }
                    if (!covered_by_work && bind_mount_entry(stage, exe_dir, 1, err_out) != 0) {
                        (void)umount2(stage, MNT_DETACH);
                        return -1;
                    }
                }
            }
        }
    }

    char proc_dst[PATH_MAX];
    if (join_paths(proc_dst, sizeof(proc_dst), stage, "/proc") != 0 || mkdir_p(proc_dst, 0555) != 0) {
        *err_out = errno;
        (void)umount2(stage, MNT_DETACH);
        return -1;
    }
    if (syscall(SYS_mount, "proc", proc_dst, "proc", (unsigned long)(MS_NOSUID | MS_NOEXEC | MS_NODEV), NULL) != 0) {
        *err_out = errno;
        (void)umount2(stage, MNT_DETACH);
        return -1;
    }

    char dev_dst[PATH_MAX];
    if (join_paths(dev_dst, sizeof(dev_dst), stage, "/dev") != 0 || mkdir_p(dev_dst, 0755) != 0) {
        *err_out = errno;
        (void)umount2(stage, MNT_DETACH);
        return -1;
    }

    static const char* const dev_nodes[] = { "/dev/null", "/dev/zero", "/dev/urandom" };
    for (size_t i = 0; i < sizeof(dev_nodes) / sizeof(dev_nodes[0]); i++) {
        if (bind_mount_dev_node(stage, dev_nodes[i], err_out) != 0) {
            (void)umount2(stage, MNT_DETACH);
            return -1;
        }
    }

    char old_root[PATH_MAX];
    if (join_paths(old_root, sizeof(old_root), stage, "/old_root") != 0 || mkdir_p(old_root, 0755) != 0) {
        *err_out = errno;
        (void)umount2(stage, MNT_DETACH);
        return -1;
    }

    if (syscall(SYS_pivot_root, stage, old_root) != 0) {
        *err_out = errno;
        (void)umount2(stage, MNT_DETACH);
        return -1;
    }

    if (syscall(SYS_chdir, "/") != 0) {
        *err_out = errno;
        return -1;
    }

    if (umount2("/old_root", MNT_DETACH) != 0) {
        *err_out = errno;
        return -1;
    }

    (void)rmdir("/old_root");
    return 0;
}

static int install_seccomp_filter(void) {
#if defined(__x86_64__)
    const uint32_t expected_arch = AUDIT_ARCH_X86_64;
#elif defined(__aarch64__)
    const uint32_t expected_arch = AUDIT_ARCH_AARCH64;
#else
    const uint32_t expected_arch = 0;
#endif

    if (expected_arch == 0) {
        return 0;
    }

    struct sock_filter filter[128];
    size_t idx = 0;

    filter[idx++] = (struct sock_filter)BPF_STMT(BPF_LD | BPF_W | BPF_ABS, (uint32_t)offsetof(struct seccomp_data, arch));
    filter[idx++] = (struct sock_filter)BPF_JUMP(BPF_JMP | BPF_JEQ | BPF_K, expected_arch, 1, 0);
    filter[idx++] = (struct sock_filter)BPF_STMT(BPF_RET | BPF_K, SECCOMP_RET_ERRNO | (EPERM & 0x0000FFFFU));
    filter[idx++] = (struct sock_filter)BPF_STMT(BPF_LD | BPF_W | BPF_ABS, (uint32_t)offsetof(struct seccomp_data, nr));

#define GLACIER_DENY_SYSCALL(nr_sym) \
    do { \
        filter[idx++] = (struct sock_filter)BPF_JUMP(BPF_JMP | BPF_JEQ | BPF_K, (uint32_t)(nr_sym), 0, 1); \
        filter[idx++] = (struct sock_filter)BPF_STMT(BPF_RET | BPF_K, SECCOMP_RET_ERRNO | (EPERM & 0x0000FFFFU)); \
    } while (0)

#ifdef __NR_mount
    GLACIER_DENY_SYSCALL(__NR_mount);
#endif
#ifdef __NR_umount2
    GLACIER_DENY_SYSCALL(__NR_umount2);
#endif
#ifdef __NR_pivot_root
    GLACIER_DENY_SYSCALL(__NR_pivot_root);
#endif
#ifdef __NR_chroot
    GLACIER_DENY_SYSCALL(__NR_chroot);
#endif
#ifdef __NR_unshare
    GLACIER_DENY_SYSCALL(__NR_unshare);
#endif
#ifdef __NR_setns
    GLACIER_DENY_SYSCALL(__NR_setns);
#endif
#ifdef __NR_ptrace
    GLACIER_DENY_SYSCALL(__NR_ptrace);
#endif
#ifdef __NR_process_vm_readv
    GLACIER_DENY_SYSCALL(__NR_process_vm_readv);
#endif
#ifdef __NR_process_vm_writev
    GLACIER_DENY_SYSCALL(__NR_process_vm_writev);
#endif
#ifdef __NR_kexec_load
    GLACIER_DENY_SYSCALL(__NR_kexec_load);
#endif
#ifdef __NR_kexec_file_load
    GLACIER_DENY_SYSCALL(__NR_kexec_file_load);
#endif
#ifdef __NR_init_module
    GLACIER_DENY_SYSCALL(__NR_init_module);
#endif
#ifdef __NR_finit_module
    GLACIER_DENY_SYSCALL(__NR_finit_module);
#endif
#ifdef __NR_delete_module
    GLACIER_DENY_SYSCALL(__NR_delete_module);
#endif
#ifdef __NR_reboot
    GLACIER_DENY_SYSCALL(__NR_reboot);
#endif
#ifdef __NR_swapon
    GLACIER_DENY_SYSCALL(__NR_swapon);
#endif
#ifdef __NR_swapoff
    GLACIER_DENY_SYSCALL(__NR_swapoff);
#endif
#ifdef __NR_bpf
    GLACIER_DENY_SYSCALL(__NR_bpf);
#endif
#ifdef __NR_userfaultfd
    GLACIER_DENY_SYSCALL(__NR_userfaultfd);
#endif
#ifdef __NR_perf_event_open
    GLACIER_DENY_SYSCALL(__NR_perf_event_open);
#endif
#ifdef __NR_add_key
    GLACIER_DENY_SYSCALL(__NR_add_key);
#endif
#ifdef __NR_request_key
    GLACIER_DENY_SYSCALL(__NR_request_key);
#endif
#ifdef __NR_keyctl
    GLACIER_DENY_SYSCALL(__NR_keyctl);
#endif
#ifdef __NR_settimeofday
    GLACIER_DENY_SYSCALL(__NR_settimeofday);
#endif
#ifdef __NR_clock_settime
    GLACIER_DENY_SYSCALL(__NR_clock_settime);
#endif
#ifdef __NR_adjtimex
    GLACIER_DENY_SYSCALL(__NR_adjtimex);
#endif
#ifdef __NR_clock_adjtime
    GLACIER_DENY_SYSCALL(__NR_clock_adjtime);
#endif
#ifdef __NR_ioperm
    GLACIER_DENY_SYSCALL(__NR_ioperm);
#endif
#ifdef __NR_iopl
    GLACIER_DENY_SYSCALL(__NR_iopl);
#endif
#ifdef __NR_open_by_handle_at
    GLACIER_DENY_SYSCALL(__NR_open_by_handle_at);
#endif
#ifdef __NR_name_to_handle_at
    GLACIER_DENY_SYSCALL(__NR_name_to_handle_at);
#endif
#ifdef __NR_acct
    GLACIER_DENY_SYSCALL(__NR_acct);
#endif
#ifdef __NR_quotactl
    GLACIER_DENY_SYSCALL(__NR_quotactl);
#endif
#ifdef __NR_syslog
    GLACIER_DENY_SYSCALL(__NR_syslog);
#endif
#ifdef __NR_vhangup
    GLACIER_DENY_SYSCALL(__NR_vhangup);
#endif
#ifdef __NR_personality
    GLACIER_DENY_SYSCALL(__NR_personality);
#endif

#undef GLACIER_DENY_SYSCALL

    filter[idx++] = (struct sock_filter)BPF_STMT(BPF_RET | BPF_K, SECCOMP_RET_ALLOW);

    struct sock_fprog prog;
    prog.len = (unsigned short)idx;
    prog.filter = filter;

    if (syscall(SYS_prctl, PR_SET_SECCOMP, (unsigned long)SECCOMP_MODE_FILTER, (unsigned long)&prog, 0UL, 0UL) != 0) {
        return -1;
    }

    return 0;
}

static size_t format_u64(char* buf, size_t cap, uint64_t val) {
    char rev[24];
    size_t n = 0;
    if (val == 0) {
        rev[n++] = '0';
    } else {
        while (val > 0 && n < sizeof(rev)) {
            rev[n++] = (char)('0' + (val % 10));
            val /= 10;
        }
    }
    if (n + 1 > cap) {
        return 0;
    }
    for (size_t i = 0; i < n; i++) {
        buf[i] = rev[n - 1 - i];
    }
    buf[n] = '\0';
    return n;
}

static int write_text_file(const char* path, const char* text, size_t len) {
    int fd = (int)syscall(SYS_openat, AT_FDCWD, path, O_WRONLY | O_CLOEXEC, 0);
    if (fd < 0) {
        return -1;
    }
    size_t off = 0;
    int rc = 0;
    while (off < len) {
        long w = syscall(SYS_write, fd, text + off, len - off);
        if (w < 0) {
            if (errno == EINTR) {
                continue;
            }
            rc = -1;
            break;
        }
        if (w == 0) {
            rc = -1;
            break;
        }
        off += (size_t)w;
    }
    raw_close_if_valid(fd);
    return rc;
}

static void try_setup_cgroup_v2(const glacier_sandbox_spawn_args_t* args, int32_t child_pid) {
    if (child_pid <= 0 || (!args->has_memory_limit && !args->has_max_processes)) {
        return;
    }

    int fd = (int)syscall(SYS_openat, AT_FDCWD, "/proc/self/cgroup", O_RDONLY | O_CLOEXEC, 0);
    if (fd < 0) {
        return;
    }

    char cg_buf[512];
    long n = syscall(SYS_read, fd, cg_buf, sizeof(cg_buf) - 1);
    raw_close_if_valid(fd);
    if (n <= 0) {
        return;
    }
    cg_buf[n] = '\0';

    /* Look for unified cgroup v2 line "0::<relpath>". */
    const char* line = cg_buf;
    const char* rel = NULL;
    while (line != NULL && *line != '\0') {
        const char* nl = strchr(line, '\n');
        if (strncmp(line, "0::", 3) == 0) {
            rel = line + 3;
            if (nl != NULL) {
                size_t rlen = (size_t)(nl - rel);
                if (rlen >= 256) {
                    return;
                }
                char rel_copy[256];
                memcpy(rel_copy, rel, rlen);
                rel_copy[rlen] = '\0';

                char pid_str[24];
                if (format_u64(pid_str, sizeof(pid_str), (uint64_t)(uint32_t)child_pid) == 0) {
                    return;
                }

                char base_cg[PATH_MAX];
                if (join_paths(base_cg, sizeof(base_cg), "/sys/fs/cgroup", rel_copy[0] == '\0' ? "/" : rel_copy) != 0) {
                    return;
                }

                char leaf_name[48];
                memcpy(leaf_name, "glacier_sbx_", 12);
                size_t plen = strlen(pid_str);
                memcpy(leaf_name + 12, pid_str, plen + 1);

                char cg_dir[PATH_MAX];
                if (join_paths(cg_dir, sizeof(cg_dir), base_cg, leaf_name) != 0) {
                    return;
                }

                if (mkdir(cg_dir, 0755) != 0) {
                    return;
                }

                char file_path[PATH_MAX];
                if (args->has_memory_limit) {
                    char mem_str[24];
                    size_t mlen = format_u64(mem_str, sizeof(mem_str), (uint64_t)args->memory_limit_bytes);
                    if (mlen > 0 && join_paths(file_path, sizeof(file_path), cg_dir, "memory.max") == 0) {
                        (void)write_text_file(file_path, mem_str, mlen);
                    }
                    if (join_paths(file_path, sizeof(file_path), cg_dir, "memory.swap.max") == 0) {
                        (void)write_text_file(file_path, "0", 1);
                    }
                }

                if (args->has_max_processes) {
                    char proc_str[24];
                    size_t prlen = format_u64(proc_str, sizeof(proc_str), (uint64_t)(uint32_t)args->max_processes);
                    if (prlen > 0 && join_paths(file_path, sizeof(file_path), cg_dir, "pids.max") == 0) {
                        (void)write_text_file(file_path, proc_str, prlen);
                    }
                }

                if (join_paths(file_path, sizeof(file_path), cg_dir, "cgroup.procs") != 0 ||
                    write_text_file(file_path, pid_str, strlen(pid_str)) != 0) {
                    (void)rmdir(cg_dir);
                }
            }
            return;
        }
        line = nl != NULL ? nl + 1 : NULL;
    }
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

    /* Wait for parent uid_map/gid_map + cgroup setup before mount/execve. */
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
        if (syscall(SYS_mount, NULL, "/", NULL, (unsigned long)(MS_REC | MS_PRIVATE), NULL) != 0) {
            int mnt_err = errno;
            if (args->enable_namespace_isolation == 1) {
                write_child_errno_and_exit(args->err_write_fd, mnt_err);
            }
        }
        else {
            int pivot_err = 0;
            if (setup_pivoted_rootfs(args, &pivot_err) != 0) {
                if (args->enable_namespace_isolation == 1) {
                    write_child_errno_and_exit(args->err_write_fd, pivot_err);
                }
                if (syscall(
                        SYS_mount,
                        "proc",
                        "/proc",
                        "proc",
                        (unsigned long)(MS_NOSUID | MS_NOEXEC | MS_NODEV),
                        NULL) != 0 &&
                    args->enable_namespace_isolation == 1) {
                    write_child_errno_and_exit(args->err_write_fd, errno);
                }
            }
        }
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

    if (syscall(SYS_prctl, PR_SET_NO_NEW_PRIVS, 1UL, 0UL, 0UL, 0UL) != 0 &&
        args->enable_namespace_isolation == 1) {
        write_child_errno_and_exit(args->err_write_fd, errno);
    }

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

    if (install_seccomp_filter() != 0 && args->enable_namespace_isolation == 1) {
        write_child_errno_and_exit(args->err_write_fd, errno);
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
        try_setup_cgroup_v2(args, (int32_t)pid);
        out->child_pid = (int32_t)pid;
        out->pidfd = -1;
        out->user_ns_active = user_ns_active;
        out->mode = user_ns_active ? GLACIER_MODE_FORK_UNSHARE_NS : GLACIER_MODE_FORK_NO_NS;
        return 0;
    }

    try_setup_cgroup_v2(args, (int32_t)pid);
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
            try_setup_cgroup_v2(args, (int32_t)res);
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
                try_setup_cgroup_v2(args, (int32_t)plain_res);
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
            try_setup_cgroup_v2(args, (int32_t)res);
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
