# Capture

Part of the [architecture reference](../ARCHITECTURE.md). The step-by-step internals (filters, nft chains, rights) are
kept in one place, [STATUS.md → How capture works](../STATUS.md#how-capture-works); this page explains how the pieces
fit and what each class is for.

Both captures implement `ITrafficCapture` (`src/Sluice.Core/Interception/ITrafficCapture.cs`): `Start()` (throws
`InvalidOperationException` with a readable reason), `NeedsRestartFor(profile)` (true when the UDP mode needs a
different filter), the `RelayUnreachable` event, and `DisposeAsync()` to stop. They are created by
`IPlatformServices.CreateCapture(engine)` ([platforms.md](platforms.md)) and owned by `EngineHost` (desktop) or the CLI.

They follow the same model:

1. See the **first packet of each new outbound flow** and nothing after it for flows left alone.
2. Find the owning **PID** and executable, then ask `RoutingEngine.Classify`.
3. Steer connections to handle into a local **`TransparentInbound` relay**, remembering each one's original destination
   in an `IRedirectTable`.
4. Answer **DNS** for faked names (`DnsRedirector`, `FakeIpPool`, `DnsResolver`; see
   [engine.md](engine.md#dns-and-fake-ips)), and restore/save `fake-dns.json`.
5. **Never touch Sluice's own traffic**, and **fail open**: if Sluice stops or crashes, traffic flows normally.

| | Windows | Linux |
|---|---|---|
| Class | `Interceptor` (`src/Sluice.Core/Interception/`, still in Core) | `LinuxCapture` (`src/Sluice.Platform.Linux/Capture/`) |
| Packet source | WinDivert driver (`WinDivertNative`): a socket-layer sniff handle (PIDs) and a network-layer handle (packets) | nftables table `inet sluice` (`NftRules`) queueing first SYNs to NFQUEUE 1473, read by `NfQueue` (raw netlink, `Native/Libc.cs`) |
| PID | WinDivert socket events (`processId`; waits up to 3 ms for the event), else iphlpapi (`ProcessLookup`) | `LinuxProcessLookup`: the socket's inode and uid from the kernel's socket diagnostics (`SockDiag`, `NETLINK_SOCK_DIAG`; `/proc/net` if unavailable), the process from a cache or `/proc/<pid>/fd` (the socket owner's newest processes first) |
| Steering to the relay | **Reflection**: `PacketReflector` rewrites the packets of a redirected flow and reinjects them inbound, in both directions, for the whole connection | **Mark + REDIRECT**: the verdict sets mark `0x5C1CE`; nftables' nat chain redirects marked SYNs to the relay port; the kernel does the rest |
| Original destination | `PacketReflector` flow table, keyed by (remote address, app port) | `LinuxCapture.FlowTable` over `FlowRecords`, keyed by (app endpoint, original destination); the relay looks the connection up in conntrack (`Conntrack`) by the reply tuple, so a source port renumbered after a clash still finds its record |
| DNS | `DnsRedirector.TryAnswer` on captured UDP/53 packets; an answer is injected, the query dropped. Unfaked queries pass | nftables redirects UDP/53 to `LinuxDnsServer` (127.0.0.1:P and [::1]:P); unfaked queries are forwarded to the real upstreams (`UpstreamServers`, skipping loopback stubs) |
| UDP | `UdpNat`: tunneled through SOCKS5 UDP associations | pass or drop only (tunneling planned, [REQUIRED.md §2](../REQUIRED.md#2-udp-through-tunnels-on-linux)) |
| Self-exclusion | filters skip Sluice's PID; helpers by PID in `Classify` | Sluice switches to a private group (`SluiceGroup.Pick`, `setresgid`) inherited by helpers; nftables skips `meta skgid` |
| Fail-open | WinDivert handles close with the process; packets with errors are re-sent unchanged | `queue … bypass`; on stop or start failure the table is removed first (`Cleanup`) |
| Rights | administrator (`Environment.IsPrivilegedProcess`) | root, or `CAP_NET_ADMIN` + `CAP_SETGID` (setcap); `NftRules` raises `CAP_NET_ADMIN` as an ambient capability so the `nft` child keeps it |

## Windows: `Interceptor`

```
  WinDivert socket layer (sniff) ──CONNECT events──▶ _socketPids (local port, remote) → PID
  WinDivert network layer, main handle ── new SYNs, relay port, fake range, UDP ──▶ NetworkLoop (own thread) → HandlePacket
  WinDivert network layer, followers' handle ── later packets to redirected real addresses ──▶ NetworkLoop (own thread)
        ├─ UDP: DnsRedirector.TryAnswer → inject reply ;  else UdpNat.ProcessOutbound → drop or re-send
        └─ TCP: PacketReflector.ProcessOutbound
               ├─ first SYN of a new flow → Defer: copy queued ──▶ DecisionLoop (4 worker threads)
               │       └─ CompleteDeferred → Interceptor.Decide(local, remote) → redirect? (Classify) → send
               │             (IPv6 on a route Core marked: TryRefuse injects a TCP reset instead of sending the SYN)
               ├─ repeated SYN while its decision is pending → Drop (the app retransmits it)
               └─ redirected flow → rewrite + reinject inbound to TransparentInbound ([::]:random)
```

- **Narrow filters** (`CaptureFilter`): only packets Sluice may rewrite are captured; direct traffic and the tunnel
  helpers' own connections never reach user mode. The two filters never match the same packet (followers exclude SYNs
  and the relay port; fake addresses are never followed). `Decide` calls `Follow(remote)` for a redirected real address,
  which reopens the followers' handle (alternating priorities 1 and 2, new handle first, old one shut down for
  receiving, drained and closed by its own thread) before the SYN is sent on. `SweepLoop` calls `PruneFollowed`
  (addresses with no redirected flow, added over a minute ago). Details: [STATUS.md](../STATUS.md#how-capture-works).
- **Decisions off the packet thread** (`PacketReflector.DeferDecisions`): process lookup and rules can take
  milliseconds, so a new flow's first SYN is handed to one of 4 `DecisionLoop` threads and every other packet keeps
  moving. If a decision throws, the SYN goes out unchanged. A decision pending for more than 5 s is treated as lost.
- **WinDivert queue** at its maximum (16384 packets / 32 MB, `WinDivertNative.QueueLengthMax` / `QueueSizeMax`)
  instead of the default 4096 / 4 MB, so a burst while Sluice is busy isn't dropped.
- **Timing counters** (`LatencyStats`): queue wait (WinDivert's capture timestamp to Sluice reading the packet),
  handling time per packet, decision time per new connection, and whether the owner came from the socket event or the
  TCP table. Logged every 5 s at Verbose and once as a summary when capture stops. Measurements and what limits speed:
  [windows/FIXES.md §3](../windows/FIXES.md#3-windows-capture-seemed-slower-than-the-socks-listener--fixed).
- Log strings for each connection are built only when the log level shows them.
- **IPv6 fallback** ([core FIXES §6](../core/FIXES.md), #31): a SYN to a real IPv6 address whose rule sends it
  through a tunnel or chain that Core marked (`RoutingEngine.Ipv6Fallback.ShouldRefuse(route)`, see
  [engine.md](engine.md#ipv6-fallback-engineipv6fallbackcs)) is neither redirected nor sent on: Sluice injects a
  reset (RST+ACK acknowledging the SYN, `BuildSynReset`) back to the app. Windows retries a connect() that got a reset,
  and the reflector passes those retries as an already decided flow, so `TryRefuse` answers every SYN of a refused flow
  (on the decision worker and on the packet thread) for 30 s; connect() fails with "connection refused" after about
  2 s and the app uses IPv4. Without that, the retries would go out directly. Windows never marks a route.

- `Decide` never redirects Sluice's own PID or DNS over TCP to a real server; always redirects fake IPs; skips
  connections with no PID unless `ProcessServices`; otherwise redirects anything that is not Direct (or everything with
  `HandleDirectConnections`).
- **Firewall detection:** reflected packets arrive at the relay as *incoming* connections, which Windows Firewall may
  drop. `SweepLoop` (every 5 s) checks: at least 3 flows redirected, 8 s since start, and the relay accepted none →
  log an error and raise `RelayUnreachable` once. The desktop app shows a banner with an "Allow" button that runs
  `EngineHost.AllowThroughFirewall` (a `netsh advfirewall` rule for this executable), only when the user presses it.
- `WarnAboutConflicts` logs when another traffic redirector is running (it would capture connections before WinDivert).
- `SweepLoop` also saves fake IPs, expires idle reflector flows (10 min) and UDP flows (60 s), and logs counters at Debug.
- Stopping (`DisposeAsync`) shuts all handles, joins the threads, stops the relay and `UdpNat`, saves fake IPs, removes
  `HostResolver`, and flushes the OS DNS cache if fakes were handed out.

## Linux: `LinuxCapture`

```
  nftables  queue_new (output, mangle) ── first SYN / new UDP flow ──▶ NFQUEUE 1473 ──▶ NfQueue.Receive (own thread)
                                                                                         │ VerdictWorkers.Post
                                                     bounded hand-off (1024) ── full ──▶ accept unmarked at once
                                                                                         │
            LinuxCapture.Decide (4 worker threads) ── TCP: DecideTcp → accept with mark 0x5C1CE or 0x5C1CF, or plain accept
                                                   └─ UDP: DecideUdpDrop → accept or drop (cached 30 s)
  nftables  to_sluice (nat output, dstnat) ── mark 0x5C1CE ──▶ redirect to relay port
                                           └─ UDP/53 ─────────▶ redirect to LinuxDnsServer port
  nftables  refuse (filter output) ── mark 0x5C1CF ──▶ reject with tcp reset
```

- **IPv6 fallback** ([core FIXES §6](../core/FIXES.md), #30): a SYN to a real IPv6 address whose rule sends it
  through a tunnel or chain that Core marked (`RoutingEngine.Ipv6Fallback.ShouldRefuse(route)`, see
  [engine.md](engine.md#ipv6-fallback-engineipv6fallbackcs)) gets mark `0x5C1CF`, and the `refuse` chain answers it
  with a TCP reset, so the app's connect() fails at once and Happy Eyeballs uses IPv4. Linux never marks a route.

- `StartCore` order matters: restore fake IPs, check rights and `nft`, switch to the private group, start
  `DnsResolver` (upstreams from `LinuxDnsServer.UpstreamServers`) and `LinuxDnsServer`, start the relay, open the queue,
  apply the nft rules, start the queue thread and the sweeper, flush systemd-resolved (`resolvectl flush-caches`).
  Any failure runs `Cleanup`, which removes the nft table before anything else.
- **Decisions off the packet thread** (`VerdictWorkers`, #83): the NFQUEUE thread only receives and hands packets to 4
  worker threads, so a slow decision (a program Sluice hasn't seen yet) doesn't hold up other programs' new connections.
  Verdicts go out by packet id in any order (`NfQueue.Verdict` is safe from several threads). When the hand-off is full,
  the packet is accepted unmarked at once (it goes direct, uncaptured) and one log line says so; the stats line counts
  them. The same flow decided twice at once (a retransmitted SYN) gets the same decision and logs once. `Cleanup` stops
  the receive thread, then the workers (queued packets get their verdicts), then closes the socket; a verdict after
  that is dropped quietly.
- `RelayUnreachable` is never raised: no firewall stands between a local REDIRECT and a loopback relay.
- The nft script is normalised to LF line endings (a CRLF checkout once broke capture), and chain names avoid nft
  keywords.
- Known limits (UDP of tunneled apps, DNS without a non-loopback upstream, `/proc` scan cost, root GUIs):
  [linux/FIXES.md §1](../linux/FIXES.md#1-linux-capture-limitations).
