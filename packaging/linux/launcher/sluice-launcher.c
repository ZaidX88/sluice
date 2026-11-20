/*
 * Sluice launcher (Linux). The only file that install.sh gives capabilities to.
 *
 * It starts Sluice.Desktop, or Sluice.Cli when started under the name "sluice-cli", from its own folder, and passes
 * the capture capabilities on to it as ambient capabilities. The .NET apps themselves carry none, so the kernel
 * doesn't treat them as privileged binaries and .NET's own start-up isn't trusted with the capabilities:
 *
 *   - Only a fixed list of environment variables is passed on; everything else (DOTNET_*, COREHOST_*, COMPlus_*,
 *     LD_* …) is dropped. PATH is reset. DOTNET_EnableDiagnostics=0 keeps the runtime from opening its diagnostics
 *     socket.
 *   - The app is an absolute path in the launcher's own folder, never taken from the arguments or the environment.
 *     The name it was started under only chooses between the two fixed apps.
 *   - It refuses to run unless root alone can change its folder, every folder above it, and everything in it.
 *
 * Sluice clears the passed-on capabilities before it starts any other program (Sluice.Platform.Linux).
 *
 * Build: cc -O2 -Wall -Wextra -Werror -o sluice sluice-launcher.c   (scripts/package-linux.sh does this)
 */
#define _GNU_SOURCE
#include <errno.h>
#include <ftw.h>
#include <libgen.h>
#include <limits.h>
#include <linux/capability.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/prctl.h>
#include <sys/stat.h>
#include <sys/syscall.h>
#include <unistd.h>

#define SAFE_PATH "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin"

/* cap_net_admin: nftables, NFQUEUE. cap_setgid: Sluice's network group. cap_sys_ptrace, cap_dac_read_search: which
 * app (of any user) owns a connection. The same set install.sh used to give the apps. */
static const int CAPS[] = { CAP_NET_ADMIN, CAP_SETGID, CAP_SYS_PTRACE, CAP_DAC_READ_SEARCH };
#define NCAPS (sizeof CAPS / sizeof CAPS[0])

/* Passed on as they are: where the session is, the user's folders and language. Nothing that names code to load. */
static const char *const KEEP[] = {
    "HOME", "USER", "LOGNAME", "LANG", "LANGUAGE", "TZ", "TERM", "COLORTERM", "NO_COLOR",
    "DISPLAY", "XAUTHORITY", "WAYLAND_DISPLAY", "XCURSOR_THEME", "XCURSOR_SIZE",
    "XDG_RUNTIME_DIR", "XDG_CONFIG_HOME", "XDG_DATA_HOME", "XDG_STATE_HOME", "XDG_CACHE_HOME",
    "XDG_CURRENT_DESKTOP", "XDG_SESSION_TYPE", "XDG_SESSION_DESKTOP", "DESKTOP_SESSION",
    "DBUS_SESSION_BUS_ADDRESS", "AVALONIA_SCREEN_SCALE_FACTORS", "AVALONIA_GLOBAL_SCALE_FACTOR",
    NULL,
};

static void die(const char *what, const char *detail)
{
    fprintf(stderr, "sluice: %s%s%s\n", what, detail ? ": " : "", detail ? detail : "");
    exit(126);
}

static int kept(const char *entry)
{
    if (strncmp(entry, "LC_", 3) == 0) return 1;
    for (const char *const *k = KEEP; *k; k++) {
        size_t n = strlen(*k);
        if (strncmp(entry, *k, n) == 0 && entry[n] == '=') return 1;
    }
    return 0;
}

/* Owned by root and not writable by group or others. */
static int safe(const struct stat *st)
{
    return st->st_uid == 0 && (st->st_mode & (S_IWGRP | S_IWOTH)) == 0;
}

static const char *unsafe_path;

static int check_entry(const char *path, const struct stat *st, int type, struct FTW *ftw)
{
    (void)ftw;
    if (type == FTW_SL || type == FTW_NS || type == FTW_DNR || !safe(st)) { unsafe_path = path; return 1; }
    return 0;
}

