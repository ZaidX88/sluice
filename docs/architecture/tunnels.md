# Tunnels

Part of the [architecture reference](../ARCHITECTURE.md). Paths are relative to `src/Sluice.Core/`.

A **tunnel** (`Profiles/Profile.cs`: `Tunnel`) is an outbound route rules can send traffic through. Never call it a
"proxy" in UI or code; the word survives only in `.ppx` XML and in protocol messages.

## Tunnel types

`TunnelType` has two families, and the engine treats them differently:

| Family | Types | Who speaks the protocol | How the engine reaches it |
|---|---|---|---|
| Simple (`TunnelTypes.IsSimple`) | `Socks4`, `Socks5`, `Http`, `Https` | Sluice itself, `Proxy/ProxyHandshake.cs` | TCP to the server, then the handshake |
| Encrypted (`TunnelBackends.Handles`) | `Ssh` | sing-box's SSH client (default) or SSH.NET inside Sluice | a session exposing a local SOCKS5 endpoint |
| | `Shadowsocks`, `VMess`, `VLess`, `Trojan`, `Hysteria2`, `Tuic` (`SingBoxConfig.Handles`) | a sing-box helper process | a session exposing a local SOCKS5 endpoint |

A `Tunnel` holds `Address`, `Port`, `Username`, `Password`, a `Label`, an `Enabled` switch, the `SubscriptionId` it came
from, and a case-insensitive `Settings` dictionary for everything protocol-specific (`uuid`, `method`, `flow`,
`security`, `sni`, `transport`, `publickey`, `privatekey`, `payload`, `sshclient`, `hostkey`, …; the full list of
sing-box keys is in the `SingBoxConfig` doc comment). Only `.sluice` profiles can store `Settings`.

A **turned-off** tunnel fails the rules that use it (`ProxyException("… is turned off")`) instead of letting traffic go
out directly; chains skip it.

## Chains

