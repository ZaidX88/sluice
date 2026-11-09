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
| 3 | Transparent interception with WinDivert | next |
| 4–8 | DNS through proxy, password encryption, proxy checker, WPF UI, extras | planned |

## Layout

```
src/Sluice.Core    engine: profiles, rules, proxy protocols, relay, listeners (no UI)
src/Sluice.Cli     console host
tests/Sluice.Tests xUnit tests with in-process SOCKS/HTTP test servers
```

## Try it

```bash
dotnet test
```

```bash
dotnet run --project src/Sluice.Cli -- "%APPDATA%\Proxifier4\Profiles\Default.ppx" --socks 127.0.0.1:1080 --log verbose
```

```bash
curl --socks5-hostname 127.0.0.1:1080 https://example.com/
```
