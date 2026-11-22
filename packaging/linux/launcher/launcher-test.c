/*
 * Tests for the launcher's checks, built and run by scripts/package-linux.sh before it builds the launcher.
 * Includes the launcher's source, so it tests the real functions. Runs as a normal user: folders the user owns
 * must be refused, root's must pass.
 *
 *   cc -O2 -Wall -Wextra -Werror -o launcher-test launcher-test.c && ./launcher-test
 */
#define main launcher_main
#include "sluice-launcher.c"
#undef main

#include <ftw.h>
#include <sys/wait.h>

static int failures;

/* Whether everything in a folder (links not followed) is owned by root and not writable by group or others: only then
   should the launcher accept it. */
static int root_only_entry(const char *path, const struct stat *st, int type, struct FTW *ftw)
{
    (void)path; (void)ftw;
    if (type == FTW_SL) return 0;
    return st->st_uid != 0 || (st->st_mode & (S_IWGRP | S_IWOTH)) ? 1 : 0;
}

static int root_only_tree(const char *path)
{
    return nftw(path, root_only_entry, 32, FTW_PHYS) == 0;
}

static void expect(int ok, const char *what)
{
    printf("%s  %s\n", ok ? "ok  " : "FAIL", what);
    if (!ok) failures++;
}

/* Runs a check in a child (a refusal exits): 1 when it passed, 0 when it refused. */
static int passes(void (*check)(const char *, int), const char *path, int links)
{
    fflush(stdout);
    pid_t pid = fork();
    if (pid == 0) {
        if (!freopen("/dev/null", "w", stderr)) _exit(2);
        check(path, links);
        _exit(0);
    }
    int status;
    waitpid(pid, &status, 0);
    return WIFEXITED(status) && WEXITSTATUS(status) == 0;
}

int main(void)
{
    /* Environment */
    expect(kept("HOME=/home/alice"), "HOME is kept");
    expect(!kept("DOTNET_STARTUP_HOOKS=/tmp/x.dll"), "DOTNET_STARTUP_HOOKS is dropped");
    expect(!kept("LD_PRELOAD=/tmp/x.so"), "LD_PRELOAD is dropped");
    expect(!kept("PATH=/home/alice/bin"), "PATH is dropped (the launcher sets its own)");
    expect(!kept("HOMEX=1"), "a name that only starts like a kept one is dropped");
    expect(kept("LANG=en_US.UTF-8"), "LANG=en_US.UTF-8 is kept");
    expect(!kept("LANG=/home/alice/x"), "LANG with a path is dropped");
    expect(kept("LANGUAGE=en_US:en"), "LANGUAGE=en_US:en is kept");
    expect(!kept("LANGUAGE=../../home/alice"), "LANGUAGE with a path is dropped");
    expect(kept("LC_TIME=en_GB.UTF-8"), "LC_TIME=en_GB.UTF-8 is kept");
    expect(!kept("LC_ALL=/home/alice/locale"), "LC_ALL with a path is dropped");
    expect(kept("TZ=Europe/London"), "TZ=Europe/London is kept");
    expect(kept("TZ=:Europe/London"), "TZ=:Europe/London is kept");
    expect(kept("TZ=UTC0"), "TZ=UTC0 is kept");
    expect(!kept("TZ=/home/alice/zone"), "TZ=/path is dropped");
    expect(!kept("TZ=:/home/alice/zone"), "TZ=:/path is dropped");
    expect(!kept("TZ=../../home/alice/zone"), "TZ with .. is dropped");

    /* Folders */
    expect(passes(check_folder, "/usr/include/linux", 0), "a root-only folder passes");
    if (geteuid() != 0) {   /* as root, the folders it makes are root's */
        char mine[] = "/tmp/sluice-launcher-test.XXXXXX";
        if (!mkdtemp(mine)) { perror("mkdtemp"); return 1; }
        expect(!passes(check_folder, mine, 0), "a folder the user owns is refused");
        char link[PATH_MAX];
        snprintf(link, sizeof link, "%s/to-root", mine);
        expect(symlink("/usr/include/linux", link) == 0, "(set up a link to a root-only folder)");
        expect(!passes(check_folder, link, 1), "a root-only folder reached through a link in a user's folder is refused");
        unlink(link);
        rmdir(mine);
    }

    /* A real .NET folder with internal links, if one is installed */
    const char *dotnet = access("/usr/lib/dotnet/shared", F_OK) == 0 ? "/usr/lib/dotnet"
                       : access("/usr/share/dotnet/shared", F_OK) == 0 ? "/usr/share/dotnet" : NULL;
    if (dotnet && root_only_tree(dotnet)) {
        expect(passes(check_folder, dotnet, 1), "the system's .NET folder passes with its own links allowed");
    } else if (dotnet) {
        /* e.g. a CI runner, where the .NET folder isn't root-only: the launcher rightly refuses it (#193) */
        printf("skip  the system's .NET folder (%s) isn't root-only here\n", dotnet);
    }

    printf("%s\n", failures ? "launcher tests FAILED" : "launcher tests passed");
    return failures != 0;
}
