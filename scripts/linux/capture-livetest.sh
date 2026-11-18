#!/bin/bash
# Live test of Linux traffic capture (nftables + NFQUEUE). Run as root on Linux (or WSL2) with the .NET 10 runtime.
#
#   sudo scripts/linux/capture-livetest.sh <dir with Sluice.Cli.dll> <profile.sluice> [upstream-dns]
#
# Build the CLI first (framework-dependent, any OS):  dotnet build src/Sluice.Cli -o out/cli
# Make the profile:                                   dotnet run scripts/linux/make-test-profile.cs -- linux-test.sluice
# The optional upstream-dns (e.g. 8.8.8.8) is given to Sluice only, through a private mount namespace; use it when the
# machine's resolver is unusable (WSL behind a Windows Sluice that fakes DNS answers).
#
# Expected: the relay listens on 127.0.0.1 and [::1] only; curl -> through the test SOCKS5 server (socks.log shows
# CONNECTs, including "example.com:80" by name, and the IPv6 address when the machine has IPv6), wget -> blocked,
# python -> direct, getent -> 198.18.x.x, the nftables table gone after stop.
set -u
CLI=$(realpath "$1"); PROFILE=$(realpath "$2"); DNS=${3:-}
HERE=$(dirname "$(realpath "$0")")
U=$(getent passwd 1000 | cut -d: -f1)
W=$(mktemp -d /tmp/sluice-livetest.XXXX)
cd "$W"
echo "== work dir $W, test user $U"

sudo -u "$U" python3 "$HERE/test-socks5.py" 1080 > socks.log 2>&1 & SOCKS=$!
RUN="exec dotnet $CLI/Sluice.Cli.dll $PROFILE --intercept --socks 127.0.0.1:10808 --log debug --duration 45"
if [ -n "$DNS" ]; then
  printf "nameserver %s\n" "$DNS" > resolv.test
  unshare -m bash -c "mount --bind $W/resolv.test /etc/resolv.conf; $RUN" > sluice.log 2>&1 & SLUICE=$!
else
  bash -c "$RUN" > sluice.log 2>&1 & SLUICE=$!
fi
sleep 5

echo "== nft ruleset"; nft list table inet sluice 2>&1 | sed 's/^/   /'
RELAY=$(nft list table inet sluice 2>/dev/null | sed -n 's/.*l4proto tcp redirect to :\([0-9]*\).*/\1/p')
echo "== relay listeners on port ${RELAY:-?} (expect 127.0.0.1 and [::1] only)"
ss -ltnH "sport = :${RELAY:-0}" | awk '{print "   " $4}'
echo "== curl http://1.1.1.1 (rule: via socks)"
sudo -u "$U" curl -sS -m 10 -o /dev/null -w "   http %{http_code} in %{time_total}s\n" http://1.1.1.1/
echo "== curl https://1.1.1.1 (rule: via socks)"
sudo -u "$U" curl -sS -m 10 -o /dev/null -w "   http %{http_code} in %{time_total}s\n" https://1.1.1.1/
echo "== curl -6 http://[2606:4700:4700::1111] (rule: via socks; relay on ::1)"
if ip -6 route get 2606:4700:4700::1111 >/dev/null 2>&1; then
  sudo -u "$U" curl -6 -sS -m 10 -o /dev/null -w "   http %{http_code} in %{time_total}s\n" "http://[2606:4700:4700::1111]/"
else
  echo "   skipped (no IPv6 route)"
fi
echo "== wget http://1.1.1.1 (rule: blocked)"
sudo -u "$U" wget -q -T 5 -t 1 -O /dev/null http://1.1.1.1/ && echo "   wget SUCCEEDED (bad)" || echo "   wget failed (expected)"
echo "== python urllib (rule: Default = direct)"
sudo -u "$U" python3 -c "import urllib.request
try: print('   python', urllib.request.urlopen('http://1.1.1.1/', timeout=10).status)
except Exception as e: print('   python', e)"
echo "== getent hosts example.com (expect 198.18.x from Sluice)"
sudo -u "$U" getent hosts example.com | sed 's/^/   /'
echo "== raw TXT query to 8.8.8.8 (forwarded; reply must come from 8.8.8.8:53)"
sudo -u "$U" python3 - <<'PY'
import socket, struct
q = struct.pack(">HHHHHH", 0x4242, 0x0100, 1, 0, 0, 0) + b"\x07example\x03com\x00" + struct.pack(">HH", 16, 1)
s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM); s.settimeout(5)
s.sendto(q, ("8.8.8.8", 53)); r, a = s.recvfrom(4096)
print("   reply from", a, "id ok" if r[:2] == q[:2] else "id mismatch")
PY
echo "== curl http://example.com by name (fake IP -> relay -> socks by hostname)"
sudo -u "$U" curl -sS -m 15 -o /dev/null -w "   http %{http_code} in %{time_total}s\n" http://example.com/
echo "== 20 parallel curls (process lookup speed)"
start=$(date +%s.%N); pids=""
for i in $(seq 20); do sudo -u "$U" curl -sS -m 10 -o /dev/null http://1.1.1.1/ & pids="$pids $!"; done; wait $pids
echo "   done in $(echo "$(date +%s.%N) - $start" | bc)s"

kill -INT $SLUICE; wait $SLUICE
echo "== after stop: table present? $(nft list table inet sluice >/dev/null 2>&1 && echo 'YES (bad)' || echo no)"
kill $SOCKS
echo "== socks.log"; sort socks.log | uniq -c | sed 's/^/   /'
echo "== sluice.log (decisions)"; grep -E "Capture|SYN|blocked|DNS|rror|open through|direct" sluice.log | head -60 | sed 's/^/   /'
