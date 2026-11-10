# Downloads the pinned sing-box release used by Sluice's encrypted tunnels into lib/sing-box,
# verifying the archive against the SHA-256 published on GitHub. sing-box is GPL-3.0; its licence
# is kept next to the binary. The binary is not committed to git (see .gitignore).
#
#   powershell -ExecutionPolicy Bypass -File scripts/get-sing-box.ps1

$ErrorActionPreference = 'Stop'
$Version = '1.14.4'
$Sha256  = '99185d75d314a07dbba4a130c8ad986cbeb629a07927975636b341ca61f991df'
$Asset   = "sing-box-$Version-windows-amd64.zip"
$Url     = "https://github.com/SagerNet/sing-box/releases/download/v$Version/$Asset"

$root = Split-Path -Parent $PSScriptRoot
$dest = Join-Path $root 'lib\sing-box'
$exe  = Join-Path $dest 'sing-box.exe'

if (Test-Path $exe) {
    $have = (& $exe version 2>$null | Select-Object -First 1)
    if ($have -match [regex]::Escape($Version)) { Write-Host "sing-box $Version already present: $exe"; exit 0 }
}

$tmp = Join-Path ([IO.Path]::GetTempPath()) ("sing-box-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tmp | Out-Null
try {
    $zip = Join-Path $tmp $Asset
    Write-Host "Downloading $Url"
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -UseBasicParsing -Uri $Url -OutFile $zip

    $actual = (Get-FileHash -Algorithm SHA256 $zip).Hash.ToLowerInvariant()
    if ($actual -ne $Sha256) { throw "SHA-256 mismatch: expected $Sha256, got $actual. Not installing." }
    Write-Host "SHA-256 verified"

    Expand-Archive -Path $zip -DestinationPath $tmp -Force
    $dir = Get-ChildItem $tmp -Directory | Where-Object { Test-Path (Join-Path $_.FullName 'sing-box.exe') } | Select-Object -First 1
    if (-not $dir) { throw 'sing-box.exe not found in the archive' }

    New-Item -ItemType Directory -Path $dest -Force | Out-Null
    Copy-Item (Join-Path $dir.FullName 'sing-box.exe') $exe -Force
    foreach ($f in 'LICENSE', 'LICENSE.txt') {
        $p = Join-Path $dir.FullName $f
        if (Test-Path $p) { Copy-Item $p (Join-Path $dest 'LICENSE') -Force }
    }
    Set-Content -Path (Join-Path $dest 'SOURCE.txt') -Encoding ascii -Value @(
        "sing-box $Version (windows-amd64), GPL-3.0",
        "Binary: $Url",
        "Source: https://github.com/SagerNet/sing-box/tree/v$Version",
        "SHA-256 of archive: $Sha256")
    Write-Host "Installed: $exe"
    & $exe version
}
finally {
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
}
