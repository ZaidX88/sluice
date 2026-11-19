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

- **Dark title bar on Windows** (the WPF app used DWM `DWMWA_USE_IMMERSIVE_DARK_MODE`): add it via `IPlatformServices`.
- **Screenshot mode** renders at 1× because `RenderTargetBitmap` mis-scales DataGrid content above 100% DPI (screenshots
  only; `SLUICE_SHOT_SCALE` overrides). For real-window captures use `PrintWindow` with `PW_RENDERFULLCONTENT` from a
  DPI-aware process.
- **Linux tray:** Avalonia uses StatusNotifierItem; GNOME needs the AppIndicator extension. Without a tray, closing the
  window hides the app with no way back except relaunching. Detect a missing tray and quit on close instead.

## 3. No log file unless "file log" is turned on — low (UI)

**Seen** ([issue #7](https://github.com/ZaidX88/sluice/issues/7)): `AppSettings.FileLogEnabled` defaults to `false`, so
a new install writes no log. After a problem there is nothing to attach to a report. On Linux it matters more: the
`setcap` app is not dumpable, so the log is the only outside view of what capture decided.

**Options:** turn the file log on by default at Verbose (daily files are small; capture writes a timing line every 5 s
at Verbose, so check the daily size first), or always keep the last few thousand lines in a rolling file.

**Fixed when:** a fresh install has a log of the last session to attach to a report.

## 4. File dialogs with capture rights on Linux — high (UI part fixed)

**Seen** ([issue #9](https://github.com/ZaidX88/sluice/issues/9)): with the `setcap` capabilities `install.sh` gives
the app, Open profile, Save as, Export for Proxifier and Load key showed no dialog: the xdg-desktop-portal file chooser
refuses a process it can't inspect ("Unable to open /proc/<pid>/root"; a process with file capabilities isn't
dumpable). On Ubuntu 26.04 the failure ends the whole app (#24 catches such errors).

**UI part fixed** ([issue #25](https://github.com/ZaidX88/sluice/issues/25)): with capture rights on Linux,
`Program.BuildAvaloniaApp` sets `X11PlatformOptions { UseDBusFilePicker = false }`, so Avalonia's in-process GTK file
dialog is used; without capture rights, and on Windows, nothing changes. The dialog follows the system theme, not the
app's. The profile menu's items now run after the menu has closed, so it no longer stays on top of the dialog.

**Still open, Linux:** GTK 3 refuses to start while capture's group IDs differ (real and effective 377294, saved the
user's: "running setuid or setgid", #28), and the ambient `CAP_NET_ADMIN` that `NftRules` raises for `nft` stays on
that thread, so GTK's sandboxed image loader (`bwrap`) exits ("Unexpected capabilities but not setuid") and GTK aborts.
Until both are fixed, the dialogs work only in a run where capture hasn't started.

**Fixed when:** with the app installed by `install.sh` and capture on, the four dialogs open and work.
