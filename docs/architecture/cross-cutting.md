# Threading, errors, logging, security, debt

Part of the [architecture reference](../ARCHITECTURE.md).

## Threading and async model

Everything network-facing is `async`/`await` on the thread pool, with `CancellationToken`s throughout. The few
dedicated threads are blocking packet loops:

| Where | Runs on | Notes |
|---|---|---|
| WinDivert socket and network loops (`Interceptor.SocketLoop`, `NetworkLoop`) | two background `Thread`s | blocking `WinDivertRecv`; every outbound packet passes through `NetworkLoop`, so per-packet work must stay short. The SYN decision (`Decide`: PID lookup + `Classify`) runs inline here |
| NFQUEUE loop (`LinuxCapture.QueueLoop`) | one background `Thread` | only first packets of flows arrive, each needs a verdict before it moves on |
| Sweepers (`Interceptor.SweepLoop`, `LinuxCapture.SweepLoop`), tunnel monitor (`TunnelBackends.MonitorLoop`) | `PeriodicTimer` tasks | 5 s / 10 s / 2 s |
| Inbound accept loops, each accepted connection, `LinuxDnsServer` queries, UDP sends in `UdpNat` | thread-pool tasks (`Task.Run`, fire-and-forget with their own error handling) | |
| UI | Avalonia UI thread | see below |

Shared state follows a few patterns:

- **Immutable snapshots swapped atomically:** `RoutingEngine._state` (profile + compiled rules + connector) is a
  `volatile` record replaced on `Reload`; a connection reads it once and keeps it. Captures take their `DnsPolicy` from a
  `DnsPolicyCache`, which rebuilds it when the name-resolution setting values change (the Settings page edits the profile
  in place, so comparing the profile object is not enough, core/FIXES.md §5).
- **Concurrent collections and `Interlocked` counters** for flow tables, sessions, connection lists and statistics;
  plain `lock`s for small structures (`FakeIpPool`, `Statistics`, `FileLog`).
- **One start at a time per tunnel:** `TunnelBackends` holds a `SemaphoreSlim` per tunnel id around session start;
  health probes use an `Interlocked` flag so only one runs.
- **Cancellation:** each `ConnectionEntry` has its own `CancellationTokenSource` (the Disconnect button), linked with the
  listener's token; `Relay` cancels both directions when one fails.

**UI thread.** `EngineHost` and the view models run on the Avalonia UI thread. Engine events from other threads are
posted with `Dispatcher.UIThread.Post` (tunnel status, host keys, firewall warnings, notifications). The log is not
bound directly: `LogViewModel` queues lines in a `ConcurrentQueue` and a 1-second `DispatcherTimer` drains them and
refreshes the visible page. `Dialogs` runs Avalonia's asynchronous dialogs in a nested dispatcher frame so callers (and
Core callbacks such as `MasterPasswordProvider`) can stay synchronous.

## Error handling

- **Connections never throw out of the engine.** `RoutingEngine.HandleAsync` catches everything, answers the app
  (`IInboundReply.FailAsync`: a SOCKS5 error code, or closing the socket for captured connections), and records the
  outcome in the tracker (`Blocked`, `Failed`, or `Closed` for a deliberate disconnect).
- **Packet handling fails open.** An exception while handling a packet is logged and the packet is passed on unchanged
  (Windows re-sends it; Linux accepts it). If the Linux queue loop dies, it logs that connections now go direct.
- **Start-up fails loudly.** `ITrafficCapture.Start` throws `InvalidOperationException` with a sentence the UI shows in
  the banner (missing rights, `nft` missing, WinDivert driver problems mapped from Win32 error codes); a failed Linux
  start removes the nft table and restores the group before rethrowing.
- **Exception types:** `ProxyException` (tunnel handshake or session problems, shown to the user),
  `ConnectionBlockedException` (a Block rule), `TimeoutException` (with what timed out), `CryptographicFailure`
  (profile secrets), `FormatException` (profiles, share links), `SshLoginException` (internal: triggers the SSH.NET
  fallback).
