# Minimal SOCKS5 (no auth, CONNECT) for the Linux capture test; logs each target.
import socket, struct, sys, threading
def pipe(a, b):
    try:
        while (d := a.recv(65536)): b.sendall(d)
    except OSError: pass
    finally:
        for s in (a, b):
            try: s.shutdown(socket.SHUT_RDWR)
            except OSError: pass
def handle(c):
    try:
        c.recv(262); c.sendall(b"\x05\x00")
        h = c.recv(4); atyp = h[3]
        if atyp == 1: host = socket.inet_ntoa(c.recv(4))
        elif atyp == 3: host = c.recv(c.recv(1)[0]).decode()
        else: host = socket.inet_ntop(socket.AF_INET6, c.recv(16))
        port = struct.unpack(">H", c.recv(2))[0]
        print(f"SOCKS CONNECT {host}:{port}", flush=True)
        u = socket.create_connection((host, port), timeout=10)
        c.sendall(b"\x05\x00\x00\x01" + b"\x00" * 6)
        threading.Thread(target=pipe, args=(u, c), daemon=True).start(); pipe(c, u)
    except Exception as e:
        print(f"SOCKS error {e}", flush=True); c.close()
s = socket.socket(); s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
s.bind(("127.0.0.1", int(sys.argv[1]))); s.listen(64)
while True:
    c, _ = s.accept(); threading.Thread(target=handle, args=(c,), daemon=True).start()
