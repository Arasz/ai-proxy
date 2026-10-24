"""A plain CONNECT proxy that tunnels bytes without decrypting, logging each CONNECT target."""
import socket, sys, threading
def pipe(a, b):
    try:
        while (d := a.recv(65536)): b.sendall(d)
    except OSError: pass
    finally:
        for s in (a, b):
            try: s.shutdown(socket.SHUT_RDWR)
            except OSError: pass
def handle(c):
    head = b""
    while b"\r\n\r\n" not in head:
        chunk = c.recv(4096)
        if not chunk:
            c.close()
            return
        head += chunk
    target = head.split(b" ")[1].decode()
    print("CONNECT", target, flush=True)
    host, port = target.rsplit(":", 1)
    u = socket.create_connection((host, int(port)))
    c.sendall(b"HTTP/1.1 200 Connection Established\r\n\r\n")
    threading.Thread(target=pipe, args=(c, u), daemon=True).start(); pipe(u, c)
srv = socket.socket(); srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1); srv.bind(("127.0.0.1", int(sys.argv[1]))); srv.listen()
while True:
    c, _ = srv.accept(); threading.Thread(target=handle, args=(c,), daemon=True).start()
