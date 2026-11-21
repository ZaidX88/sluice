# Architecture

How Sluice is built, read from the code. Start here for the big picture; each part has its own page in
[architecture/](architecture/). For what works today see [STATUS.md](STATUS.md); for what is planned,
[REQUIRED.md](REQUIRED.md) and each area's `FIXES.md` and `FEATURES.md`
([README.md](README.md)).

| Page | Covers |
|---|---|
| This page | The modules, the life of one connection, the DNS path |
| [architecture/platforms.md](architecture/platforms.md) | How Windows and Linux share one app: two builds, `IPlatformServices`, `#if WINDOWS` |
| [architecture/engine.md](architecture/engine.md) | `Sluice.Core` engine: routing and rules, inbound listeners, connection tracking, relay, DNS and fake IPs, UDP, happy eyeballs |
| [architecture/tunnels.md](architecture/tunnels.md) | Tunnel types, chains, SSH, the sing-box helper, share links and subscriptions, tunnel lifecycle |
| [architecture/capture.md](architecture/capture.md) | Capture on Windows (WinDivert) and Linux (nftables + NFQUEUE) |
| [architecture/profiles.md](architecture/profiles.md) | `.sluice` profiles, password encryption, app settings |
| [architecture/desktop.md](architecture/desktop.md) | The Avalonia app: `App`, `EngineHost`, view models, tray, single instance, notifications, banners |
| [architecture/build-and-test.md](architecture/build-and-test.md) | `Sluice.Cli`, the tests, the build-time dependency fetch, packaging |
| [architecture/cross-cutting.md](architecture/cross-cutting.md) | Threading, errors and logging, security, known debt |

All paths below are relative to `src/` unless they start with another top-level folder.

## Modules

```
 front ends     Sluice.Desktop (Avalonia: App, EngineHost, view models)     Sluice.Cli (run / check / import / update)
                     │ one of the two, chosen at compile time                  │ run-time OS check
                     ▼                                                         ▼
 OS modules     Sluice.Platform.Windows                 Sluice.Platform.Linux
                  WindowsPlatform                         LinuxPlatform, LinuxProcessLookup
                  StartMenuShortcut (toasts)              Capture/: LinuxCapture, NftRules, NfQueue, LinuxDnsServer
                     │  both implement IPlatformServices and use Core
                     ▼
 shared         Sluice.Core
                  Engine/        RoutingEngine, ConnectionTracker, Relay, Statistics, FileLog
                  Rules/         RuleEngine
                  Inbound/       InboundListener, Socks5Inbound, TransparentInbound
                  Proxy/         TunnelConnector, ProxyHandshake, HappyEyeballs, Socks5UdpAssociation
                  Tunnels/       TunnelBackends, SingBoxConfig, SingBoxSession, SshSingBoxSession, SshSession,
                                 TunnelTransport, ShareLinks, Subscriptions
                  Dns/           DnsResolver, DnsPolicy, FakeIpPool, DnsMessage
                  Interception/  ITrafficCapture, Interceptor + WinDivertNative (Windows), PacketReflector,
                                 DnsRedirector, UdpNat
                  Profiles/      Profile, SluiceProfileSerializer, ProxifierPasswordCodec
                  Platform/      IPlatformServices, ProcessLookup
                  Checker/       TunnelChecker

 outside the process: sing-box helpers (one per encrypted tunnel), the WinDivert driver, the nft command
```

