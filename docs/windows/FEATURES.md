# Features: Windows

Ideas not built yet for the **Windows** area (roughly by value) and its performance work. The other areas:
[Core](../core/FEATURES.md), [Linux](../linux/FEATURES.md), [UI](../ui/FEATURES.md). Work that also needs another area
says so; it gets a parent issue with one sub-issue per area ([CONTRIBUTING.md → Issues](../CONTRIBUTING.md#issues)).

## Features

1. **Capture WSL / VMs on Windows**: tunnel their traffic too. Not possible with WinDivert today: Windows' NAT forwards
   their connections below the layers it sees ([FIXES.md §1](FIXES.md#1-windows-wsl--vms-get-fake-dns-answers-they-cant-use--fixed-nat-mode), which makes WSL work
   direct while capturing).

## Performance

- **Windows capture throughput** (one packet thread doing one receive per packet): measured, not limiting on a
  ~2 Mbps tunnel; may matter on faster ones (locally the WinDivert loop measured ~16–35 MB/s versus ~400 MB/s through
  the SOCKS listener). Possible improvements are listed under "Later" in
  [FIXES.md §3](FIXES.md#3-windows-capture-seemed-slower-than-the-socks-listener--fixed).
- **UDP "through tunnels" on Windows** sends every UDP packet through Sluice. Exclude LAN, multicast and always-direct
  ports in the WinDivert filter.
