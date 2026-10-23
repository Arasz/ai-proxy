# Research: rtk-proxy's real CCR threshold, and whether rtk-proxy can sit in front of ai-proxy

**Date:** 2026-10-23
**Question:** At what tool-result size does rtk-proxy 1.0.1 replace a result with a CCR preview,
which setting controls that size, and can rtk-proxy's upstream calls pass through ai-proxy?

This settles F13 and F14 of
[2026-10-23-compression-state-model-readiness-and-rtk-pipeline.md](2026-10-23-compression-state-model-readiness-and-rtk-pipeline.md).
It is task-graph step `rtk-probes` of `aip-compression-and-rtk-replacement`.

Every probe ran against a second rtk-proxy started for the purpose, on a spare port, with its
own scratch databases. The live rtk-proxy on 8080, the operator's ai-proxy on 8081 and the
macOS system proxy were left running and unchanged. For F13 the probe instances forwarded to a
local fake upstream that saves each body it receives, so no request reached a provider and
nothing was billed. The scripts are in [2026-10-23-rtk-probe-kit/](2026-10-23-rtk-probe-kit/).

```chart:matrix
title: tool result reached the upstream as a CCR preview (1) or whole (0)
chars, rtk tokens, default, CCR_MIN_TOKENS=1000, tokens_gt=5000, BULK_MIN_TOKENS=1000
720, ~309, 0, -, -, -
740, 318, 1, 1, 1, 0
2000, 819, 1, 1, 1, 0
3000, 1207, 1, 1, 1, 1
```

## Findings

### F1 — rtk-proxy previews any tool result over about 310 tokens, not over 1,000 [MEASURED]

