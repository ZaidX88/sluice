# Installing Sluice on Windows

Sluice sends each app's connections where you choose: **directly**, **blocked**, or **through a tunnel**
(SSH, SOCKS, HTTP(S), VLESS, VMess, Trojan, Shadowsocks, Hysteria2, TUIC).

| File | What it is |
|---|---|
| `Sluice-Setup-<version>-x64.exe` | the installer |
| `Sluice-Setup-<version>-x64.exe.sha256` | its checksum, to check the download |

The short version (add a tunnel, add rules, start) is [../QUICKSTART.md](../QUICKSTART.md). Building the installer
yourself: [Building the installer](#building-the-installer) below.

## 1. Requirements

- **Windows 10 (1809 or later) or Windows 11, 64-bit (x64).**
- **Administrator rights** to install, and to capture other apps' traffic (Sluice uses the WinDivert driver).
- Nothing else: the installer contains everything Sluice needs, .NET included, so it installs offline too.

## 2. Check the download (optional)

In PowerShell, in the folder holding both files:

```powershell
(Get-FileHash .\Sluice-Setup-<version>-x64.exe -Algorithm SHA256).Hash.ToLower()
Get-Content .\Sluice-Setup-<version>-x64.exe.sha256
```

The two values must be the same.

## 3. Install

Run `Sluice-Setup-<version>-x64.exe` and follow the wizard.

**Windows SmartScreen** may say it "prevented an unrecognized app from starting", because the installer isn't code-signed
yet (#65). Choose **More info → Run anyway** if you trust where you got it from.

Setup:

1. installs Sluice to `C:\Program Files\Sluice`: the app, the command line (`Sluice.Cli.exe`), the WinDivert driver
   and the sing-box helper;
2. adds **Sluice** to the Start Menu, plus a desktop shortcut if you tick it;
3. **allows Sluice through Windows Firewall** (the ticked option). Captured connections are handed to Sluice locally,
   so Windows must let Sluice receive them.

A newer installer upgrades an installed Sluice in place; close Sluice first, or Setup closes it.

Silent install (IT deployment): `Sluice-Setup-<version>-x64.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART`.

## 4. Start Sluice

Open **Sluice** from the Start Menu. To **capture other apps' traffic**, Sluice needs administrator rights: right-click
→ **Run as administrator**, or press **Restart as administrator** in the banner when you turn capture on.

Then follow [../QUICKSTART.md](../QUICKSTART.md): add a tunnel, add rules, press the play button.

Settings and profiles are stored in `%APPDATA%\Sluice`.

## 5. Uninstall

**Settings → Apps → Installed apps → Sluice → Uninstall** (or "Add or remove programs"). This removes the program, its
folder, its shortcuts and its firewall rules, and unloads the WinDivert driver. Your settings and profiles in
`%APPDATA%\Sluice` are kept; delete that folder too if you want them gone.

## Troubleshooting

**"Capturing other applications' traffic needs administrator rights."** Start Sluice as administrator (see step 4).

**Captured connections don't go anywhere / a firewall banner appears.** The firewall rule is missing (for example, the
option was unticked). Press **Allow** in Sluice's banner, or run Setup again with the firewall option ticked.

**Another traffic redirector is running.** Proxifier, ProxyCap, Clash, v2rayN and similar tools capture traffic first.
Quit them while Sluice is capturing.

**Antivirus warns about `WinDivert64.sys`.** WinDivert is a legitimate, signed packet-capture driver used by many tools;
some antivirus products flag it because it can intercept traffic.

## Building the installer

Needs the .NET 10 SDK and Inno Setup 6.4 or later (<https://jrsoftware.org/isinfo.php>). From the repository root:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\package-windows.ps1
```

It publishes `Sluice.Desktop` and `Sluice.Cli` self-contained for win-x64 into `artifacts\windows-publish` (the build
fetches the pinned WinDivert and sing-box), compiles `packaging\windows\Sluice.iss` and writes
`artifacts\windows\Sluice-Setup-<version>-x64.exe` with its `.sha256`. The version comes from `Directory.Build.props`.
Built packages are never committed; release CI builds them from a version tag (#74).
