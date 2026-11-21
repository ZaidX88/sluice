<#
  Builds the Windows installer artifacts\windows\Sluice-Setup-<version>-x64.exe and its .sha256 (#64).

    powershell -ExecutionPolicy Bypass -File scripts\package-windows.ps1 [-Iscc <path to ISCC.exe>] [-OutputDir <dir>]

  Needs the .NET 10 SDK and Inno Setup 6.4 or later (https://jrsoftware.org/isinfo.php). Publishes Sluice.Desktop and
  Sluice.Cli self-contained for win-x64 into one folder (the build fetches the pinned WinDivert and sing-box), then
  compiles packaging\windows\Sluice.iss. Built packages are never committed; release CI builds them from a tag (#74).
#>
param(
    [string]$Iscc = "",
    [string]$OutputDir = ""
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $OutputDir) { $OutputDir = Join-Path $repo 'artifacts\windows' }
$version = ([xml](Get-Content (Join-Path $repo 'Directory.Build.props'))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw "No <Version> in Directory.Build.props" }
$numeric = ($version -split '[-+]')[0]            # 1.0.0-rc.1 -> 1.0.0, for the file's version resource
$publish = Join-Path $repo 'artifacts\windows-publish'

if (-not $Iscc) {
    $Iscc = @(
        "$env:ProgramFiles\Inno Setup 7\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 7\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 7\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $Iscc -or -not (Test-Path $Iscc)) { throw "ISCC.exe (Inno Setup) not found; install it or pass -Iscc <path>." }

Write-Host "Sluice ${version}: publishing to $publish"
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
dotnet publish (Join-Path $repo 'src\Sluice.Desktop\Sluice.Desktop.csproj') -f net10.0-windows10.0.19041.0 -r win-x64 --self-contained true -c Release -o $publish
if ($LASTEXITCODE) { throw "publishing Sluice.Desktop failed" }
dotnet publish (Join-Path $repo 'src\Sluice.Cli\Sluice.Cli.csproj') -r win-x64 --self-contained true -c Release -o $publish
if ($LASTEXITCODE) { throw "publishing Sluice.Cli failed" }
foreach ($f in 'Sluice.Desktop.exe', 'Sluice.Cli.exe', 'coreclr.dll', 'WinDivert.dll', 'WinDivert64.sys', 'sing-box.exe') {
    if (-not (Test-Path (Join-Path $publish $f))) { throw "$f is missing from the publish output" }
}

New-Item -ItemType Directory -Force $OutputDir | Out-Null
& $Iscc /Q "/DSourceDir=$publish" "/DAppVersion=$version" "/DNumericVersion=$numeric" "/DOutputDir=$OutputDir" "/DRepoDir=$repo" (Join-Path $repo 'packaging\windows\Sluice.iss')
if ($LASTEXITCODE) { throw "Inno Setup failed" }

$setup = Join-Path $OutputDir "Sluice-Setup-$version-x64.exe"
$hash = (Get-FileHash $setup -Algorithm SHA256).Hash.ToLower()
"$hash  $(Split-Path $setup -Leaf)" | Set-Content -Encoding ascii "$setup.sha256"
Write-Host ("Built {0} ({1:N1} MB)  sha256 {2}" -f $setup, ((Get-Item $setup).Length / 1MB), $hash)