With the shipped configuration, one Bash tool result of 720 characters reached the upstream
whole and left no `ccr_decisions` row. At 740 characters (318 tokens by rtk's own count) it
arrived as a 700-character head+tail preview. Every larger size tested, up to 14,000
characters, arrived compressed. The smallest result rtk compressed was 314 tokens. The cut-off
therefore sits between roughly 309 tokens (720 characters, which rtk did not count because it
made no decision) and 314.

There are two preview shapes. Under about 1,000 tokens, `strategy=headtail` keeps about
280-300 tokens. Above that, results classed `flat-unique` or `repetitive` keep about 1,000
tokens (`tokens_after` 994-1,040).

**Evidence:** `probe.py` through a second `rtk-proxy start --bind 127.0.0.1:18080
--anthropic-upstream http://127.0.0.1:18999`, scratch `RTK_PROXY_DB` and `RTK_PROXY_ICM_DB`,
with the operator's `pipeline.toml` loaded (log: `pipeline=haiku-compaction`). Sizes 300-8,000
characters, then 720-880 in 20-character steps; `select tokens_before, tokens_after from
ccr_decisions`. Run once per size on 2026-10-23, MacBook Air M4. The payload is synthetic
compiler-warning lines, all unique, so `normalize` could not shorten it.

### F2 — The `tokens_gt = 1000` rule in pipeline.toml has no effect on the proxy [MEASURED]

A probe instance loaded a copy of `pipeline.toml` with the CCR rule changed to
`tokens_gt = 5000` and the pipeline renamed `probe-c`. Its startup log printed
`pipeline=probe-c`, which proves it read the edited copy. It still compressed the 740- and
2,000-character results exactly as the default did.

The comment at `~/.config/rtk/pipeline.toml:37` ("Results over 1,000 tokens become a head+tail
preview") therefore describes a rule the proxy does not obey.

**Evidence:** instance on 18082 started with `HOME` pointed at a scratch directory holding the
edited `Library/Application Support/rtk/pipeline.toml`; `rtk-c2.log` line 1; probes 740, 2000,
3000.

### F3 — `RTK_PROXY_CCR_BULK_MIN_TOKENS` is the setting that moves the threshold; `RTK_PROXY_CCR_MIN_TOKENS` does not [MEASURED]

With `RTK_PROXY_CCR_BULK_MIN_TOKENS=1000`, results of 740 and 2,000 characters (318 and 819
tokens) passed whole, and 3,000 characters (1,207 tokens) was still compressed. With
`RTK_PROXY_CCR_MIN_TOKENS=1000` instead, all three were compressed, the same as the default. The
live rtk-proxy sets neither variable (`ps eww` shows only `RTK_PROXY_DB` and `RTK_PROXY_ICM_DB`),
so it runs on the built-in bulk minimum.

What `RTK_PROXY_CCR_MIN_TOKENS` governs was not found. The binary also carries
`RTK_PROXY_CCR_READ_MIN_TOKENS`, `RTK_PROXY_KEEP_RECENT_TURNS` and `RTK_PROXY_RETIRE_MIN_TOKENS`;
none was probed.

**Evidence:** instances on 18081 (`CCR_MIN_TOKENS=1000`) and 18083 (`CCR_BULK_MIN_TOKENS=1000`),
each with its own fake upstream; `strings ~/.local/bin/rtk-proxy | grep MIN_TOKENS`.

### F4 — `tokens_compressed` in the log is per request, summed over blocks [MEASURED]

A request carrying two 3,000-character results logged `tokens_compressed=2443
blocks_compressed=2`, and `ccr_decisions` holds one row per block, at 1,222 and 1,221 tokens.
The threshold in F1 applies per block: each of the two blocks was judged on its own size.

**Evidence:** `rtk-a.log`, the `blocks_compressed=2` line; `ccr_decisions` rows for session
`ebd546fefb26d542`.

### F5 — rtk-proxy ignores the macOS system proxy, so it never reaches ai-proxy on its own [MEASURED]

A fresh rtk-proxy started with no proxy variables, while the system web and secure web proxy
pointed at ai-proxy on 8081. It sent a probe with a unique marker to the real
`api.anthropic.com` using a key that is not valid. Anthropic answered 401 `invalid x-api-key`,
so the call reached the provider, and ai-proxy's capture store has no `/v1/messages` row with
that marker. The call went direct.

The capture store does hold an Anthropic row containing the marker text. It is
`/v1/code/sessions/.../worker/events`, the event stream of the Claude Code session that ran
the probe, which carried the command line typed. It is not rtk-proxy's request. Anyone
measuring that corpus later should exclude rows whose body contains `F14-PROBE-`.

**Evidence:** `send_marker.py 18084 F14-PROBE-E-7c1d9a`; `sqlite3 -readonly
~/.ai-proxy/capture.db "... where path='/v1/messages' and instr(cast(body as text),'F14-PROBE-')>0"`
returned no rows; `networksetup -getwebproxy` / `-getsecurewebproxy Wi-Fi` identical before and
after (`diff` clean).

### F6 — With HTTPS_PROXY set, rtk-proxy tunnels through a proxy, but rejects ai-proxy's inspection certificate [MEASURED]

Two instances set `HTTPS_PROXY`. Pointed at a plain CONNECT tunnel that does not decrypt, rtk-proxy
sent `CONNECT api.anthropic.com:443` and got Anthropic's 401 back. Pointed at ai-proxy on 8081,
it failed in 24 ms with `upstream forward failed ... error sending request` and answered the
client 502. ai-proxy captured nothing from it. Setting `SSL_CERT_FILE` to ai-proxy's root
certificate made no difference.

**Evidence:** `tunnel.py 18990` log; `rtk-f.log` and `rtk-h.log` WARN lines; `openssl pkcs12
-nokeys` gave the root's public part only (`CN=Titanium Root Certificate Authority`).

### F7 — Chaining Claude Code → rtk-proxy → ai-proxy does not work today [INFERRED]

From F5 and F6: rtk-proxy only uses a proxy it is told about through `HTTPS_PROXY`. Through
ai-proxy, its TLS client fails exactly where the plain tunnel succeeded, so the failure is in
certificate trust. The binary links rustls, and `SSL_CERT_FILE` was ignored, which fits a
built-in root set (webpki roots) that no setting extends. Running both proxies stacked would
need either a change to rtk-proxy or ai-proxy passing `api.anthropic.com` through uninspected
for that one client. Neither is worth building, given that ai-proxy is meant to replace
rtk-proxy outright. Side by side on separate ports (`PROXY_PORT`) is the only arrangement that
works.

### F8 — What the 310-token threshold costs is not measured [UNVERIFIED]

F1 means rtk-proxy previews far more tool results than its configuration claims. Whether that
loses answers, and how often the model then calls `rtk_retrieve`, was not measured.
`ccr_decisions.retrieve_count` on the live database would show the retrieval rate. The scoring
harness (step `scoring-harness`) measures what a preview keeps.

## Still open

- **The exact default bulk minimum.** It lies between roughly 309 and 314 tokens, and only the
  rtk-proxy source would give the number. It doesn't change any decision.
- **What `RTK_PROXY_CCR_MIN_TOKENS` and the other three variables do.** Each could get one
  probe of the same kind as F3.
- **The retrieval rate on real traffic** (F8). Run `select count(*), sum(retrieve_count>0) from
  ccr_decisions` against the live `rtk-proxy.db`.
- **Whether to fix the live config.** The comment at `pipeline.toml:37` is wrong, and the
  `[[proxy.request.tool_result]]` rule does nothing. If 1,000 tokens is the real intent, set
  `RTK_PROXY_CCR_BULK_MIN_TOKENS=1000` on the rtk-proxy service. That is the owner's call, so
  this record changes nothing.
- **Whether a rustls build with native roots would trust ai-proxy's root** through the macOS
  keychain. It doesn't matter while rtk-proxy is being replaced.
