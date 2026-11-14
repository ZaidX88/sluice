# Status

Where Sluice stands and how it is put together. For what to do next see [REQUIRED.md](REQUIRED.md) (must-do),
[FIXES.md](FIXES.md) (known bugs) and [FEATURES.md](FEATURES.md) (ideas, with how to test).

Last updated: 2026-11-14.

## What works

Sluice captures other applications' connections and routes each one by rules: **Direct**, **Block**, or through a
**tunnel** (SSH, SOCKS4/5, HTTP(S), VLESS, VMess, Trojan, Shadowsocks, Hysteria2, TUIC) or a chain of tunnels.

| Area | Windows | Linux |
|---|---|---|
| Routing engine, rules, chains, `.sluice` profiles, `.ppx` (Proxifier) import/export, profile password encryption | ✅ | ✅ |
| Tunnels: SSH (sing-box client, SSH.NET fallback; direct / payload / TLS+SNI modes), SOCKS4/5, HTTP(S) | ✅ | ✅ |
| Tunnels through the sing-box helper: Shadowsocks (incl. 2022), VMess, VLESS (+REALITY/Vision), Trojan, Hysteria2, TUIC; ws / gRPC / HTTP / HTTPUpgrade transports | ✅ | ✅ |
| Share links and subscriptions | ✅ | ✅ |
| Capturing other apps' traffic | ✅ WinDivert | ✅ nftables + NFQUEUE |
| Fake DNS (198.18.0.0/15) so hostnames reach tunnels; kept across restarts (`fake-dns.json`) | ✅ | ✅ |
| UDP through tunnels (QUIC, games, calls) | ✅ | ❌ blocked for tunneled apps (see [REQUIRED.md](REQUIRED.md#2-udp-through-tunnels-on-linux)) |
| Tunnel lifecycle: on/off, idle stop, health checks, retries, restart on network change, notifications | ✅ | ✅ |
| Desktop app (Avalonia, light/dark, tray) | ✅ | ✅ (tray needs a StatusNotifier host) |
| Command line (`Sluice.Cli`) | ✅ | ✅ |
| Release package | ❌ not yet | ✅ app-only tarball + `install.sh` ([linux/INSTALL.md](linux/INSTALL.md)) |

**Not supported, by design:** Xray-only features that sing-box doesn't implement, namely the `xhttp` transport and
VLESS post-quantum encryption (`mlkem768x25519plus`). Sluice shows a clear error for such links.

**Tested against real servers** (2026-11-13, 79 servers from a public list): Shadowsocks (all ciphers incl. 2022),
VLESS REALITY / Vision / gRPC / WS-TLS, Trojan and VMess work. Hysteria2 / TUIC are verified end to end against local
sing-box servers on Windows and Linux. Two compatibility bugs found that way were fixed: SNI for CDN-fronted links,
Xray-only uTLS fingerprints.

## Projects

How the modules fit together and how each build picks its OS module: [ARCHITECTURE.md](ARCHITECTURE.md).

| Project | Target | Role |
|---|---|---|
| `src/Sluice.Core` | net10.0 | Everything shared: engine, tunnels, DNS, profiles, inbound listeners, the interfaces `Platform/IPlatformServices.cs`, `Interception/ITrafficCapture.cs` and `IProcessLookup` (in `Platform/ProcessLookup.cs`). **Still contains Windows-only code** (WinDivert interceptor, iphlpapi lookups, DPAPI, Job object); see [REQUIRED.md](REQUIRED.md#1-move-windows-only-code-out-of-core). |
| `src/Sluice.Platform.Windows` | net10.0-windows10.0.19041.0 | Toast notifications (Start Menu shortcut with AUMID "Sluice"); creates the WinDivert `Interceptor`. |
| `src/Sluice.Platform.Linux` | net10.0 | notify-send notifications, `/proc` process lookup, nftables/NFQUEUE capture (`Capture/`), loopback DNS server. |
| `src/Sluice.Desktop` | net10.0-windows…; net10.0 | The Avalonia app. `#if WINDOWS` in `Services/Platform.cs` picks the platform module. |
| `src/Sluice.Cli` | net10.0 | Headless engine; `--intercept` uses WinDivert on Windows and `LinuxPlatform` on Linux. |
| `src/Sluice.App` | net10.0-windows | The old WPF app, kept until the Avalonia app is checked against it ([REQUIRED.md](REQUIRED.md#3-retire-the-wpf-app)). |
| `tests/Sluice.Tests` | net10.0 | xUnit, 258 tests; references Core and the Linux module. |
| `build/Dependencies.targets` | — | Fetches WinDivert and sing-box on the first build (pinned, SHA-256 checked) into `.deps/`. |
| `packaging/linux`, `scripts/` | — | Linux release package and installer; live-test helpers. |

## How capture works

**Windows** (`src/Sluice.Core/Interception/`): WinDivert sees each new outbound connection's first packet. `Interceptor`
finds the owning process (iphlpapi), asks `RoutingEngine.Classify`, and reflects connections to handle into the local
`TransparentInbound` relay (`PacketReflector`). UDP/53 queries are answered with fake addresses by `DnsRedirector`; other
UDP of tunneled apps goes through `UdpNat`.

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
3. `LinuxCapture.OnPacket`: TCP → owning PID from `/proc` (`LinuxProcessLookup`) → `RoutingEngine.Classify` → mark and
   remember the original destination by the app's endpoint (REDIRECT keeps the source). UDP → drop or pass, cached 30 s.
4. Sluice's own sockets are recognised by group: at start the process calls `setresgid(gid, gid, -1)` (377294 or the next
   free GID); helpers it starts inherit it. Real and effective group must match, or the kernel strips the ambient
   capability that `nft` needs.
5. `LinuxDnsServer` listens on **127.0.0.1:P and [::1]:P** (replies must leave from the redirect address for conntrack to
   translate them back), answers faked names, and forwards the rest to the real upstreams from
   `/run/systemd/resolve/resolv.conf` (falling back to `/etc/resolv.conf`), skipping loopback stubs.
6. Rights: root, or `cap_net_admin,cap_setgid` (+ `cap_sys_ptrace,cap_dac_read_search` to see other users' processes) on
   the binary. Under setcap, `NftRules` raises `CAP_NET_ADMIN` as an ambient capability so the `nft` child keeps it.

**sing-box helper** (`src/Sluice.Core/Tunnels/`): `SingBoxConfig` turns a tunnel into a sing-box config,
`SingBoxSession` starts one helper per tunnel with a local SOCKS5 inbound. Configs hold secrets, so they are written to a
private (0700) temp folder per process and deleted once sing-box has read them.
