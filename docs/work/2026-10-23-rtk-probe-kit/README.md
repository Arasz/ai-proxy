# 2026-10-23-rtk-probe-kit/

These are the scripts behind
[2026-10-23-rtk-proxy-ccr-threshold-and-chaining.md](../2026-10-23-rtk-proxy-ccr-threshold-and-chaining.md).
They use only the Python standard library, and every one binds to 127.0.0.1.

Start a second rtk-proxy for each probe and give it its own databases. That way the live
instance and its stats stay untouched:

```bash
tmp=$(mktemp -d)
RTK_PROXY_DB=$tmp/rtk-proxy.db RTK_PROXY_ICM_DB=$tmp/mem.db \
  rtk-proxy start --bind 127.0.0.1:18080 --anthropic-upstream http://127.0.0.1:18999
```
