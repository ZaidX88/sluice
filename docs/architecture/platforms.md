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
| `OnAppStart()` | `StartMenuShortcut.Ensure()`: registers the app ID "Sluice" and a Start Menu shortcut carrying it, so notifications show Sluice's name and icon | Nothing (the `/proc` process lookup is `LinuxPlatform.ProcessLookup`, set by `PlatformServices.Use`) |
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
  narrower interface it returns), implement it in both classes, and call it through `OsPlatform.Current` from the
  desktop app or `PlatformServices.Current` from Core and the CLI ([section 6](#6-interfaces-for-the-core-split-decided-in-52)).
- **A new OS** (e.g. macOS): a new module with `MacPlatform : IPlatformServices`, a target framework and project
  reference for it in `Sluice.Desktop.csproj`, and one more branch in `Platform.cs`. Nothing else changes.

## Not finished yet

- **Windows capture code still lives in `Sluice.Core`** (`Interceptor`, `WinDivertNative`, iphlpapi process lookup,
  DPAPI, the Job object), guarded by `OperatingSystem.IsWindows()` checks. Core is built once for both, so this code is
  also compiled into the Linux build; it is never called there (`LinuxPlatform.CreateCapture` uses nftables), but it
  should move to `Sluice.Platform.Windows`. The plan is in [REQUIRED.md §1](../REQUIRED.md#1-move-windows-only-code-out-of-core)
  and the interfaces it uses are fixed below.
- **`Sluice.Cli` is not multi-targeted yet**: it picks its capture with a run-time `OperatingSystem.IsLinux()` branch
  instead of the platform module (same section of REQUIRED.md).

## 6. Interfaces for the Core split (decided in #52)

These names and signatures are **binding** for the parts of #46 (#53–#58) and for #59 / #60. A part that finds one
doesn't fit stops and raises it on #46; the owner changes this section first. Every addition to an interface is a
**default interface member** (doing nothing, or returning `null`), so the other platform keeps building until its own
part lands.

**The Core side is in (#104):** `PlatformServices`, the four members of 6.2 and `ISecretProtector` exist in Core;
`App` and the CLI call `PlatformServices.Use`; `SingBoxSession` calls `TieChildProcess` (it still assigns its own job
object until #56) and `ProxifierPasswordCodec` uses `SecretProtector` when there is one (otherwise the DPAPI code still
in Core, until #55). So each platform part changes only its own area's files.

### 6.1 Reaching the platform from Core: `PlatformServices`

Core code deep inside the engine (a sing-box session, the `.ppx` password codec) needs the platform but has no
`OsPlatform` (that lives in the desktop app). One static entry point in Core, set once at start-up:

```csharp
namespace Sluice.Core.Platform;

public static class PlatformServices
{
    /// <summary>The OS module in use. A do-nothing default until Use is called (tests, tools).</summary>
    public static IPlatformServices Current { get; }

    /// <summary>Called once at start-up by Sluice.Desktop (App) and Sluice.Cli (Program), before anything else:
    /// sets Current and points ProcessLookup.Backend at platform.ProcessLookup.</summary>
    public static void Use(IPlatformServices platform);
}
```

- `OnAppStart()` stays the desktop app's own start-up step (Start Menu shortcut, notification registration); the CLI
  never calls it. Nothing that both programs need may live in `OnAppStart` any more.
- `OsPlatform.Current` (desktop) stays as it is; `App` passes it to `PlatformServices.Use`.

### 6.2 New `IPlatformServices` members

```csharp
public interface IPlatformServices
{
    // … the members in section 4 …

    /// <summary>"Which app owns this connection" for this OS. Null: no lookups (connections have PID 0).</summary>
    IProcessLookup? ProcessLookup => null;

    /// <summary>Makes <paramref name="process"/> (a helper Sluice just started) end when Sluice ends, even on a crash.</summary>
    void TieChildProcess(System.Diagnostics.Process process) { }

    /// <summary>Called on the thread about to start a helper (sing-box), right before Process.Start, with nothing
    /// awaited in between. Throwing stops the start.</summary>
    void PrepareHelperStart() { }

    /// <summary>Drops the OS's cached DNS answers (after capture started or stopped, after DNS settings changed).
    /// Never throws; a failure is logged by the caller's log if it passes one, otherwise ignored.</summary>
    void FlushDnsCache() { }

    /// <summary>Protects secrets with the current OS account (Windows DPAPI). Null where there is none.</summary>
    Sluice.Core.Profiles.ISecretProtector? SecretProtector => null;
}
```

| Member | Windows | Linux | Called by |
|---|---|---|---|
| `ProcessLookup` | `WindowsProcessLookup` (iphlpapi + kernel32, moved out of `ProcessLookup.cs`, #54) | `LinuxProcessLookup` (sock_diag + `/proc`; this property since #57, no longer set in `OnAppStart`) | `PlatformServices.Use` sets `ProcessLookup.Backend` |
| `TieChildProcess` | the kill-on-close job object, moved out of `SingBoxSession` (#56) | ends helpers on exit signals and cleans up helpers left by a crashed run (`HelperTies`, #57) | `SingBoxSession`, right after `Process.Start` |
| `PrepareHelperStart` | nothing | to be filled in by the Linux area | `SingBoxSession`, on the same thread right before `Process.Start`; an exception stops the start |
| `FlushDnsCache` | `DnsFlushResolverCache` (dnsapi), moved out of `Interceptor` (#56) | `resolvectl flush-caches` (`LinuxCapture.FlushResolverCache`, which the capture calls too, #57) | each capture, where it flushes today |
| `SecretProtector` | `WindowsSecretProtector` (DPAPI, moved from `Profiles/Dpapi.cs`, #55) | `null` | `ProxifierPasswordCodec` |

`ProcessLookup` (the static class in Core) keeps `Backend` and its forwarding methods; with no backend they return
"unknown" (PID 0, no listeners, no path), never Windows code.

### 6.3 `ISecretProtector`

```csharp
namespace Sluice.Core.Profiles;

public interface ISecretProtector
{
    byte[] Protect(byte[] data, string? description = null);   // as Dpapi.Protect today
    byte[] Unprotect(byte[] data);                               // throws CryptographicException if it can't
}
```

`ProxifierPasswordCodec` uses `PlatformServices.Current.SecretProtector`. When it is `null`, the "account" mode
(`PasswordEncryptionMode.CurrentUser`) fails with a clear message: "Passwords protected with the Windows account can't
be read on this system; open the profile on Windows and save it with another password mode." Profiles in the other
modes are unaffected (#55).

### 6.4 Datagram I/O for UDP: `UdpFlows` and `IDatagramSender`

`UdpNat` mixes the flow logic (per-app association through `RoutingEngine.OpenUdpAsync`, replies, expiry) with raw
WinDivert packets. #59 splits it so the Linux TPROXY path (#60) reuses the logic with sockets instead of packets:

```csharp
namespace Sluice.Core.Interception;

/// <summary>One UDP datagram an app sent: from App to Destination. Context is the platform's own (e.g. the WinDivert
/// address it arrived with), handed back unchanged in SendToApp.</summary>
public readonly record struct AppDatagram(IPEndPoint App, IPEndPoint Destination, ReadOnlyMemory<byte> Payload, object? Context);

/// <summary>Implemented by each capture: delivers a reply to the app as if it came from <paramref name="from"/>.</summary>
public interface IDatagramSender
{
    void SendToApp(IPEndPoint from, IPEndPoint app, ReadOnlyMemory<byte> payload, object? context);
}

/// <summary>The flow logic of today's UdpNat, with no packet format in it.</summary>
public sealed class UdpFlows : IAsyncDisposable
{
    public enum Verdict { Pass, Drop }   // Drop: taken (tunneled or blocked); the platform must not send it on

    public UdpFlows(RoutingEngine engine, FakeIpPool fakes, IDatagramSender sender, Func<IPEndPoint, int> findOwner);
    public Verdict Handle(in AppDatagram datagram);
    public int FlowCount { get; }
    public long DatagramsTunneled { get; }
    public long DatagramsDropped { get; }
    public void Sweep(TimeSpan idle);
}
```

- **Windows** (#53): a small WinDivert adapter parses each captured packet with `UdpPacket.TryParse` into an
  `AppDatagram` (context: its WinDivert address) and implements `SendToApp` with `UdpPacket.Build` + an inbound inject,
  which is what `UdpNat` does today. `UdpPacket` stays in Core.
- **Linux** (#60): a transparent UDP socket (`IP_TRANSPARENT`, `IP_RECVORIGDSTADDR`) gives `App`, `Destination` and the
  payload; `SendToApp` answers from a transparent socket bound to `from`.
- `UdpNat` itself goes away once both use `UdpFlows`; its tests move to `UdpFlows` with a fake sender.

### 6.5 The CLI takes its capture from the platform

`Sluice.Cli` becomes multi-targeted like the desktop app (`net10.0-windows10.0.19041.0;net10.0`), references the
platform module the same way (section 2), picks it with the same `#if WINDOWS` line, calls `PlatformServices.Use`, and
gets its capture from `CreateCapture(engine)` after checking `CaptureUnavailableReason` and `HasCaptureRights` (#58).
The `OperatingSystem.IsLinux()` branch and the direct `new Interceptor(...)` go. The Windows packet diagnostics behind
`--log debug` (`Interceptor.DiagnosticPackets`) become the Windows capture's own choice: it enables them when the
tracker's level is Debug.

### 6.6 Capture rights and autostart (#68, for #69 and #70)

Also default interface members on `IPlatformServices`, so a platform that doesn't offer them keeps building and the
UI hides them:

```csharp
bool CanGrantCaptureRights => false;                                   // show the "Grant capture rights" button?
Task<bool> TryGrantCaptureRightsAsync() => Task.FromResult(false);     // OS password prompt; true when it worked; never throws
bool AutostartSupported => false;                                      // show the "Start at login" switch?
bool Autostart { get => false; set { } }                               // start Sluice at login for this user
```

| Member | Windows | Linux |
|---|---|---|
| `CanGrantCaptureRights` / `TryGrantCaptureRightsAsync` | not offered (capture needs administrator; the banner offers a restart as administrator) | pkexec + setcap on the running binary (#69) |
| `AutostartSupported` / `Autostart` | not offered yet | `~/.config/autostart/sluice.desktop` (#69) |

The desktop app (#70) shows the button in the capture-rights banner and the switch in Settings only when the platform
says it supports them.

### 6.7 What stays in Core

`PacketReflector`, `DnsRedirector`, `TcpPacket`, `UdpPacket`, `FakeIpPool`, `ITrafficCapture`, `IProcessLookup`, the
static `ProcessLookup` (backend and forwarding only) and everything in sections 6.1–6.4. After #46, Core has no
`DllImport` of a Windows library and no `OperatingSystem.IsWindows()` branch around one.