`Sluice.Core` has no UI and is shared by every build. The platform modules implement `IPlatformServices`; the desktop
app picks one at compile time ([platforms.md](architecture/platforms.md)). Some Windows-only code still sits in Core
(`Interceptor`, `WinDivertNative`, the iphlpapi part of `ProcessLookup`, `Dpapi`, the job object in `SingBoxSession`);
see [REQUIRED.md §1](REQUIRED.md#1-move-windows-only-code-out-of-core).

## The life of one connection

An application on the machine opens a TCP connection. It never knows Sluice exists.

```
 app ──connect(host or IP, port)──▶ OS network stack
                                      │
          ┌───────────────────────────┴─────────────────────────────┐
          │ capture (first packet of each new flow)                 │   or: the app uses Sluice's
          │  Windows: WinDivert → Interceptor → PacketReflector     │   local SOCKS5 server directly
          │  Linux:   nftables queue → NfQueue → LinuxCapture       │   (Socks5Inbound, port 1080)
          └───────────────────────────┬─────────────────────────────┘               │
                                      │ owning PID (iphlpapi / /proc)               │
                                      ▼                                             │
                   RoutingEngine.Classify(InboundRequest)                           │
                   = RuleEngine.Match: first enabled rule that matches              │
                     application + target + port; "Default" last                    │
                                      │                                             │
              Direct and not handled  │   anything else (or a fake IP)              │
            ◀── packet passes, never ─┤                                             │
                touched again         ▼                                             │
                   connection steered to the local relay                            │
                   (TransparentInbound on a random port)                            │
                                      │  IRedirectTable gives back the original     │
                                      │  destination, PID and (for fake IPs) the    │
                                      │  hostname                                   │
                                      ▼                                             ▼
                         RoutingEngine.HandleAsync(socket, InboundRequest, IInboundReply)
                           ├─ stale fake IP? reset at once
                           ├─ Classify again → ConnectionTracker.Begin (shows in Connections)
                           ├─ lease the route's encrypted tunnels (TunnelBackends.Lease)
                           ├─ TunnelConnector.ConnectAsync(action)
                           │     Block   → ConnectionBlockedException
                           │     Direct  → resolve (DnsResolver) → HappyEyeballs → remote
                           │     Tunnel  → ProxyHandshake through one hop
                           │     Chain   → simple (hop by hop) / redundancy / load balancing
                           │     encrypted first hop → its local SOCKS5 endpoint
                           │       (sing-box helper or SSH.NET forward, run by TunnelBackends)
                           ├─ reply.SuccessAsync (SOCKS5 answer, or nothing for captured flows)
                           └─ Relay.RunAsync: bytes both ways until both sides close
                                      │
                                      ▼
                     ConnectionTracker.End → Statistics, log, Connections pane
```

Key points:

- **The decision is made once, on the first packet.** Direct connections that need no handling are never seen again
  by Sluice; only connections that are blocked, tunneled, or aimed at a fake IP go through the relay (all of them
  with the profile option `HandleDirectConnections`).
- **Sluice's own traffic is never captured:** on Windows the WinDivert filters exclude Sluice's PID; on Linux nftables
  skips sockets of Sluice's private group. sing-box helpers are recognised too (`TunnelBackends.IsHelperProcess`) and
  always go direct, as do programs that host a tunnel of the profile on this machine (loop prevention).
- **Encrypted tunnels look like SOCKS5 to the engine.** `TunnelBackends` runs one session per tunnel and exposes it on
  `127.0.0.1:<port>`; `TunnelConnector` swaps the tunnel for that endpoint and speaks plain SOCKS5 to it.
- UDP takes a different path: per-flow decisions in `UdpNat` (Windows) or a drop/pass verdict (Linux). See
  [engine.md](architecture/engine.md#udp).

## The DNS path

Rules can name hosts (`*.example.com`), but a captured packet only carries an IP address. Sluice keeps names intact
with **fake IPs**: when the profile resolves names through tunnels (`ResolveOptions.ViaProxy` or leak prevention), DNS
queries for such names are answered locally with a placeholder address from 198.18.0.0/15, and the name is remembered.

```
 app: "where is example.com?" (UDP/53)
        │  captured: Windows → Interceptor sees the packet; Linux → nftables redirects it to LinuxDnsServer
        ▼
 DnsRedirector.TryAnswerQuery ── DnsPolicy.ShouldFake(name)?
        │ yes                                   │ no
        ▼                                       ▼
 A     → FakeIpPool.GetOrAdd(name) = 198.18.x.y     Windows: the query passes to the real DNS server
 AAAA, HTTPS, SVCB → empty answer                 Linux:   LinuxDnsServer forwards it to the upstreams
 other types → empty answer only with BlockNonATypes          and relays the answer
        │
        ▼
 app connects to 198.18.x.y:443 ── capture: fake IP → always handled
        │
        ▼
 FakeIpPool.TryGetHost(198.18.x.y) = "example.com" → InboundRequest.Host = "example.com"
        │
        ▼
 rules match the name; a tunnel receives the name (the tunnel server resolves it);
 a Direct route resolves it with Sluice's own DnsResolver
```

- **`DnsResolver`** is Sluice's own stub resolver. It talks UDP to the system's real DNS servers from one fixed local
  port, which `DnsRedirector` recognises (`ownResolverPort`) and never fakes, so Sluice itself always gets real answers,
  bypassing the OS cache that holds fake ones. While capture runs it is installed as `RoutingEngine.HostResolver`, which
  also feeds `TunnelConnector` and `TunnelBackends.Resolver` (tunnel server names).
- **Fake IPs outlive Sluice in apps' caches**, so `FakeIpPool` is saved to `fake-dns.json` in the settings folder
  every few seconds (5 s on Windows, 10 s on Linux) and on stop, and restored on the next start. A fake IP that is
  still unknown (an app's cache from before a reset) is reset at once (`RoutingEngine.HandleAsync`), so the app looks
  the name up again.
- `.localhost`, `.local`, `.arpa`, `localhost` and IP literals are never faked; the profile's exclusion list is left
  alone too, or with `OnlyFromListMode` it is the only list faked (`DnsPolicy`). DNS over TCP to a real server is
  never tunneled (`Interceptor.Decide`, `LinuxCapture.DecideTcp`).
- The OS DNS cache is flushed when capture starts (on Windows only with faking on) and when it stops with fakes
  handed out: `DnsFlushResolverCache` on Windows, `resolvectl flush-caches` on Linux.

Details: [engine.md](architecture/engine.md#dns-and-fake-ips) and [capture.md](architecture/capture.md).
