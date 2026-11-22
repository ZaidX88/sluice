# Status

Where Sluice stands and how it is put together. For what to do next see [REQUIRED.md](REQUIRED.md) (must-do),
each area's `FIXES.md` (known bugs) and `FEATURES.md` (ideas), listed in [README.md](README.md).

Last updated: 2026-11-16.

## What works

Sluice captures other applications' connections and routes each one by rules: **Direct**, **Block**, or through a
**tunnel** (SSH, SOCKS4/5, HTTP(S), VLESS, VMess, Trojan, Shadowsocks, Hysteria2, TUIC) or a chain of tunnels.

| Area | Windows | Linux |
|---|---|---|
| Routing engine, rules, chains, `.sluice` profiles, profile password encryption | ✅ | ✅ |
| Tunnels: SSH (sing-box client, SSH.NET fallback; direct / payload / TLS+SNI modes), SOCKS4/5, HTTP(S) | ✅ | ✅ |
| Tunnels through the sing-box helper: Shadowsocks (incl. 2022), VMess, VLESS (+REALITY/Vision), Trojan, Hysteria2, TUIC; ws / gRPC / HTTP / HTTPUpgrade transports | ✅ | ✅ |
| Share links and subscriptions | ✅ | ✅ |
| Capturing other apps' traffic | ✅ WinDivert | ✅ nftables + NFQUEUE |
| Fake DNS (198.18.0.0/15) so hostnames reach tunnels; kept across restarts (`fake-dns.json`) | ✅ | ✅ |
| UDP through tunnels (QUIC, games, calls) | ✅ | ✅ tproxy (#60) |
| Tunnel lifecycle: on/off, idle stop, health checks, retries, restart on network change, notifications | ✅ | ✅ |
| Desktop app (Avalonia, light/dark, tray) | ✅ | ✅ (tray needs a StatusNotifier host; without one, closing the window quits) |
| Command line (`Sluice.Cli`) | ✅ | ✅ |
| Release package | ❌ not yet | ✅ app-only tarball + `install.sh` ([linux/INSTALL.md](linux/INSTALL.md)) |

**Not supported, by design:** Xray-only features that sing-box doesn't implement, namely the `xhttp` transport and
VLESS post-quantum encryption (`mlkem768x25519plus`). Sluice shows a clear error for such links.

**Tested against real servers** (2026-11-13, 79 servers from a public list): Shadowsocks (all ciphers incl. 2022),
VLESS REALITY / Vision / gRPC / WS-TLS, Trojan and VMess work. Hysteria2 / TUIC are verified end to end against local
sing-box servers on Windows and Linux. Two compatibility bugs found that way were fixed: SNI for CDN-fronted links,
Xray-only uTLS fingerprints.

## Projects

How the modules fit together: [ARCHITECTURE.md](ARCHITECTURE.md); how each build picks its OS module:
[architecture/platforms.md](architecture/platforms.md).

| Project | Target | Role |
|---|---|---|
| `src/Sluice.Core` | net10.0 | Everything shared: engine, tunnels, DNS, profiles, inbound listeners, the interfaces `Platform/IPlatformServices.cs`, `Interception/ITrafficCapture.cs` and `IProcessLookup` (in `Platform/ProcessLookup.cs`). **Still contains Windows-only code** (iphlpapi lookups, DPAPI); see [REQUIRED.md](REQUIRED.md#1-move-windows-only-code-out-of-core). |
| `src/Sluice.Platform.Windows` | net10.0-windows10.0.19041.0 | Toast notifications (Start Menu shortcut with AUMID "Sluice"), the WinDivert capture (`Capture/`: `Interceptor`, `WinDivertNative`, `CaptureFilter`, `WinDivertUdp`), the helpers' job object, the DNS cache flush. |
| `src/Sluice.Platform.Linux` | net10.0 | notify-send notifications, `/proc` process lookup, nftables/NFQUEUE capture (`Capture/`), loopback DNS server. |
| `src/Sluice.Desktop` | net10.0-windows…; net10.0 | The Avalonia app. `#if WINDOWS` in `Services/Platform.cs` picks the platform module. |
| `src/Sluice.Cli` | net10.0-windows…; net10.0 | Headless engine; `#if WINDOWS` picks the platform module, and `--intercept` takes its capture from it. |
| `tests/Sluice.Tests` (+ `.Linux`, `.Windows`, `.Support`) | net10.0 (Windows tests: net10.0-windows) | xUnit: shared tests plus one project per platform; run with `Sluice.Linux.slnf` / `Sluice.Windows.slnf`. |
| `build/Dependencies.targets` | — | Fetches WinDivert and sing-box on the first build (pinned, SHA-256 checked) into `.deps/`. |
| `scripts/` | — | Live-test helpers (`scripts/linux/`), sing-box download scripts. Linux release packaging: `scripts/package-linux.sh` and `packaging/linux/`. |

## How capture works

**Windows** (`src/Sluice.Platform.Windows/Capture/`): WinDivert sees each new outbound connection's first packet. `Interceptor`
finds the owning process (iphlpapi), asks `RoutingEngine.Classify`, and reflects connections to handle into the local
`TransparentInbound` relay (`PacketReflector`). UDP/53 queries are answered with fake addresses by `DnsRedirector`; other
UDP of tunneled apps goes through `UdpFlows` (`WinDivertUdp`).

- **Only packets Sluice may rewrite** reach user mode (`CaptureFilter`). WinDivert can't redirect a whole connection,
  so each packet of a handled flow is rewritten, but nothing else is captured:
  - the **main handle** takes every new connection's first SYN (to decide it), the relay's packets back to apps, all
    TCP to the fake-address range 198.18.0.0/15 (those flows are always redirected), and UDP as the UDP mode needs
    (in "let it go direct" only DNS and UDP to the fake range, which `UdpFlows` sends direct by name, #208);
  - a **followers' handle** takes the later packets of redirected connections to *real* addresses (a rule matched an
    app or an IP, not a faked name). Its filter lists those addresses; `Interceptor.Follow` adds one and reopens the
    handle **before** that connection's SYN is sent on, and `PruneFollowed` drops addresses with no redirected flow left.
    The new handle opens first; the old one is shut down for receiving, drains its queue and closes itself. Over
    `CaptureFilter.MaxFollowed` (150) addresses, or if WinDivert rejects the list, it captures all later TCP packets.
  - So direct apps' traffic and the tunnel helpers' own connections to their servers stay in the kernel. Measured
    (10 MB download): Sluice handled 10,600 packets instead of 13,164 when captured, and 282 instead of 2,376 when the
    download went through the SOCKS listener ([windows/FIXES.md §3](windows/FIXES.md#3-windows-capture-seemed-slower-than-the-socks-listener--fixed)).
- Every handled packet still makes one round trip through one user-mode thread (`Interceptor.NetworkLoop`); app↔relay
  packets are MTU-sized because they go through the adapter path. Both are what batching or a WFP driver would improve.
- **New flows are decided off the packet thread.** `PacketReflector.DeferDecisions` hands a new flow's first SYN back as
  `Defer`; one of 4 worker threads (`Interceptor.DecisionLoop`) decides it (`CompleteDeferred`) and sends it on. A
  retransmitted SYN meanwhile is dropped (`Drop`). So a slow process lookup delays only that connection, not every packet.
- **Owner lookup:** the PID comes from the socket-layer `CONNECT` event (`_socketPids`). That event arrives on another
  handle, so a decision waits up to 3 ms for it before falling back to scanning the TCP table (`FindTcpOwner`).
- **WinDivert queue** is set to its maximum (16384 packets, 32 MB) instead of the default 4096 / 4 MB, so a burst while
  Sluice is busy isn't dropped silently.
- **Measuring:** at Verbose (the desktop app's default) the log shows a "Capture timing (5 s)" line: how long packets
  waited in WinDivert's queue (from WinDivert's capture timestamp), how long Sluice held each one, and how long new
  connections waited for their decision. Stopping capture logs the totals and how often the owner came from the socket
  event versus the TCP table (`LatencyStats`).
- **Relay and CPU** (both platforms, capture or SOCKS listener): every 5 s at Verbose a "Relay (5 s)" line gives reads
  per direction, bytes, average read size, reads under 4 KB, and Sluice's CPU time over the same 5 s (and per MB)
  (`RelayStats`, counted by `Relay`, logged by `RoutingEngine`). The relay's CPU follows the number of reads, so a
  tunnel delivering data in small pieces costs more per MB. Typical (SSH via sing-box, ~1.5 MB/s): ~30 KB per read,
  15–60 ms of CPU per MB.

**Tunnel health checks** (`TunnelBackends`): a connected tunnel is checked every 30 s by opening a test connection
through it (`ProbeTimeout` 6 s, half per target). Two failed checks in a row (or one after a real connection failed)
restart it. A failed check is **ignored while the tunnel delivered data in the last 10 s** (`BusyGrace`; the relay calls
`NoteData`): a tunnel filled by a download can take longer than the check allows to open a new channel, and restarting
it would reset every connection through it.

**Linux** (`src/Sluice.Platform.Linux/Capture/`):

1. `NftRules` installs the table `inet sluice`:
   - chain `queue_new` (filter/output, priority mangle) sends the first SYN of each new outbound TCP connection, and new
     UDP flows when UDP is handled, to NFQUEUE 1473 with `bypass` (traffic passes if Sluice is gone);
   - chain `to_sluice` (nat/output, priority dstnat) redirects packets carrying mark `0x5C1CE` to the relay port, and
     UDP/53 to the DNS port;
   - both skip `meta skgid <Sluice's group>` and loopback. Chain names must not be nft keywords (`redirect` once broke the
     ruleset), and the script must have LF line endings (a CRLF checkout once broke capture on a real PC).
2. `NfQueue` is a hand-written netlink NFQUEUE client (no libnetfilter_queue), on raw `AF_NETLINK` sockets via
   `Native/Libc.cs`; fail-open, verdicts with an optional mark.
3. `LinuxCapture.Decide`, on one of 4 worker threads (`VerdictWorkers`; the NFQUEUE thread only receives): TCP → owning PID (`LinuxProcessLookup`: the socket from sock_diag, its process from a cache
   or `/proc/<pid>/fd`) → `RoutingEngine.Classify` → mark and
   remember the original destination by the app's endpoint and the destination (`FlowRecords`); the relay finds it through
   conntrack (`Conntrack`, by the reply tuple), which also covers a source port renumbered after a clash. UDP → pass,
   drop, or take (tunnel, or Direct to a placeholder: accept with the UDP mark, for `TransparentUdp` and `UdpFlows`),
   cached 30 s.
4. Sluice's own sockets are recognised by group: at start the process calls `setresgid(gid, gid, gid)` (377294 or the
   next free GID); helpers it starts inherit it. Real and effective group must match, or the kernel strips the ambient
   capability that `nft` needs; the saved group must match too, or GTK refuses to start the file dialog (#28). On stop
   all three go back to what they were.
5. `LinuxDnsServer` listens on **127.0.0.1:P and [::1]:P** (replies must leave from the redirect address for conntrack to
   translate them back), answers faked names, and forwards the rest to the real upstreams from
   `/run/systemd/resolve/resolv.conf` (falling back to `/etc/resolv.conf`), skipping loopback stubs.
6. Rights: root, or `cap_net_admin,cap_setgid` (+ `cap_sys_ptrace,cap_dac_read_search` to see other users' processes)
   on the launcher `/opt/sluice/sluice`, which passes them on to the app; Sluice clears the passed-on set before it
   starts any other program (`Libc.ClearPassedOn`). `NftRules` raises `CAP_NET_ADMIN` alone as an ambient capability so
   the `nft` child keeps it, and lowers it again once `nft` has started (`Libc.LowerAmbient`), so nothing else Sluice
   starts gets it (#99).

**sing-box helper** (`src/Sluice.Core/Tunnels/`): `SingBoxConfig` turns a tunnel into a sing-box config,
`SingBoxSession` starts one helper per tunnel with a local SOCKS5 inbound. Configs hold secrets, so they are written to a
private per-user folder (`HelperConfigFolder`, not the temp folder, which users empty) and deleted once sing-box has
read them.
