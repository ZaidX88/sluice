# Sluice

A rule-based proxy client for Windows. Sluice decides, per application, host and port, whether
traffic goes direct, gets blocked, or flows through a proxy or proxy chain.

Built with C# / .NET 10. Interception will use WinDivert, and the UI will be WPF. Profiles are
compatible with Proxifier 4 `.ppx` files. See [ANALYSIS.md](ANALYSIS.md) for the reference
analysis and the build plan.

## Status

| Step | | |
|---|---|---|
| 1 | Profiles (`.ppx`), rule engine, SOCKS4/4a/5 and HTTPS proxy clients, chains | done |
| 2 | Engine and relay, connection tracking, local SOCKS5 listener, process detection, CLI | done |
| 3 | Transparent TCP interception with WinDivert (`--intercept`, needs Administrator) | done |
| 4 | DNS through proxy: fake IPs (198.18.0.0/15), hostname rules for intercepted traffic, loop prevention | done |
| 5 | Profile password encryption, byte-compatible with Proxifier (disabled / basic / account / master) | done |
| 6 | Proxy checker (3 tests), statistics per app/target/rule, daily file log, clean shutdown | done |
| 7 | WPF app: Fluent light/dark UI, connections, statistics, log, proxies (+checker), rules, settings, tray | done |
| 8 | Extras: chains editor, UDP via SOCKS5, NTLM/Kerberos, service mode, profile auto-update | planned |

![Rules](docs/screenshots/rules-light.png)
![Proxies, dark theme](docs/screenshots/proxies-dark.png)

## Layout

```
src/Sluice.Core    engine: profiles, rules, proxy protocols, relay, listeners (no UI)
src/Sluice.App     WPF desktop app (Sluice.exe)
src/Sluice.Cli     console host
tests/Sluice.Tests xUnit tests with in-process SOCKS/HTTP test servers
```

## Try it

```bash
dotnet test
```

The desktop app opens your last profile and listens for SOCKS5 on 127.0.0.1:1080. Turn on **Capture traffic** to
route every app (it asks to restart as administrator when needed):

```bash
dotnet run --project src/Sluice.App
```

Render every page to PNG (demo profile, no admin rights, nothing saved):

```bash
dotnet run --project src/Sluice.App -- --screenshot shots --theme dark
```

```bash
dotnet run --project src/Sluice.Cli -- "%APPDATA%\Proxifier4\Profiles\Default.ppx" --socks 127.0.0.1:1080 --log verbose
```

```bash
curl --socks5-hostname 127.0.0.1:1080 https://example.com/
```

Transparent mode captures every application's TCP traffic, with no proxy settings needed in the apps.
Run it from an Administrator terminal, and quit Proxifier or any other redirector first:

```bash
dotnet run --project src/Sluice.Cli -- my-profile.ppx --intercept --log verbose
```

Check proxies like Proxifier's Proxy Checker: test a whole profile or a single proxy URL:

```bash
dotnet run --project src/Sluice.Cli -- check my-profile.ppx
```

```bash
dotnet run --project src/Sluice.Cli -- check socks5://user:pass@proxy.example:1080 --target example.com:80
```

WinDivert 2.2.2 (x64) is vendored in `lib/WinDivert` (LGPLv3 / GPLv2) and copied next to the build output.