- **Tunnels degrade, then stop.** Session failures are retried with back-off and end in `Failed` rather than hammering a
  dead server ([tunnels.md](tunnels.md#lifecycle)).

## Logging

There is one log stream: `ConnectionTracker.Write(level, message)`, levels `Error`, `Normal`, `Verbose`, `Debug`. The
tracker only raises its `Log` event up to its `Level`, so each consumer raises the level it needs:

| Consumer | Level |
|---|---|
| Log page (`LogViewModel`) | chosen in the page (default Normal); the tracker runs at Verbose in the app |
| File log (`FileLog`, Settings → log to file; on by default) | Normal, or Verbose with "Log detail: Detailed"; daily files `Sluice-yyyy-MM-dd.log` in `AppSettings.LogDirectory`; the last 7 days are kept (`FileLog.RetainDays`) |
| `--log-file DIR` (desktop or CLI) | Debug (CLI: `--log-file-level`) |
| CLI console | `--log` |

Debug adds per-SYN decisions, DNS answers, periodic capture counters and (CLI `--intercept --log debug`) raw packet
headers (`Interceptor.DiagnosticPackets`). Helper output (sing-box) is logged line by line ([core/FIXES.md §1](../core/FIXES.md#1-sing-box-output-logged-at-the-wrong-level)).

## Security points

- **Secrets at rest.** Profiles encrypt tunnel passwords and secret settings by the profile's mode
  ([profiles.md](profiles.md#password-encryption)); the default `Basic` is obfuscation only, so a `.sluice` file should be
  treated as secret unless a master password (or, on Windows, DPAPI) is used. The master password lives only in memory.
  `settings.json` and `fake-dns.json` hold no secrets (but `fake-dns.json` lists hostnames the user visited).
- **Secrets in helper configs.** sing-box configs contain passwords and keys: written to a per-user app folder
  (`HelperConfigFolder`; 0700, files 0600 on Linux), never the shared or temp folder, and deleted as soon as sing-box
  is listening.
- **Elevation.** Windows: the whole app runs as administrator to capture (relaunch through UAC). Linux: no root; the
  installer grants `cap_net_admin,cap_setgid` (plus optional caps to see other users' processes) with `setcap` to a
  small launcher (`/opt/sluice/sluice`), which starts the app with a fixed set of environment variables and passes the
  capabilities on as ambient ones; the .NET apps carry none (docs/linux/BUILD.md → The launcher). Sluice clears the
  passed-on set before it starts any program, and `CAP_NET_ADMIN` is passed on only to the `nft` child (raised right
  before it starts, lowered right after). Sluice changes its own group with
  `setresgid` while capturing (real, effective and saved group alike) and restores all three on stop. A copy run
  without the installer can get the same rights from the app (#69): `pkexec setcap` on the launcher next to it, only
  when root alone can change that folder and every folder above it (the launcher refuses to run otherwise, and
  nothing can be swapped while the password prompt is open). Never through
  `dotnet Sluice.Desktop.dll`, which would put them on `dotnet` itself.
- **Fail-open capture.** If Sluice exits or crashes, WinDivert's handles close and the nft queue rule has `bypass`, so
  the machine keeps its connectivity; the price is that traffic which rules sent through tunnels then goes **direct**.
  There is no kill switch. Inside a running Sluice the opposite holds: a turned-off or failed tunnel fails its
  connections instead of sending them direct, and UDP that cannot be tunneled is blocked.
- **Own traffic and loops.** Sluice's sockets (by PID on Windows, by group on Linux) and its sing-box helpers are never
  captured; connections to Sluice's own listeners are refused; processes hosting a profile tunnel locally go direct.
- **Listening sockets.** The local SOCKS5 server binds `127.0.0.1` without authentication, so any local process can use
  it. The capture relay binds `[::]:<random>` on Windows (reflected packets arrive addressed to the machine's own IP),
  closing clients it has no redirect record for, and needs an inbound firewall allow rule because reflected connections
  arrive as incoming. On Linux it binds only `127.0.0.1` and `[::1]` on one port (`LoopbackRelay`): nftables' redirect
  of a local connection goes to loopback, so nothing on the network can reach it.
- **Helper lifetime.** On Windows a kill-on-close job object ends helpers with Sluice. Linux has no such object for a
  running process, so `LinuxPlatform.TieChildProcess` sends the helpers SIGTERM when Sluice exits on a signal or an
  unhandled exception, and records them in a file in the helper folder, so the next Sluice ends the helpers of one
  killed with SIGKILL or crashed (`HelperTies`, [linux/FIXES.md §6](../linux/FIXES.md#6-sing-box-helpers-outlived-sluice--fixed)).
- **SSH host keys** are trusted on first use and pinned in the profile; a changed key fails the connection.
- **Supply chain.** WinDivert and sing-box are pinned to exact releases and SHA-256 checked at build and install time.

## Known architectural debt

Not restated here; the plan of record is:

- [REQUIRED.md §1](../REQUIRED.md#1-move-windows-only-code-out-of-core): Windows-only code still in `Sluice.Core`,
  and `Sluice.Cli` not multi-targeted.
- [REQUIRED.md §2](../REQUIRED.md#2-udp-through-tunnels-on-linux): UDP through tunnels on Linux (TPROXY).
- Known bugs, by area: [core](../core/FIXES.md) (helper log levels, machine-dependent tests, IPv6 through tunnels),
  [windows](../windows/FIXES.md) (WSL/VM DNS, fake answers after capture), [linux](../linux/FIXES.md) (capture
  limits), [ui](../ui/FIXES.md) (layout, UI gaps, log file).
