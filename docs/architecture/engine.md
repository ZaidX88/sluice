# The engine (`Sluice.Core`)

Part of the [architecture reference](../ARCHITECTURE.md). Everything here is UI-free and shared by every build.
Paths are relative to `src/Sluice.Core/`.

```
 Inbound/  ──InboundRequest──▶  Engine/RoutingEngine  ──RuleAction──▶  Proxy/TunnelConnector ──▶ remote
 (SOCKS5, transparent)            │  uses Rules/RuleEngine               │  uses Proxy/ProxyHandshake,
                                  │  logs to Engine/ConnectionTracker     │  Proxy/HappyEyeballs,
                                  │  pumps with Engine/Relay              │  Tunnels/TunnelBackends
                                  ▼
                      Engine/Statistics, Engine/FileLog
```

## Routing engine: `Engine/RoutingEngine.cs`

`RoutingEngine` is the heart of the app. One instance lives for the whole run (the desktop's `EngineHost` or the CLI
creates it) and is fed by every inbound listener and capture.

- **State is an immutable snapshot.** `_state` holds the `Profile`, a compiled `RuleEngine` and a `TunnelConnector`.
  `Reload(profile)` builds a new snapshot and swaps it in; connections already running keep the old one. Reload also
  calls `TunnelBackends.PruneAsync`, which closes sessions of tunnels that were removed, edited or turned off.
- **`Classify(InboundRequest)`** returns a `RuleMatch` (rule + action). Before the rules it applies two fixed rules:
  a sing-box helper started by Sluice (`Backends.IsHelperProcess`) always goes Direct ("Tunnel helper"), and, with
  `ConnectionLoopDetection`, a process that listens on the port of a tunnel whose address is on this machine goes
  Direct ("Loop prevention", `IsLocalProxyHost`, cached for 5 s). Then `RuleEngine.Match`.
