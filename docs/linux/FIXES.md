# Known bugs: Linux

Bugs and limits whose fix belongs to the **Linux** area ([CONTRIBUTING.md → Areas](../CONTRIBUTING.md#areas)). The other
areas: [Core](../core/FIXES.md), [Windows](../windows/FIXES.md), [UI](../ui/FIXES.md).

Each entry: what was seen, why, what to do, and when it counts as fixed, with the GitHub issue that tracks it.
Priorities: **high** = breaks normal use. An entry is kept after the fix, marked **fixed**, when its cause or evidence
is worth keeping.

## 1. Linux capture limitations

- ~~UDP of tunneled apps is dropped~~ **fixed** (#60): taken through a tproxy into `UdpFlows` (REQUIRED.md §2).
- ~~Capture fails when IPv6 is disabled~~ **fixed** (#216): with `net.ipv6.conf.*.disable_ipv6=1`, `::1` is gone,
  so `LinuxDnsServer` couldn't bind it ("Cannot assign requested address") and capture didn't start; and adding the
  IPv6 policy route fails with EACCES, which lost UDP tunnelling for IPv4 too and left the IPv4 rule behind. Both now
  carry on with IPv4 only (`LinuxIpv6DisabledNetnsTests`).
- ~~IPv6 extension headers aren't parsed in `PacketInfo.TryParse`~~ **fixed** (issue #35, PR #27): the parser follows
  the chain; ESP, later fragments and Routing headers with segments left still pass uncaptured.
- With no non-loopback upstream DNS (e.g. dnsmasq without systemd-resolved), DNS isn't captured (a log line says so).
  The UDP tproxy (#60) doesn't change this: UDP/53 stays with Sluice's DNS server, which needs an upstream to forward to.
- ~~Finding a connection's owner reads all of `/proc/net/tcp` for every new connection~~ **fixed** (issue #82): each
  new connection cost ~4.4 ms on the packet thread (60 at once: the last waited ~267 ms). `LinuxProcessLookup` now asks
  the kernel's socket diagnostics (`Native/SockDiag.cs`, `NETLINK_SOCK_DIAG`) for the socket's inode and uid (~0.01 ms;
  UDP by local port), falls back to `/proc/net` if that isn't available (logged once), and maps the inode to a process
  from a lock-free cache or by scanning the socket owner's newest processes first (`getdents64` + `readlinkat`).
  Measured live: 60 connects at once spread 7–12 ms (was 116–214 ms). Still open: a miss scans `/proc/PID/fd`
  (~0.8 ms for a program holding ~250 sockets); #83 moves decisions off the packet thread.
- Running the GUI as root breaks notifications (no session bus) and Wayland may refuse it. The installer's `setcap`
  avoids that; long-term a privileged service (see [core FEATURES #5](../core/FEATURES.md)).

## 2. Captured connections reset after a source-port clash — fixed

**Seen** (issue #38, fixed by PR #75): now and then a connection through capture was reset at once, with `No redirect record for
<addr>:<port>; dropping` in the log: 19 times in about 7 hours of normal use on one machine, half of them on ports
outside `ip_local_port_range`, and one of 6 parallel downloads in a speed test. Reproduced every time: two sockets bound
to one port (`SO_REUSEADDR`) connecting through capture to two different servers; the second was reset.

**Why:** Linux lets two connections share a source port when their destinations differ. `redirect` gives every
captured connection the same new destination (the relay), so such a pair clashes after NAT and conntrack renumbers one
connection's source port. The flow table was keyed by the app's source endpoint alone, so the relay found no record for
the renumbered port; and when both first packets were queued before the relay accepted either, the second record
overwrote the first, which could send a connection to the other one's destination.

**Fix:** `FlowRecords` keys the records by source **and** original destination. When the relay accepts a connection,
`Conntrack` asks the kernel's connection tracking (ctnetlink over netlink, `IPCTNL_MSG_CT_GET` by the reply tuple relay
→ client) for its original tuple, which gives the app's real source port and the destination. Without conntrack (no
`CAP_NET_ADMIN`, an expired entry) the only record for the source is used, and none when there are several.
`IRedirectTable` and `TransparentInbound` are unchanged. Tests: `LinuxFlowRecordsTests`, `LinuxConntrackTests` (its
kernel test runs in a network namespace of its own:
`unshare -rn sh -c 'ip link set lo up && SLUICE_NETNS_TEST=1 dotnet test tests/Sluice.Tests.Linux --no-build --filter LinuxConntrackTests'`).

**Fixed when** (passes): the two-socket reproduction reaches **both** servers, one after the other and with both
connections opened at once (each answer checked, 12 of 12), the log has no "No redirect record", and 3 bursts of 30
parallel downloads through capture complete (90 of 90).

## 3. Conntrack lookup: follow-ups from the review of PR #75 — low

Three small improvements to the fix in §2, found in the review. None is a bug seen in use; no issue yet.

1. **Lookups wait in line.** `Conntrack.TryGetOriginal` runs under one lock with a 1 s receive timeout. If conntrack
   ever stopped answering, every connection the relay accepts would wait up to a second, one after another.
   **To do:** after a few consecutive timeouts, skip the lookup for a while (fall back to the record by source) and log
   it once.
2. **No sender check.** Answers are read with `recv`, so the code doesn't check who sent them. **To do:** read with
   `recvfrom` and accept only messages from the kernel (`nl_pid` 0), a cheap safeguard.
3. **Fragile logging check.** `FlowTable.TryGetOriginal` decides what to log by comparing the `how` text
   (`"conntrack"`, `"by source"`) from `FlowRecords.TryTake`. **To do:** return a small enum with the text kept only
   for the log line.

**Fixed when:** with conntrack made unavailable mid-capture (for example by a test double that never answers), new
connections are not delayed beyond the first few; replies not from the kernel are ignored (unit test); the logging
decision uses the enum.

## 4. CAP_NET_ADMIN left raised after starting nft — fixed

**Seen** ([issue #99](https://github.com/ZaidX88/sluice/issues/99), part of #9, found while testing #25): with the app
installed by `install.sh` and capture started once, the in-process GTK file dialog aborted the app (`Loader process
exited early`): GTK's sandboxed image loader (`bwrap`) refused to run ("Unexpected capabilities but not setuid"). The
thread that had started capture showed `CapAmb: cap_net_admin`, also after capture was turned off.

**Why:** `NftRules.Run` raised `CAP_NET_ADMIN` as an ambient capability (and added it to the inheritable set) so `nft`
gets it under `setcap`, and never lowered it. Every thread and program started from that thread afterwards had it too.

**Fixed:** `Libc.LowerAmbient` lowers the ambient capability and clears it from the inheritable set; `NftRules.Run`
calls it right after `nft` has started (in a `finally`, so a failed start lowers it too). `nft` keeps its own copy.
Before the first raise, `NftRules` also starts .NET's signal-handling thread (`.NET SigHandler`), which .NET otherwise
creates during the first `Process.Start`, i.e. while the capability is raised, and which would keep it. The
sing-box helpers (SOCKS inbound only) and `resolvectl flush-caches` don't need the capability. Tests:
`LinuxAmbientCapabilityTests` (they raise the capability only when it is permitted; see the class for running them in a
user namespace). Live, with the `setcap` install: before, 7 of 38 threads had `CAP_NET_ADMIN` as ambient; after
starting, stopping and restarting capture, none of 41 threads has it, ambient or inheritable. The file dialog with
capture on also needs #28 (capture's group IDs, §5).

## 5. Capture's group IDs kept GTK from starting — fixed

**Seen** ([issue #28](https://github.com/ZaidX88/sluice/issues/28), part of #9, found while testing #25): with capture
on, the in-process GTK file dialog didn't start: GTK refuses to run in a process whose real, effective and saved group
IDs differ ("running setuid or setgid"), and capture switched only two of them (`Gid: 377294 377294 1000`).

**Why:** `LinuxCapture` called `setresgid(gid, gid, -1)`, keeping the user's group as the saved one so it could switch
back without privileges. It doesn't need to: switching to Sluice's group needs `CAP_SETGID` anyway, and that also
switches back.

**Fixed:** capture calls `setresgid(gid, gid, gid)` and, on stop, restores the real, effective and saved group it read
from `/proc/self/status` (an error is logged if it can't). Sluice's own traffic is still excluded by `meta skgid`.

## 6. sing-box helpers outlived Sluice — fixed

**Seen** ([issue #57](https://github.com/ZaidX88/sluice/issues/57), recorded from #36): when the app was stopped by a
signal (`kill`, logout, Ctrl+C), crashed or got SIGKILL, its `sing-box run` helpers kept running under systemd, each with
its SSH session open. Only a normal shutdown (`SingBoxSession.DisposeAsync`) stopped them. Windows avoids this with a
kill-on-close job object.

**Why:** Linux has no job object, and `PR_SET_PDEATHSIG` can only be set by the helper itself before it runs sing-box,
while Core starts the helper and hands it to `IPlatformServices.TieChildProcess` afterwards.

**Fixed:** `LinuxPlatform.TieChildProcess` (`HelperTies`):
- on SIGTERM, SIGINT, SIGHUP, an unhandled exception or an exit that didn't stop the tunnels, the tied helpers get
  SIGTERM; a helper a tunnel starts while Sluice shuts down gets it at once;
- each tied helper is also written to `<pid>-<start time>.helpers` in the helper folder (`HelperConfigFolder`). The
  next Sluice ends the helpers listed in the files of Sluice processes that are gone (SIGKILL, a crash), when it starts.
- Processes are known by pid and start time (`/proc/<pid>/stat`), and a file by its boot, so a reused pid is never
  signalled and the helpers of another running Sluice are left alone.

Tests: `LinuxHelperTiesTests` (a fake `/proc` for the rules, and a real child process ended as a leftover). Live, with
the `setcap` install and an SSH tunnel through sing-box: before, the helper kept running after `kill -TERM` and
`kill -KILL` of the app; after, none is left after TERM, HUP or INT, and after KILL the next start ends it.
