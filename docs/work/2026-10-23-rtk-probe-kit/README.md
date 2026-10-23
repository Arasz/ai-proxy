# 2026-10-23-rtk-probe-kit/

These are the scripts behind
[2026-10-23-rtk-proxy-ccr-threshold-and-chaining.md](../2026-10-23-rtk-proxy-ccr-threshold-and-chaining.md).
They use only the Python standard library, and every one binds to 127.0.0.1.

## Contents

| File | What it is |
|---|---|
| `fake_upstream.py` | `fake_upstream.py <port> <dir>`. Answers like the Anthropic Messages API and saves each request body to `<dir>/NNN.json`. |
| `probe.py` | `probe.py <proxy-url> <bodies-dir> <size>[+<size>] ...`. Sends Bash tool results of the given character sizes, then reports each block's length and whether a CCR preview reached the fake upstream. |
| `send_marker.py` | `send_marker.py <port> <marker>`. Sends one request whose user text is the marker, using a key that is not valid. |
| `tunnel.py` | `tunnel.py <port>`. A plain CONNECT proxy that logs the target and passes bytes through without decrypting. |

Start a second rtk-proxy for each probe and give it its own databases. That way the live
instance and its stats stay untouched:

```bash
RTK_PROXY_DB=$tmp/rtk-proxy.db RTK_PROXY_ICM_DB=$tmp/mem.db \
  rtk-proxy start --bind 127.0.0.1:18080 --anthropic-upstream http://127.0.0.1:18999
```
