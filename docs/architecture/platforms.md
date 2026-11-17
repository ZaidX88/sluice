# Platforms: how Windows and Linux share one app

Sluice is one codebase on one branch. Shared logic lives in one place; everything that differs between operating
systems sits in a small per-OS module behind one interface. Each OS gets its own build, and which OS module goes into
a build is decided **when compiling**, not while running.

Part of the [architecture reference](../ARCHITECTURE.md). What each module does inside: [engine.md](engine.md),
[tunnels.md](tunnels.md), [capture.md](capture.md); the remaining clean-up is in
[REQUIRED.md §1](../REQUIRED.md#1-move-windows-only-code-out-of-core).

## Modules

```
Sluice.Core               shared: routing engine, rules, tunnels (SSH, sing-box), DNS, profiles, .ppx import,
                          and the interfaces the OS modules implement (IPlatformServices, ITrafficCapture, IProcessLookup)
Sluice.Platform.Windows   Windows only: toast notifications, Start Menu shortcut, creates the WinDivert capture
Sluice.Platform.Linux     Linux only: notify-send, /proc process lookup, nftables + NFQUEUE capture, DNS server
Sluice.Desktop            the Avalonia UI, the same for both
Sluice.Cli                command-line engine
```

Rules for new code:

- UI-free logic goes in `Sluice.Core`; UI in `Sluice.Desktop`.
- Anything OS-specific goes in a platform module, reached through `IPlatformServices` (or a narrower interface it
  hands out). The UI and the engine never ask "am I on Windows?".

## 1. One project, two builds

`src/Sluice.Desktop/Sluice.Desktop.csproj` lists two target frameworks:

```xml
<TargetFrameworks>net10.0-windows10.0.19041.0;net10.0</TargetFrameworks>
```

- **`net10.0-windows10.0.19041.0`**: .NET 10 on Windows, with Windows-only APIs (toast notifications). The Windows build.
- **`net10.0`**: plain .NET 10. Used for the Linux build.

`dotnet build` compiles the project once per target. Pick one with `-f`, e.g.
`dotnet build src/Sluice.Desktop -f net10.0`.

## 2. Each build references one OS module

```xml
<ItemGroup Condition="$(TargetFramework.Contains('-windows'))">
  <ProjectReference Include="..\Sluice.Platform.Windows\Sluice.Platform.Windows.csproj" />
</ItemGroup>
<ItemGroup Condition="!$(TargetFramework.Contains('-windows'))">
  <ProjectReference Include="..\Sluice.Platform.Linux\Sluice.Platform.Linux.csproj" />
</ItemGroup>
```

The condition is evaluated per build: the Windows build references only `Sluice.Platform.Windows`, the Linux build
only `Sluice.Platform.Linux`. The other module is not part of that build at all.

## 3. `#if WINDOWS` creates the right class

For every `-windows` target the .NET SDK defines the compiler symbol `WINDOWS`. `src/Sluice.Desktop/Services/Platform.cs`:

```csharp
public static IPlatformServices Current { get; } =
#if WINDOWS
    new Sluice.Platform.Windows.WindowsPlatform();
#else
    new Sluice.Platform.Linux.LinuxPlatform();
#endif
```

`#if` is resolved before compiling: the branch that does not apply is thrown away, so each build only ever sees one
line. This is required, not just tidy: the Linux build doesn't reference the Windows module, so `WindowsPlatform`
does not exist there, and a run-time `if (OperatingSystem.IsWindows())` would not compile.

```
                     ┌─ Windows build ─ WINDOWS defined ─ Platform.Windows ─ WindowsPlatform
Sluice.Desktop ──────┤
(UI + Core, shared)  └─ Linux build ─── no WINDOWS ────── Platform.Linux ─── LinuxPlatform
```

## 4. The interface: `IPlatformServices`

`src/Sluice.Core/Platform/IPlatformServices.cs` lists everything that differs between operating systems. Both
classes implement it; the rest of the app only sees `OsPlatform.Current`, typed as the interface.

| Member | `WindowsPlatform` | `LinuxPlatform` |
|---|---|---|
| `Name` | `"Windows"` | `"Linux"` |
| `OnAppStart()` | `StartMenuShortcut.Ensure()`: registers the app ID "Sluice" and a Start Menu shortcut carrying it, so notifications show Sluice's name and icon | Points Core's process lookup ("which app owns this connection") at `/proc` (`LinuxProcessLookup`) |
| `Notify(title, message, isError)` | A Windows toast under the app ID "Sluice"; errors use the "urgent" scenario | `notify-send --app-name=Sluice …` (freedesktop notifications); errors use "critical" urgency |
| `CaptureUnavailableReason` | Always `null`: WinDivert ships with Sluice | "install nftables" when `nft` is missing |
| `HasCaptureRights` | Running as administrator? | Root, **or** `CAP_NET_ADMIN` + `CAP_SETGID` in the effective set (read from `/proc/self/status`) |
| `CaptureRightsHint` | "…needs administrator rights." | "…needs root, or these capabilities…" with the exact `setcap` command |
| `CanRestartElevated` | `true`: relaunch through UAC | `false`: a desktop app can't relaunch itself as root |
| `CreateCapture(engine)` | `new Interceptor(engine)`: WinDivert | `new LinuxCapture(engine, …)`: nftables + NFQUEUE |

`CreateCapture` is the big one. Both captures do the same job (catch each new connection, ask the routing engine for
Direct / Block / tunnel, hand the connection over) with entirely different OS mechanisms. Their internals are in
[capture.md](capture.md).

## 5. Where the shared app calls it

None of these callers knows which class it is talking to.

| When | Where | Calls |
|---|---|---|
| App starts | `Sluice.Desktop/App.axaml.cs` | `OnAppStart()` |
| Tunnel connects or drops; app minimised to tray | `App.axaml.cs`, `Services/AppTray.cs` | `Notify(...)` |
| Start capture | `Services/EngineHost.cs` | `HasCaptureRights`, `CaptureRightsHint`, `CreateCapture(Engine)` |
| Banner when rights are missing | `ViewModels/MainViewModel.cs` | `CaptureRightsHint`; shows "Restart as administrator" only if `CanRestartElevated` |
| Capture impossible on this system | `Services/EngineHost.cs` | `CaptureUnavailableReason` |

Example, pressing **Start capture**:

- **Windows:** `HasCaptureRights` checks for administrator. Missing: the banner offers "Restart as administrator".
  Otherwise `CreateCapture` returns `Interceptor`, which opens WinDivert.
- **Linux:** `HasCaptureRights` checks for root or the capabilities. Missing: the banner shows the `setcap` hint, no
  restart button. Otherwise `CreateCapture` returns `LinuxCapture`, which installs the nftables rules and reads NFQUEUE.

The button, the banner and the rules engine are the same code on both; only the object created at start-up differs.

## Adding an OS difference or a new OS

- **A new difference** (e.g. tying helper processes to Sluice's lifetime): add a member to `IPlatformServices` (or a
  narrower interface it returns), implement it in both classes, and call it through `OsPlatform.Current`.
- **A new OS** (e.g. macOS): a new module with `MacPlatform : IPlatformServices`, a target framework and project
  reference for it in `Sluice.Desktop.csproj`, and one more branch in `Platform.cs`. Nothing else changes.

## Not finished yet

- **Windows capture code still lives in `Sluice.Core`** (`Interceptor`, `WinDivertNative`, iphlpapi process lookup,
  DPAPI, the Job object), guarded by `OperatingSystem.IsWindows()` checks. Core is built once for both, so this code is
  also compiled into the Linux build; it is never called there (`LinuxPlatform.CreateCapture` uses nftables), but it
  should move to `Sluice.Platform.Windows`. The plan is in [REQUIRED.md §1](../REQUIRED.md#1-move-windows-only-code-out-of-core).
- **`Sluice.Cli` is not multi-targeted yet**: it picks its capture with a run-time `OperatingSystem.IsLinux()` branch
  instead of the platform module (same section of REQUIRED.md).
