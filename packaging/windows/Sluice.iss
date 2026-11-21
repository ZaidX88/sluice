; Sluice installer for Windows (Inno Setup 6.4 or later; built with 7.1). scripts\package-windows.ps1 publishes the
; app and compiles this script, passing SourceDir, AppVersion, NumericVersion, OutputDir and RepoDir.
;
; The installer holds Sluice.Desktop and Sluice.Cli published self-contained for win-x64 (the .NET runtime is inside, so
; nothing is downloaded during setup), with their helpers WinDivert and sing-box (DECISIONS.md, 2026-11-22, #64).

#ifndef SourceDir
  #error Pass the published app folder: /DSourceDir=...
#endif
#ifndef AppVersion
  #error Pass the version: /DAppVersion=... (Directory.Build.props)
#endif
#ifndef NumericVersion
  #define NumericVersion "0.0.0"
#endif
#ifndef OutputDir
  #define OutputDir "..\..\artifacts\windows"
#endif
#ifndef RepoDir
  #define RepoDir "..\.."
#endif

[Setup]
; Keep the AppId: it ties every version's installer and uninstaller together, so a newer one upgrades in place.
AppId={{6F0E8B2C-5A3D-4E8B-9C71-5C1CE0000001}
AppName=Sluice
AppVersion={#AppVersion}
AppVerName=Sluice {#AppVersion}
AppPublisher=Sluice
AppPublisherURL=https://github.com/ZaidX88/sluice
AppSupportURL=https://github.com/ZaidX88/sluice/issues
DefaultDirName={autopf}\Sluice
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir={#OutputDir}
OutputBaseFilename=Sluice-Setup-{#AppVersion}-x64
SetupIconFile={#RepoDir}\src\Sluice.Desktop\Assets\Sluice.ico
UninstallDisplayIcon={app}\Sluice.Desktop.exe
UninstallDisplayName=Sluice
VersionInfoVersion={#NumericVersion}
VersionInfoProductName=Sluice
VersionInfoProductVersion={#NumericVersion}
VersionInfoDescription=Sluice setup
LicenseFile={#RepoDir}\LICENSE
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; Sluice (and its sing-box helpers) must not be running while files are replaced or removed.
CloseApplications=force
RestartApplications=no

[Tasks]
Name: firewall; Description: "Allow Sluice through Windows Firewall (needed to capture other apps' traffic)"
Name: desktopicon; Description: "Create a desktop shortcut"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#RepoDir}\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion
Source: "{#RepoDir}\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
; The app ID makes Windows title notifications "Sluice" with its icon; the app keeps a working shortcut with this ID.
Name: "{autoprograms}\Sluice"; Filename: "{app}\Sluice.Desktop.exe"; AppUserModelID: "Sluice"
Name: "{autodesktop}\Sluice"; Filename: "{app}\Sluice.Desktop.exe"; Tasks: desktopicon

[Run]
; Captured connections are redirected to Sluice's local relay, so the firewall must let Sluice receive them.
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=all program=""{app}\Sluice.Desktop.exe"""; Flags: runhidden; Tasks: firewall
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""Sluice"" dir=in action=allow program=""{app}\Sluice.Desktop.exe"" enable=yes profile=any"; Flags: runhidden; Tasks: firewall; StatusMsg: "Adding the firewall rules..."
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=all program=""{app}\Sluice.Cli.exe"""; Flags: runhidden; Tasks: firewall
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""Sluice CLI"" dir=in action=allow program=""{app}\Sluice.Cli.exe"" enable=yes profile=any"; Flags: runhidden; Tasks: firewall
Filename: "{app}\Sluice.Desktop.exe"; Description: "Start Sluice"; Flags: postinstall nowait skipifsilent runasoriginaluser

[UninstallRun]
; Unload the WinDivert driver so its file in {app} can be deleted now rather than after a restart.
Filename: "{sys}\sc.exe"; Parameters: "stop WinDivert"; Flags: runhidden; RunOnceId: "StopWinDivert"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=all program=""{app}\Sluice.Desktop.exe"""; Flags: runhidden; RunOnceId: "FirewallApp"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=all program=""{app}\Sluice.Cli.exe"""; Flags: runhidden; RunOnceId: "FirewallCli"

[UninstallDelete]
; Files the app writes next to itself at run time, if any.
Type: filesandordirs; Name: "{app}"

[Code]
// Before files are copied (install and upgrade alike): unload the WinDivert driver if a Sluice left it loaded, so
// WinDivert64.sys can be replaced. Sluice itself is closed by CloseApplications. Failing to stop it isn't fatal.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\sc.exe'), 'stop WinDivert', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := '';
end;

[Messages]
FinishedLabel=Sluice is installed. Capturing other apps' traffic needs administrator rights: start Sluice as administrator, or use "Restart as administrator" when it asks.
