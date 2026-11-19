# Features: Core

Ideas not built yet for the **Core** area (roughly by value) and its performance work. The other areas:
[Linux](../linux/FEATURES.md), [Windows](../windows/FEATURES.md), [UI](../ui/FEATURES.md). Work that also needs another
area says so; it gets a parent issue with one sub-issue per area ([CONTRIBUTING.md →
Issues](../CONTRIBUTING.md#issues)).

## Features

1. **UDP for the local SOCKS5 listener:** `Socks5Inbound` has no UDP ASSOCIATE, so apps pointed at `127.0.0.1:1080` get
   no UDP.
2. **Kill switch:** when every tunnel a rule uses is down (`TunnelBackends` states), block that rule's traffic instead
   of failing per connection, with "allow LAN" as an exception.
3. **Faster failover:** redundancy chains skip tunnels the health monitor knows are down.
4. **Restore after sleep:** reconnect tunnels and flush stale fake-DNS entries on resume, not only on network change.
5. **Service / daemon mode** (Core, with Windows and Linux service parts and the UI as a client): a Windows service and
   a systemd unit run the engine with the needed rights; the UI becomes a client over a local pipe/socket. Fixes
   start-at-login with rights on both OSes and the root-GUI problems on Linux.
6. **Local DNS option per tunnel:** resolve on the PC and send the server an IP, for servers with slow DNS (one SSH
   server took ~5 s per first visit). Opt-in, because DNS then leaves the tunnel.
7. **Rules from domain lists** (geosite / plain text import).
8. **Speed and latency test for all tunnels**, sortable; auto-pick the fastest for load-balancing chains.
9. **Scheduled subscription updates** (e.g. every 12 h) with a notification summary.
10. **DNS over HTTPS** for Sluice's own resolver.
