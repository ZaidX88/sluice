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