`TunnelChain` (`Simple`, `Redundancy`, `LoadBalancing`) with `ChainHop`s (tunnel id + enabled flag). How each type
connects is in [engine.md](engine.md#upstream-connections-proxy). Limits: an encrypted tunnel can only be the **first**
hop (its session would otherwise have to run through the earlier hops), and UDP can't be relayed hop by hop.

## Sessions: `Tunnels/TunnelBackends.cs`

`TunnelBackends` (one per `RoutingEngine`, as `RoutingEngine.Backends`) runs the client side of every encrypted tunnel.
Each running session implements the internal `IBackendSession` (`StartAsync`, `SocksEndPoint`, `Fingerprint`,
`IsAlive`, `ProcessId`).

```
 TunnelConnector / UdpNat / TunnelChecker
          │ GetLocalSocksAsync(tunnel)  (one start at a time per tunnel: SemaphoreSlim)
          ▼
 TunnelBackends ── session for tunnel.Id alive and same Fingerprint? ── yes ──▶ 127.0.0.1:port
          │ no
          ▼
 StartSessionAsync
   ├─ Ssh, sshclient = auto|singbox, sing-box present ─▶ SshSingBoxSession ─┐ login refused (e.g. keyboard-
   │                                                     (sing-box "ssh")  │ interactive only) and "auto"
   ├─ Ssh otherwise ──────────────────────────────────▶ SshSession ◀────────┘ (SSH.NET)
   └─ sing-box protocols ─────────────────────────────▶ SingBoxSession
          │
          ▼ VerifyOnStart: a test connection through it must work (sing-box listens even when the server is dead)
       Connected
```

`Fingerprint(tunnel)` is a SHA-256 of the connection settings (not the learned host key); a changed fingerprint
restarts the session. `AsLocalSocksAsync` returns a synthetic `Socks5` tunnel pointing at the local endpoint.

### Lifecycle

States (`SessionState`, reported through `StatusChanged` as `SessionStatus`): `NotStarted`, `Connecting`, `Connected`,
`NotResponding`, `Reconnecting`, `Failed`, `Idle`, `Disabled`. A monitor loop (`MonitorLoop`, every 2 s) applies:

| Rule | Default | Where |
|---|---|---|
| Start on first use, or when capture starts for the rules' tunnels | — | `GetLocalSocksAsync`, `RoutingEngine.WarmUpTunnels` |
| Each app connection holds a **lease**; a session with no leases and no use for `IdleTimeout` stops (`Idle`) and starts again on the next connection | 10 min (`AppSettings.TunnelIdleMinutes`, 0 = never) | `Lease`, `MonitorOnceAsync` |
| **Health check**: a SOCKS5 connect through the session to several targets (for SSH the server's own port 22, then 1.1.1.1:443, the server itself, a well-known site); one failure → `NotResponding` and a recheck in 5 s, two → restart. A failed check is **ignored while the tunnel carried data** in the last `BusyGrace`: a full tunnel can be too slow to open a new channel in time, yet works | every 30 s, 6 s timeout; 10 s grace | `ProbeSessionAsync`, `ProbeAsync`, `NoteData` (called by the relay as data arrives) |
| A real connection that times out or finds the local endpoint gone triggers an **urgent check** (at most one per 5 s); if it fails, restart at once | — | `ReportProblem` |
| **Retries** with growing delays while the tunnel is wanted; **give up** (`Failed`) after `MaxAttempts` failures in a row until the user presses Check or turns the tunnel off and on (`ResetFailures`) | 3 s, 10 s, 30 s, 60 s, 2 min; 5 attempts | `RecordFailure` |
| **Network change** (new Wi-Fi, IP change, resume): after 3 s of quiet, if addresses or gateways really changed, restart every running session | on (`RestartOnNetworkChange`) | `CheckNetworkChangeAsync` |
| Profile edits: removed, edited or turned-off tunnels' sessions close; turning a tunnel back on starts fresh | — | `PruneAsync` |

The desktop app turns state transitions into notifications (`EngineHost.OnTunnelStatus`, see
[desktop.md](desktop.md#notifications-and-banners)), only on real transitions so a retry loop does not flood the user.

## The sing-box helper

Decision record: sing-box (GPL-3.0) runs as a **separate process, one per tunnel**, never linked into Sluice.

- **`Tunnels/SingBoxConfig.cs`** builds the JSON: one `socks` inbound on `127.0.0.1:<free port>`, one outbound for the
  tunnel's protocol (`Outbound(tunnel)`: TLS / REALITY / uTLS, ws / gRPC / HTTP / HTTPUpgrade transports, Shadowsocks
  plugins, Hysteria2 obfs, TUIC congestion), `route.final = out`. `BuildSsh` does the same with an `ssh` outbound pinned
  to a host key. Xray-only features sing-box lacks (the `xhttp` transport, VLESS encryption other than `none`) are
  rejected with a clear error when the share link is read (`ShareLinks`).
- **`Tunnels/SingBoxSession.cs`** starts one helper:
  1. resolves the server with Sluice's resolver (sing-box would ask the OS, whose cache may hold fake answers) and
     passes one address, IPv4 preferred;
  2. writes the config to a **private folder** (`Directory.CreateTempSubdirectory("sluice-sing-box-")`, owner-only
     0700 on Linux, files 0600) because it holds passwords and keys; the folder is deleted at exit;
  3. runs `sing-box run -c <config> --disable-color`, records the PID in `SingBoxSession.RunningHelpers` before
     anything else (its own traffic must never be captured), and on Windows assigns it to a kill-on-close **job
     object** so it dies with Sluice even after a crash;
  4. waits up to 10 s for the SOCKS inbound to accept, then **deletes the config file**;
  5. collects the helper's output into the log and `RecentOutput` (used in error messages).
- The binary is `sing-box.exe` / `sing-box` next to the program (`TunnelBackends.SingBoxPath`), put there by the build
  ([build-and-test.md](build-and-test.md#build-time-dependencies-builddependenciestargets)) or the Linux installer.

## SSH

Two clients, chosen per tunnel by the `sshclient` setting (`auto` default, `singbox`, `sshnet`):

| | `SshSingBoxSession` (default) | `SshSession` (fallback) |
|---|---|---|
| Client | sing-box's SSH outbound (Go x/crypto/ssh) in a `SingBoxSession` | SSH.NET `SshClient` + `ForwardedPortDynamic` on `127.0.0.1` |
| Flow control | per channel: one slow connection does not stall the others | none: Sluice adds the elastic download buffer in `Relay` |
| Login check | sing-box logs in lazily, so the start opens one test channel; a refused login throws `SshLoginException` and `auto` falls back to SSH.NET | at connect |
| Host key | pinned by the full public key (`hostpubkey`), fetched once with an SSH.NET key exchange (`SshHostKey.FetchAsync`) and checked against the `hostkey` fingerprint | `HostKeyReceived` checks the `hostkey` fingerprint |
| PID | the helper's (always routed direct) | none: runs inside Sluice, whose own traffic is never captured |

**Trust on first use:** the first connection learns the host key and raises `TunnelBackends.HostKeyLearned`; the
desktop app saves `hostkey` and `hostpubkey` into the profile. A different key later fails the connection ("SSH host key
changed!").

**Transports** (`Tunnels/TunnelTransport.cs`, HTTP Injector's connection modes; `TransportOptions` from the tunnel's
settings): `Direct`, `Payload` (send an HTTP payload, optionally through a remote HTTP proxy, then SSH), `Tls` (TLS with a
chosen SNI, then SSH), `TlsPayload`, `PayloadTls`. Payloads support `[host]`, `[port]`, `[crlf]`, `[split]` and the other
HTTP Injector placeholders (`RenderPayload`). With any mode other than `Direct`, the SSH client connects to a local
`TransportBridge` that opens a fresh transport connection to the server for each incoming connection.

## Share links and subscriptions

- **`Tunnels/ShareLinks.cs`**: `Parse(link)` → `LinkResult(Link, Tunnel?, Error)` for `vless://`, `vmess://`,
  `trojan://`, `ss://`, `hysteria2://` / `hy2://`, `tuic://`, `ssh://`, `socks5://` / `socks4://`, `http(s)://`
  (v2rayN / NekoRay / Shadowrocket / HTTP Injector conventions). `ParseMany(text)` accepts a list of lines or a base64
  blob of lines (subscription bodies). `ToLink(tunnel)` writes a link back (the Tunnels page's "copy link").
- **`Tunnels/Subscriptions.cs`**: `ImportLinks` adds new tunnels (skipping duplicates); `AddSubscription`,
  `RemoveSubscription`, `UpdateAsync` → `FetchAsync` (HTTP GET sent direct with Sluice's resolver; Sluice's own traffic
  is never captured) → `Apply`. Refreshing keeps tunnel ids stable (matched by server identity, then by name) so rules and
  chains keep working; a body with no usable link changes nothing; removed tunnels turn their rules into Direct and drop
  out of chains (`RemoveTunnel`). New ids start at 100 (`NextTunnelId`). Results come back as an `ImportResult`.
