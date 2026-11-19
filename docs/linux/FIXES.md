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
- `LinuxProcessLookup` scans `/proc/*/fd` on a miss. Fast in tests (20 parallel connections in ~0.15 s); on machines
  with tens of thousands of fds consider `NETLINK_SOCK_DIAG` + a background inode→pid cache.
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
