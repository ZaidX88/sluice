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
- **Linux tray:** Avalonia uses StatusNotifierItem; GNOME needs the AppIndicator extension. Without a tray, closing the
  window hides the app with no way back except relaunching. Detect a missing tray and quit on close instead.

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

## 4. File dialogs with capture rights on Linux — high (UI part fixed)

**Seen** ([issue #9](https://github.com/ZaidX88/sluice/issues/9)): with the `setcap` capabilities `install.sh` gives
the app, Open profile, Save as, Export for Proxifier and Load key showed no dialog: the xdg-desktop-portal file chooser
refuses a process it can't inspect ("Unable to open /proc/<pid>/root"; a process with file capabilities isn't
dumpable). On Ubuntu 26.04 the failure ended the whole app; since #24 it shows a message instead (§5).

**UI part fixed** ([issue #25](https://github.com/ZaidX88/sluice/issues/25)): with capture rights on Linux,
`Program.BuildAvaloniaApp` sets `X11PlatformOptions { UseDBusFilePicker = false }`, so Avalonia's in-process GTK file
dialog is used; without capture rights, and on Windows, nothing changes. The dialog follows the system theme, not the
app's. The profile menu's items now run after the menu has closed, so it no longer stays on top of the dialog.

**Still open, Linux:** GTK 3 refuses to start while capture's group IDs differ (real and effective 377294, saved the
user's: "running setuid or setgid", #28), and the ambient `CAP_NET_ADMIN` that `NftRules` raises for `nft` stays on
that thread, so GTK's sandboxed image loader (`bwrap`) exits ("Unexpected capabilities but not setuid") and GTK aborts.
Until both are fixed, the dialogs work only in a run where capture hasn't started.

**Fixed when:** with the app installed by `install.sh` and capture on, the four dialogs open and work.

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
