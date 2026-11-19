# Known bugs: Linux

Bugs and limits whose fix belongs to the **Linux** area ([CONTRIBUTING.md → Areas](../CONTRIBUTING.md#areas)). The other
areas: [Core](../core/FIXES.md), [Windows](../windows/FIXES.md), [UI](../ui/FIXES.md).

Each entry: what was seen, why, what to do, and when it counts as fixed, with the GitHub issue that tracks it.
Priorities: **high** = breaks normal use. An entry is kept after the fix, marked **fixed**, when its cause or evidence
is worth keeping.

## 1. Linux capture limitations

- UDP of tunneled apps is dropped (see [REQUIRED.md](../REQUIRED.md#2-udp-through-tunnels-on-linux)).
- ~~IPv6 extension headers aren't parsed in `PacketInfo.TryParse`~~ **fixed** (issue #35, PR #27): the parser follows
  the chain; ESP, later fragments and Routing headers with segments left still pass uncaptured.
- With no non-loopback upstream DNS (e.g. dnsmasq without systemd-resolved), DNS isn't captured (a log line says so).
  TPROXY for UDP would solve it (the original destination comes with each datagram).
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
