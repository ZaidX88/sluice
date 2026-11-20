# Known bugs: UI

Bugs and limits whose fix belongs to the **UI** area ([CONTRIBUTING.md → Areas](../CONTRIBUTING.md#areas)). The other
areas: [Core](../core/FIXES.md), [Linux](../linux/FIXES.md), [Windows](../windows/FIXES.md).

Each entry: what was seen, why, what to do, and when it counts as fixed, with the GitHub issue that tracks it.
Priorities: **high** = breaks normal use. An entry is kept after the fix, marked **fixed**, when its cause or evidence
is worth keeping.

## 1. Layout at narrower window sizes

**Seen** at about 1040×650 DIP: on **Tunnels** with the check panel open, the Status column disappears and Session is
cut; on **Statistics** the "Share of traffic" header is cut.

**To do:** let the check panel shrink or move below the list under a width threshold; hide Address and Session before
Status; shorten the header to "Share"; add `--screenshot DIR --size 1040x650` to keep it checked.

## 2. Avalonia UI gaps (small)

- **Screenshot mode** renders at 1× because `RenderTargetBitmap` mis-scales DataGrid content above 100% DPI (screenshots
  only; `SLUICE_SHOT_SCALE` overrides). For real-window captures use `PrintWindow` with `PW_RENDERFULLCONTENT` from a
  DPI-aware process.
- **Linux tray — fixed** ([issue #45](https://github.com/ZaidX88/sluice/issues/45)): Avalonia uses StatusNotifierItem;
  GNOME needs the AppIndicator extension. Without a tray, closing the window hid the app with no way back except
  relaunching. Now closing asks the session bus whether a tray is there (`TrayHost`: a `org.kde.StatusNotifierWatcher`
  with a host registered) and quits when there is none, or no answer within 2 s.

## 3. No log file unless "file log" is turned on — fixed

**Seen** ([issue #7](https://github.com/ZaidX88/sluice/issues/7)): `AppSettings.FileLogEnabled` defaulted to `false`,
so a new install wrote no log. After a problem there was nothing to attach to a report. On Linux it matters more: the
`setcap` app is not dumpable, so the log is the only outside view of what capture decided.

**Fixed** ([issue #40](https://github.com/ZaidX88/sluice/issues/40)): the file log is on for new settings files (an
existing `settings.json` keeps its choice) and writes at **Normal**: tunnels, rules applied, blocked connections and
errors. Settings → "Log detail" switches to Detailed (Verbose: also closed connections and relay statistics). The page
says that the last 7 days are kept and that the log lists the apps and sites Sluice handled, and shows the folder
(shortened to `%APPDATA%\…` / `~/…`) with "Open folder". Screenshot mode writes no log.

Found on the way: on Linux, .NET returns `""` for the config folder while `~/.config` doesn't exist (a fresh account),
so settings and logs went to the current folder. `AppSettings.Folder` now asks with `DoNotVerify`.

## 4. File dialogs with capture rights on Linux — fixed

**Seen** ([issue #9](https://github.com/ZaidX88/sluice/issues/9)): with the capture capabilities (given with `setcap`,
since GHSA-qvhh-fmwh-wx3j passed on by the launcher), Open profile, Save as, Export for Proxifier and Load key showed
no dialog: the xdg-desktop-portal file chooser refuses a process it can't inspect ("Unable to open /proc/<pid>/root").
On Ubuntu 26.04 the failure ended the whole app; since #24 it shows a message instead (§5).

**First fix** ([issue #25](https://github.com/ZaidX88/sluice/issues/25)): Avalonia's in-process GTK file dialog
(`X11PlatformOptions { UseDBusFilePicker = false }`). It needed Linux changes to start at all under capture's group IDs
and capabilities (#28, #99), and it put GTK, which loads modules named in the user's own GTK settings, inside the
process that holds the capture capabilities.

**Fixed** ([issue #126](https://github.com/ZaidX88/sluice/issues/126)): with capture rights on Linux,
`Program.BuildAvaloniaApp` uses Avalonia's own managed file dialogs (`UseManagedSystemDialogs`, part of the Avalonia
package): drawn by Avalonia in Sluice's theme and font, no portal, and GTK is never loaded in that process. Without
capture rights the portal dialog is kept; Windows is unchanged. `SLUICE_MANAGED_DIALOGS=1` forces the managed dialogs
for testing without capture rights (the launcher doesn't pass it on).

**Known limit of the managed dialog** (seen in the live check of #128): in Save as, a folder typed into the location
bar isn't used for the result. Avalonia builds it as `Path.Combine(Location, FileName)` from the folder the dialog is
showing, and the path came out relative, so it was resolved against Sluice's working folder (`/`) and the save was
refused (nothing written, the error shown). Reaching the folder by clicking works. It's inside Avalonia's dialog, so the
fix belongs upstream.

## 5. A failing file dialog closed the app; a crash could leave capture behind — fixed

**Seen** ([issue #24](https://github.com/ZaidX88/sluice/issues/24), part of #9): any exception from a file picker (Open
profile, Save as, Export for Proxifier, Load key) closed the whole app, and an unexpected error during capture ended it
without stopping capture, which on Linux could leave the nftables table behind.

**Fixed:** `Dialogs.PickFile` runs the three profile pickers and shows "The file dialog couldn't open: …" when one
fails, as if cancelled; `TunnelDialog.OnLoadKey` (an `async void` handler) shows the picker's or the key file's error in
the dialog. `CrashGuard` is the last resort: an error on the UI thread (`Dispatcher.UnhandledException`) or any other
thread (`AppDomain.UnhandledException`) is logged, capture is stopped off the UI thread
(`EngineHost.StopInterceptionForCrash`, at most 5 s; on Linux it removes the table), and the app exits (code 1 from the
UI thread; on other threads .NET ends the process). A task whose failure nobody observed
(`TaskScheduler.UnobservedTaskException`) is only logged: .NET ignores it and the app keeps working. Note: Avalonia
swallows exceptions thrown in a `DispatcherTimer` callback, so they never reach `CrashGuard`.

Checked on Linux with the test-only `--crash-test ui|thread|task` (an error 15 s after start): with capture on, both
`ui` and `thread` log the error, "Capture stopped" and "Capture stopped before closing"; name lookups and direct
connections work right after the crash and no sing-box is left. `task` is logged and the app keeps running. A failing
Open profile (its folder couldn't be created) shows the message and the app keeps running.