/* The folder, every folder above it, and everything inside it (links refused). */
static void check_folder(const char *dir)
{
    char up[PATH_MAX], tmp[PATH_MAX];
    struct stat st;
    snprintf(up, sizeof up, "%s", dir);
    for (;;) {
        if (stat(up, &st) != 0 || !safe(&st)) die("refusing to run: only root may be able to change", up);
        if (strcmp(up, "/") == 0) break;
        snprintf(tmp, sizeof tmp, "%s", up);
        snprintf(up, sizeof up, "%s", dirname(tmp));
    }
    if (nftw(dir, check_entry, 16, FTW_PHYS) != 0)
        die("refusing to run: only root may be able to change", unsafe_path ? unsafe_path : dir);
}

/* Inheritable = exactly the capture capabilities we hold (ambient requires them there), then ambient = the same. */
static void pass_on_capabilities(void)
{
    struct __user_cap_header_struct hdr = { _LINUX_CAPABILITY_VERSION_3, 0 };
    struct __user_cap_data_struct data[2];
    if (syscall(SYS_capget, &hdr, data) != 0) die("capget", strerror(errno));
    __u32 want[2] = { 0, 0 };
    for (size_t i = 0; i < NCAPS; i++)
        if (data[CAPS[i] / 32].permitted & (1u << (CAPS[i] % 32))) want[CAPS[i] / 32] |= 1u << (CAPS[i] % 32);
    data[0].inheritable = want[0];
    data[1].inheritable = want[1];
    if (syscall(SYS_capset, &hdr, data) != 0) die("capset", strerror(errno));
    if (prctl(PR_CAP_AMBIENT, PR_CAP_AMBIENT_CLEAR_ALL, 0, 0, 0) != 0) die("clearing ambient capabilities", strerror(errno));
    for (size_t i = 0; i < NCAPS; i++)
        if ((want[CAPS[i] / 32] & (1u << (CAPS[i] % 32))) && prctl(PR_CAP_AMBIENT, PR_CAP_AMBIENT_RAISE, CAPS[i], 0, 0) != 0)
            die("raising an ambient capability", strerror(errno));
}

int main(int argc, char **argv)
{
    char self[PATH_MAX], dir[PATH_MAX], app[PATH_MAX];
    ssize_t n = readlink("/proc/self/exe", self, sizeof self - 1);
    if (n <= 0 || (size_t)n >= sizeof self - 1) die("can't find the launcher's own path", NULL);
    self[n] = '\0';
    snprintf(dir, sizeof dir, "%s", dirname(self));   /* dirname may change self; it isn't used again */

    /* Only the last part of the name it was started under; anything else starts the desktop app. */
    const char *name = argv[0] ? strrchr(argv[0], '/') : NULL;
    name = name ? name + 1 : argv[0] ? argv[0] : "";
    const char *target = strcmp(name, "sluice-cli") == 0 ? "Sluice.Cli" : "Sluice.Desktop";
    if ((size_t)snprintf(app, sizeof app, "%s/%s", dir, target) >= sizeof app) die("path too long", dir);

    check_folder(dir);
    struct stat st;
    if (stat(app, &st) != 0 || !S_ISREG(st.st_mode)) die("not found", app);

    /* Environment: the kept variables, a safe PATH, no .NET diagnostics. */
    extern char **environ;
    size_t count = 0;
    for (char **e = environ; *e; e++) count++;
    char **env = calloc(count + 3, sizeof *env);
    if (!env) die("out of memory", NULL);
    size_t j = 0;
    for (char **e = environ; *e; e++)
        if (kept(*e)) env[j++] = *e;
    env[j++] = "PATH=" SAFE_PATH;
    env[j++] = "DOTNET_EnableDiagnostics=0";
    env[j] = NULL;

    pass_on_capabilities();

    char *none[] = { app, NULL };
    if (argc > 0) argv[0] = app; else argv = none;
    execve(app, argv, env);
    die("can't start", app);
}
