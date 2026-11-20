# The desktop app (`Sluice.Desktop`)

Part of the [architecture reference](../ARCHITECTURE.md). Paths are relative to `src/Sluice.Desktop/`. One Avalonia 12
project, built twice (Windows and Linux; see [platforms.md](platforms.md)), with the same design on both: monochrome,
JetBrains Mono (`Assets/Fonts/`), pill buttons, cards, light and dark themes.

## Structure

```
Program.cs ──▶ App (App.axaml.cs)
                 ├─ single-instance pipe, OsPlatform.Current.OnAppStart()
                 ├─ AppSettings.Load()
                 ├─ EngineHost(settings)  ──owns──▶ RoutingEngine, ConnectionTracker, Statistics, FileLog,
                 │                                  Socks5Inbound, ITrafficCapture
                 ├─ MainViewModel(host)   ──owns──▶ page view models
                 ├─ MainWindow(vm, settings)
                 └─ AppTray(app, vm, show, exit)
```

| Folder / file | Holds |
|---|---|
| `Program.cs` | Avalonia set-up; on Linux with capture rights, Avalonia's own managed file dialogs instead of the portal, so GTK is never loaded there ([ui/FIXES §4](../ui/FIXES.md), #126) |
| `App.axaml(.cs)` | Start-up and shutdown, command-line options, single instance, clipboard and folder helpers |
| `MainWindow.axaml(.cs)` | The shell: sidebar navigation, top bar (capture button, search, rates sparkline), banner, current page; closing hides to the tray when `MinimizeToTray` is on and a tray is there (`TrayHost`), otherwise quits |
| `Services/EngineHost.cs` | The bridge between UI and engine (below) |
| `Services/Platform.cs` | `OsPlatform.Current`, chosen with `#if WINDOWS` |
| `Services/AppSettings.cs` | Per-user settings ([profiles.md](profiles.md#app-settings-and-the-settings-folder)) |
| `Services/TrayHost.cs` | Whether a tray can show Sluice's icon (#45): always on Windows; on Linux a `org.kde.StatusNotifierWatcher` with `IsStatusNotifierHostRegistered`, asked over the session bus (`Tmds.DBus.Protocol`, which Avalonia already uses) when the window is closed |
| `Services/AppTray.cs` | Tray icon: open, "Capture traffic" toggle, exit; tooltip with status and rates; icon changes with capture state (`AppIcon`) |
| `Services/Dialogs.cs` | Modal dialogs (`MessageDialog`, tunnel / rule / import / password dialogs, file pickers) |
| `Services/ScreenshotMode.cs` | `--screenshot DIR`: a demo profile in memory, real traffic through the app's own SOCKS5 listener, every page rendered to PNG (plus the capture-rights banner); touches no user files, never captures. It replaces `OsPlatform.Current` with a demo platform that offers granting rights and autostart, so both show in the images |
| `ViewModels/` | `MainViewModel` and one `PageViewModel` per page |
| `Views/` | The pages (`ConnectionsView`, `StatisticsView`, `LogView`, `TunnelsView`, `RulesView`, `SettingsView`; code-behind in `PageViews.axaml.cs`) and dialogs (`TunnelDialog`, `RuleDialog`, `ImportDialog`, `PasswordDialog`) |
| `Mvvm/ObservableObject.cs` | A minimal MVVM kit: `ObservableObject`, `RelayCommand`, `AsyncCommand` (no MVVM library) |
| `Controls/IconView.cs` | Line icons drawn from vector paths (the WPF app's Segoe Fluent Icons font does not exist on Linux) |
| `Controls/SluiceWindow.cs`, `Controls/TitleBar.cs` | Every window derives from `SluiceWindow` (#44). On Windows (a Win32 window handle) it turns the system title bar off (`ExtendClientAreaToDecorationsHint`, `WindowDecorations.BorderOnly`) and draws `TitleBar` (icon, title, minimise / maximise / close; close only when the window can't be resized) with a 1 px `WindowBorder`; Windows keeps the resize edges, shadow, rounded corners and snapping. Each part has its window role (`WindowDecorationProperties.ElementRole`): the bar is the caption (move, double-click to maximise, Aero Snap), the maximise button gives Windows 11's snap layouts. The bar's height is added to the window's height, so the content keeps its space. Colours: `TitleBarBg`, `TitleBarText`, `TitleBarButtonHover`, `WindowBorder` in `Palette.axaml`. Other window systems keep their own title bar until #84 |
| `Theme/` | `Palette.axaml` (light/dark colours), `Controls.axaml`, `Styles.axaml` |

## Start-up and shutdown: `App`

`OnFrameworkInitializationCompleted` runs, in order:

1. `--theme dark|light`; `--screenshot DIR` short-circuits into `ScreenshotMode` and exits.
2. **Single instance:** connect to the named pipe `Sluice-<user name>`; if another Sluice answers, send `show` and exit.
   Otherwise listen on that pipe (`ListenForShowRequestsAsync`) and show the window on each request.
3. `OsPlatform.Current.OnAppStart()` (Windows: Start Menu shortcut and app ID for notifications; Linux: `/proc` process
   lookup).
4. Create `EngineHost` and install `CrashGuard` on it (below), then `MainViewModel`, `MainWindow`, `AppTray`; route
   `EngineHost.TunnelNotice` to `OsPlatform.Current.Notify`.
5. Open the profile from `--profile` or `AppSettings.LastProfilePath` (asking for a master password if needed).
6. `--log-file DIR` (a Debug-level log for this run only), start the local SOCKS5 listener, and turn capture on with
   `--intercept` or `StartInterceptionOnLaunch`.

`ShutdownMode` is explicit: closing the window only hides it when tray mode is on and `TrayHost` finds a tray (stock
GNOME has none without the AppIndicator extension: there, closing quits). `ExitApp` (tray "Exit", or closing
without tray mode) closes the window, removes the tray icon, stops the pipe listener and disposes `EngineHost`.

**After an unexpected error** (`Services/CrashGuard.cs`, #24): an exception nothing caught, on the UI thread or any
other, is logged, capture is stopped off the UI thread (`EngineHost.StopInterceptionForCrash`, at most 5 s), the tray
icon is removed (Avalonia removes it in its next pass of UI work, which is run before exiting; otherwise Windows keeps a
dead icon until the mouse passes over it), and the app exits. Unobserved task exceptions are only logged. `--crash-test ui|thread|task` raises such an error 15 s after
start, for testing. File pickers that fail show a message instead (`Dialogs.PickFile`).

## `EngineHost`: the bridge

`Services/EngineHost.cs` owns the running engine for the UI. All its methods are called on the UI thread; engine events
from other threads are marshalled with `Dispatcher.UIThread.Post`.

- **Engine objects:** one `ConnectionTracker` (level Verbose), `Statistics`, one `RoutingEngine` for the whole run,
  the optional `FileLog`s.
- **Profile:** `LoadProfile` (`.ppx` imported to a new `.sluice`), `NewProfile`, `UseProfile`, `CommitProfileEdit`
  (reload + autosave), `TrySave`, `ExportProxifier`. Events `ProfileChanged`, `StateChanged`, `Notice`.
- **SOCKS5:** `StartSocks` / `RestartSocksAsync` on `127.0.0.1:<AppSettings.SocksPort>`.
- **Capture:** `StartInterception` checks `CaptureUnavailableReason` and `HasCaptureRights`, calls
  `OsPlatform.Current.CreateCapture(Engine)`, subscribes to `RelayUnreachable`, starts it and warms up tunnels;
  `StopInterceptionAsync`. A profile edit that changes the UDP mode restarts capture (`NeedsRestartFor`).
- **Elevation:** `RestartElevated` relaunches through UAC (`runas`) with `--intercept` (Windows only).
- **Firewall:** `AllowThroughFirewall` adds the `netsh` allow rule, only from the banner button.
- **Tunnel settings:** `ApplyTunnelSettings` sets `Backends.IdleTimeout`; `HostKeyLearned` writes SSH host keys into the
  profile.
- `DisposeAsync`: disconnect everything (2 s grace), stop capture, the SOCKS listener and the engine (closing tunnel
  sessions and helpers), close the file log.

## View models and refresh

`MainViewModel` builds the pages and groups them in the sidebar: **Monitor** (`ConnectionsViewModel`,
`StatisticsViewModel`, `LogViewModel`), **Configure** (`TunnelsViewModel`, `RulesViewModel`), **App**
(`SettingsViewModel`). It owns the capture switch (`Intercepting`, `TurnOn`), file commands, search, rates and the
banner.

The UI does not bind to engine objects directly. A **1-second `DispatcherTimer`** in `MainViewModel` drains the log
(`LogViewModel.Pump`, which queues lines from the tracker's `Log` event in a `ConcurrentQueue`), refreshes only the
visible page (`PageViewModel.Refresh`) and updates rates and status. `TunnelsViewModel` additionally listens to
`Backends.StatusChanged` (posted to the UI thread) and runs its own 3-second timer for session states; it also runs
`TunnelChecker`, imports links and subscriptions, and copies share links. When the profile is replaced, every page gets
`OnProfileChanged`.

## Notifications and banners

- **Notifications** go through `OsPlatform.Current.Notify(title, text, isError)`: Windows toasts under the app ID
  "Sluice" (`StartMenuShortcut`), `notify-send` on Linux. Sources: tunnel transitions (`EngineHost.OnTunnelStatus`,
  only real ones: connected, reconnected, disconnected, could not connect, stopped; off with
  `AppSettings.TunnelNotifications`) and the one-time "still running" hint when the window first hides to the tray
  (`AppTray.ShowHiddenHint`).
- **The banner** at the top of the content area (`MainViewModel.Banner`) shows errors and notices, with optional
  actions:
  - capture rights missing → `CaptureRightsHint` (`ShowRightsBanner`), plus "Restart as administrator" only when
    `CanRestartElevated`, and "Grant capture rights" only when `CanGrantCaptureRights` (#70). Granting runs
    `TryGrantCaptureRightsAsync` (the OS's password prompt); afterwards capture starts if the rights already apply,
    otherwise the banner shows the platform's `CaptureRightsHint`, which says how to start Sluice with them (Linux:
    through the launcher, #129);
  - capture impossible (Linux without `nft`) → `CaptureUnavailableReason`;
  - `FirewallBlocked` (from `RelayUnreachable`) → "Allow" button running `AllowThroughFirewall`;
  - neutral notices (`ShowInfo`), e.g. "imported from Proxifier".

Checking UI changes: `Sluice.Desktop --screenshot <dir> [--theme dark]`, light and dark (see
[windows/BUILD.md](../windows/BUILD.md)). Known UI gaps: [ui/FIXES.md §1 and §2](../ui/FIXES.md).
