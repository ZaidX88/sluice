# Windows: building and testing

Shared build and test notes: [../FEATURES.md](../FEATURES.md). There is no Windows release package yet
([../REQUIRED.md](../REQUIRED.md#4-windows-release-package)); run Sluice from a build, as below.

## Build and run

Needs Windows 10/11 x64 and the .NET 10 SDK. The first build downloads WinDivert 2.2.2-A and sing-box 1.14.4 into
`.deps/` (pinned, SHA-256 checked) and copies them next to the app.

```powershell
dotnet build src/Sluice.Desktop -f net10.0-windows10.0.19041.0
dotnet run --project src/Sluice.Desktop -f net10.0-windows10.0.19041.0
```

- **Capture needs administrator rights** (WinDivert loads a kernel driver). Start Sluice "as administrator", or press
  "Restart as administrator" in the banner that appears when you turn capture on.
- **Windows Firewall:** captured connections are redirected to Sluice's local relay, so Sluice must be allowed to receive
  incoming connections. If the firewall blocks it, Sluice shows a banner with an **Allow** button (it adds a rule with
  `netsh`).
- **Notifications** are titled "Sluice" with its icon through a Start Menu shortcut carrying the app ID "Sluice"; the app
  creates it on first start.
- The old WPF app (`src/Sluice.App`) still builds; it is being retired.

## Command line

```powershell
dotnet run --project src/Sluice.Cli -- --help
dotnet run --project src/Sluice.Cli -- myprofile.sluice --intercept      # as administrator
dotnet run --project src/Sluice.Cli -- check "vless://..."               # test a tunnel or share link
```

## Testing on Windows

- `dotnet test tests/Sluice.Tests` runs everything, including live tests against local sing-box servers.
- `Sluice.Desktop --screenshot <dir> [--theme dark]` renders every page and dialog off-screen (at 1× scale; see
  [../FIXES.md](../FIXES.md#5-avalonia-ui-gaps-small)).
- While your own Sluice capture is running, real-connection tests and NuGet restores go through it: see the pitfalls in
  [../FEATURES.md](../FEATURES.md#testing).
