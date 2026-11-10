<#
.SYNOPSIS
    Shows which public IP your UDP traffic leaves from, using a STUN request (UDP 19302).

    Run it once with Sluice capture off and once with it on (UDP mode "Through the tunnel").
    If UDP is tunneled, the second run shows the tunnel server's IP instead of yours,
    and Sluice's Connections page lists "...:19302 UDP" through your tunnel.
#>
param(
    [string]$Server = "stun.l.google.com",
    [int]$Port = 19302,
    [int]$TimeoutMs = 4000
)

$ip = ([Net.Dns]::GetHostAddresses($Server) | Where-Object AddressFamily -eq 'InterNetwork' | Select-Object -First 1)
if (-not $ip) { Write-Host "Cannot resolve $Server"; exit 2 }

# STUN Binding Request: type 0x0001, length 0, magic cookie, 12-byte transaction id.
$tx = New-Object byte[] 12
(New-Object Random).NextBytes($tx)
$req = [byte[]](0x00, 0x01, 0x00, 0x00, 0x21, 0x12, 0xA4, 0x42) + $tx

$udp = New-Object Net.Sockets.UdpClient
$udp.Client.ReceiveTimeout = $TimeoutMs
try {
    [void]$udp.Send($req, $req.Length, $ip.ToString(), $Port)
    $from = New-Object Net.IPEndPoint([Net.IPAddress]::Any, 0)
    $r = $udp.Receive([ref]$from)
}
catch {
    Write-Host "No UDP reply from ${Server}:$Port within $TimeoutMs ms."
    Write-Host "Either UDP is blocked on this route, or the tunnel does not carry UDP (see Sluice's Log page)."
    exit 1
}
finally {
    $udp.Dispose()
}

# Walk the attributes looking for XOR-MAPPED-ADDRESS (0x0020) or MAPPED-ADDRESS (0x0001).
$i = 20
while ($i + 4 -le $r.Length) {
    $type = ($r[$i] -shl 8) -bor $r[$i + 1]
    $len = ($r[$i + 2] -shl 8) -bor $r[$i + 3]
    $v = $i + 4
    if (($type -eq 0x0020 -or $type -eq 0x0001) -and $r[$v + 1] -eq 1) {
        $port = ($r[$v + 2] -shl 8) -bor $r[$v + 3]
        $a = $r[($v + 4)..($v + 7)]
        if ($type -eq 0x0020) {
            $port = $port -bxor 0x2112
            $a = @(($a[0] -bxor 0x21), ($a[1] -bxor 0x12), ($a[2] -bxor 0xA4), ($a[3] -bxor 0x42))
        }
        Write-Host "UDP works. Your traffic leaves from $($a -join '.'):$port (as seen by $Server)."
        exit 0
    }
    $i = $v + $len + ((4 - $len % 4) % 4)
}
Write-Host "Got a reply from $from but it had no mapped address."
exit 3