- **`HandleAsync(socket, request, reply, ct)`** serves one application connection end to end and never throws (see the
  diagram in [ARCHITECTURE.md](../ARCHITECTURE.md#the-life-of-one-connection)). It refuses connections to Sluice's own
  listeners (`RegisterOwnEndpoint`, a loop guard), holds a lease on every encrypted tunnel of the route for the
  connection's life, and maps failures to `ConnectionState.Blocked / Failed / Closed`.
- **`WarmUpTunnels()`** starts the sessions of every encrypted tunnel that an enabled rule uses (directly or as a chain
  hop), so the first captured connection does not wait for an SSH login. Called when capture starts and after a profile
  edit while capturing.
- **`HostResolver`** is the resolver for names Sluice connects to itself; capture installs `DnsResolver.ResolveAsync`
  there (see [DNS](#dns-and-fake-ips)). Setting it rebuilds the snapshot and sets `Backends.Resolver`.
- **SSH routes** get a 16 MB elastic download buffer in `Relay` (`SshDownstreamBuffer`), because SSH.NET channels have
  no per-channel flow control.

### IPv6 fallback: `Engine/Ipv6Fallback.cs`

A tunnel whose server has no IPv6 can't open IPv6 destinations, and an app holding a real IPv6 address only falls back
to IPv4 after each attempt failed through the tunnel ([core FIXES §6](../core/FIXES.md)). `RoutingEngine.Ipv6Fallback`
remembers such routes:

- **Only Core marks.** `HandleAsync` reports every tunnel or chain connection that opened (`Opened`) and every one
  that failed before it opened (`FailedBeforeOpen`), keyed by the route text `Describe(action)` gives.
- **The evidence:** at least 2 different IPv6 addresses failed with a SOCKS5 reply 0x01 (general failure; sing-box
  and SSH.NET report an SSH "connect failed" this way), 0x03 or 0x04 (`ProxyException.Socks5Reply`) within 60 s;
  the route opened an IPv4 or hostname connection within those 60 s (so the tunnel itself works); and no IPv6
  connection through it opened meanwhile (an IPv6 success clears the evidence). "Refused", timeouts, a tunnel that is
  down and failures after the connection opened never count.
- **A mark lasts 10 minutes**, is announced once through the `Marked` event, and Core writes the log line ("refusing
  IPv6 through … for 10 minutes"). `Reload` forgets all marks.
- **The captures refuse:** for each new flow to an IPv6 address they ask `ShouldRefuse(route)` and, while it is true,
  reset it at once so the app's Happy Eyeballs moves to IPv4 (Linux: nft `reject with tcp reset`; Windows: a reset to
  the SYN). IPv4 and hostname targets are never refused.

`InboundRequest(Host, Port, ProcessId, ProcessPath)` is what every listener hands in. `Host` is a hostname when one is
known (SOCKS5 with a name, or a fake IP mapped back), otherwise the IP as text.

## Rules: `Rules/RuleEngine.cs`

Proxifier semantics: enabled rules are evaluated top to bottom and the **first match wins**; the rule named `Default`
always matches and is evaluated last wherever it sits. A rule matches when all of its non-empty fields match:

| Field (`Rule`) | Syntax | Compiled to |
|---|---|---|
| `Applications` | `chrome.exe; "C:\path\x.exe"; fire*` (env vars expanded; with a path separator the full path is matched, otherwise the file name) | wildcard regexes over `ProcessPath` |
| `Targets` | `*.example.com; 10.0.0.0/8; 10.1.0.0-10.5.255.255; 192.168.1.*; ::1; %ComputerName%; %SimpleHostnames%` | `HostWildcard`, `IpRange`, `SimpleHostnames` patterns |
| `Ports` | `80; 8000-9000` | port ranges |

Lists split on `;`, `,` or newlines, honouring double quotes. Host wildcards also match the IP's text form; IP ranges
also match a literal-IP host. The action is a `RuleAction`: `Direct`, `Block`, `Tunnel` (with a tunnel id) or `Chain`
(with a chain id). `Profile.CreateDefault()` gives the Proxifier defaults: `Localhost → Direct`, `Default → Direct`.

`RuleEngine.ParseTargets` is reused by `DnsPolicy` for the DNS exclusion list.

## Process lookup: `Platform/ProcessLookup.cs`

Rules by application need the PID and executable behind a socket. `ProcessLookup` is a static facade with
`FindTcpOwner`, `FindUdpOwner`, `FindListeners` and `GetProcessPath`. Its built-in implementation is Windows
(iphlpapi `GetExtendedTcpTable` / UDP table, `QueryFullProcessImageName`); a platform module can replace it by setting
`ProcessLookup.Backend` to an `IProcessLookup` — `PlatformServices.Use` installs the platform's lookup, on Linux `LinuxProcessLookup` (sockets
from the kernel's socket diagnostics, `/proc/net/*` as the fallback; processes from `/proc/<pid>/fd`). The Windows capture additionally learns PIDs directly from WinDivert's socket
layer; see [capture.md](capture.md).

## Inbound listeners: `Inbound/`

`InboundListener` is the shared accept loop: binds a TCP socket (dual-stack for IPv6), registers its endpoint with the
engine's loop guard, accepts, sets `NoDelay`, and runs each client on the thread pool through `ServeAsync`.

| Listener | Who uses it | How it learns the destination |
|---|---|---|
| `Socks5Inbound` | apps configured with Sluice as a SOCKS5 proxy (desktop: `127.0.0.1:1080` by default, `AppSettings.SocksPort`; CLI `--socks`) | SOCKS5 CONNECT (no auth; IPv4, IPv6 or a name). PID from `InboundListener.IdentifyClient` (owner of the loopback socket). Answers through `IInboundReply` with SOCKS reply codes |
| `TransparentInbound` | the capture relay: `[::]:<random>` on Windows; `127.0.0.1` and `[::1]` on one port on Linux (`LoopbackRelay`) | `IRedirectTable.TryGetOriginal(client endpoint)` → `OriginalDestination(Destination, ProcessId, Hostname)`. No handshake (`NoHandshake` reply): a failure just closes the socket |

`IRedirectTable` is implemented by `PacketReflector` (Windows, wrapped by `Interceptor.LateBoundTable` to add the
hostname of fake IPs) and by `LinuxCapture.FlowTable`.

## Connection tracking, statistics, logs: `Engine/`

- **`ConnectionTracker`** keeps the live `ConnectionEntry` list (PID, process, target, rule, `Via`, state, byte counters,
  a `CancellationTokenSource` for "Disconnect"), running totals, and the **log stream**: `Write(level, message)` raises
  `Log` for messages at or below `Level` (`Error < Normal < Verbose < Debug`). Events: `Opened`, `Closed`, `Log`.
  `DisconnectAll` + `WaitIdleAsync` are used on shutdown.
- **`Statistics`** folds closed connections into per-application / target / rule totals and adds the live ones at
  snapshot time.
- **`FileLog`** subscribes to the tracker's log and writes `Sluice-yyyy-MM-dd.log` files with its own level. It keeps
  the last `RetainDays` days of them, today included (default 7; 0 keeps everything): when it opens its first file and
  when it rolls over to a new day it deletes older `Sluice-yyyy-MM-dd.log` files in its folder, never other files.
- **`Relay.RunAsync`** pumps bytes both ways with 64 KB buffers, forwards half-closes, and tears down both sides when
  either fails. With `downstreamBuffer > 0` the download side reads into a bounded queue (`ElasticPumpAsync`) so a slow
  app cannot stall an SSH.NET session shared with other connections. Every read is counted in the engine's
  **`RelayStats`** (reads, bytes, reads under 4 KB, per direction); `RoutingEngine` logs them every 5 s at Verbose with
  Sluice's CPU time ("Relay (5 s)") and the totals when it is disposed.

## Upstream connections: `Proxy/`

- **`TunnelConnector.ConnectAsync(action, …)`** opens the upstream `TcpClient`:
  - `Block` throws `ConnectionBlockedException`; `Direct` resolves and connects (20 s timeout).
  - `Tunnel`: fails if the tunnel is turned off (no silent direct fallback), otherwise one hop.
  - `Chain` (`TunnelChain`, only enabled hops of enabled tunnels): `Simple` connects to the first hop and runs one
    handshake per hop; `Redundancy` tries hops in order with `RedundancyTimeoutSeconds`, skips a failed one for
    `RedundancyRecheckSeconds`, optionally falls back to Direct; `LoadBalancing` picks a random hop, sticky per PID with
    `LoadBalanceSamePerPid`.
  - An **encrypted first hop** (SSH or a sing-box protocol) is replaced by the local SOCKS5 endpoint of its session
    (`TunnelBackends.AsLocalSocksAsync`). Encrypted tunnels can only be the first hop of a chain. Timeouts and refused
    local endpoints are reported back (`ReportProblem`) so the session is checked at once.
- **`ProxyHandshake`** is the client side of SOCKS4/4a, SOCKS5 (with user/password; CONNECT and UDP ASSOCIATE) and HTTP
  CONNECT, each run over an already-connected stream.
- **`HappyEyeballs.ConnectAsync`** connects to the first of several addresses that answers: families interleaved, the
  next attempt started after 250 ms or at once on failure, late winners closed. Used for direct connections, tunnel
  servers and SOCKS5 UDP control connections.
- **`Socks5UdpAssociation`** is a SOCKS5 UDP ASSOCIATE session: a TCP control connection that keeps it alive plus a UDP
  socket connected to the server's relay; datagrams carry the real destination (or source, for replies).

## DNS and fake IPs

Files: `Dns/DnsResolver.cs`, `Dns/DnsPolicy.cs`, `Dns/DnsPolicyCache.cs`, `Dns/FakeIpPool.cs`, `Dns/DnsMessage.cs`, `Interception/DnsRedirector.cs`.
The flow is drawn in [ARCHITECTURE.md](../ARCHITECTURE.md#the-dns-path).

| Class | Role |
|---|---|
| `DnsPolicy` | Built from `ResolveOptions` + `LeakPreventionMode`. Faking is on with `ViaProxy` (the default since #167) or leak prevention ("auto-detect" counts as local). `ShouldFake(name)` honours the exclusion list (`OnlyFromListMode` inverts it) and never fakes `localhost`, `.localhost`, `.local`, `.arpa` or IP literals. `BlockNonATypes` answers other record types with no data. |
| `DnsPolicyCache` | The current `DnsPolicy` for a profile, rebuilt when the setting values change (`Settings` record), with a `Changed` event (new, old) so a capture can flush the system DNS cache. Used by both captures on every query, and checked by a timer (Windows every 5 s, Linux every second) so a change applies even when no app asks. |
| `FakeIpPool` | Maps names ↔ 198.18.0.0/15 (131 070 usable addresses, round-robin, the oldest recycled when full). `IsFake(ip)` is a static range check. `Save`/`Load` write `fake-dns.json` atomically (temp file + rename) under `FakeIpPool.DefaultStatePath` (`<ApplicationData>/Sluice/`); a damaged file is ignored. |
| `DnsRedirector` | Pure packet logic: `TryAnswer(ip packet)` (Windows) or `TryAnswerQuery(dns message)` (Linux server) returns a reply, or null to let the query go to a real resolver. Faked A → one fake address, TTL 5 s; AAAA / HTTPS / SVCB → no data (no IPv6 fakes, and those records can carry real address hints). Queries from `DnsResolver`'s own port are never touched. |
| `DnsResolver` | Sluice's stub resolver: UDP from one fixed local port to the system's DNS servers (`SystemDnsServers()`: active non-loopback interfaces, IPv4 first; Linux capture passes `LinuxDnsServer.UpstreamServers` instead), A then AAAA, 60 s cache, 3 s timeout per server. Falls back to the OS resolver (filtering out fake answers) when no server is known. |
| `DnsMessage` | Minimal RFC 1035 wire format: parse a one-question query, build answers and queries, read A/AAAA replies. |

The capture owns these objects (one `FakeIpPool`, `DnsResolver`, `DnsRedirector` per capture run); the engine only
sees `HostResolver`. When capture is off, fake IPs are not handed out and Sluice resolves through the OS.

## UDP

UDP is only captured while capture runs and the profile's `UdpMode` is not `Bypass`.
`RoutingEngine.ClassifyUdp(request)` returns a `UdpRoute`:

| Rule action | `UdpMode` | Verdict |
|---|---|---|
| Direct | any | `Pass` |
| Block | any | `Block` |
| Tunnel / Chain | `Bypass` | `Pass` (UDP goes direct) |
| Tunnel / Chain | `BlockAll` | `Block` |
| Tunnel / Chain | `BlockQuic` | `Block` for port 443, otherwise `Pass` |
| Tunnel / Chain | `Tunnel` (default) | `Tunnel` through a tunnel that carries UDP (SOCKS5 or a sing-box protocol, `CarriesUdp`); a simple chain only with exactly one hop; redundancy uses the first usable hop, load balancing one per PID. Otherwise `Block` |

**Windows: `Interception/UdpNat.cs`.** For each outbound datagram `Interceptor` calls `UdpNat.ProcessOutbound`. Flows
are keyed by (app socket, destination) and classified once (again after a profile change). Tunneled datagrams are taken
out of the network and sent through one `Socks5UdpAssociation` per app socket and tunnel (`RoutingEngine.OpenUdpAsync`,
which starts an encrypted tunnel's session and uses its local SOCKS5); replies are rebuilt as inbound packets from the
original destination (`UdpPacket`) and injected through the `Injector` delegate. Fake destinations are sent by name.
Multicast, broadcast, link-local, DNS and DHCP are never captured; idle flows end after 60 s; a failed association is
retried at most every 5 s. Each tunneled flow appears in the Connections list (`BeginUdp`).

**Linux:** UDP is not tunneled yet; `LinuxCapture` only passes or drops new flows (verdict cached 30 s), so tunneled
apps' UDP is blocked and browsers fall back from QUIC to TCP. Plan: [REQUIRED.md §2](../REQUIRED.md#2-udp-through-tunnels-on-linux).

## Health checking a tunnel by hand: `Checker/TunnelChecker.cs`

`TunnelChecker.CheckAsync` is the equivalent of Proxifier's proxy checker, used by the Tunnels page's Check button and
`Sluice.Cli check`: test 1 connects to the server (or starts an encrypted tunnel's session), test 2 connects through
it (optionally loading a web page, needed to prove an HTTP proxy works), test 3 measures latency. The result is a
`CheckReport` of `CheckStep`s with plain-language explanations.
