# Research: the latency budget per 1,000 characters for size-normalized reducer scores

**Date:** 2026-10-24
**Question:** What value of `budget_per_kchar_s` should the reducer score use, so that latency is
judged relative to payload size and scores compare across payloads?

## Findings

### F1 — The latency score with an absolute budget does not compare payloads of different sizes [READ]

`latency_score` is `budget_s / (budget_s + latency_s)` with one `budget_s` of 1.0 second for every
payload. A 60 KB build log and a 575-character snippet are held to the same second. `kind_row`
also sums characters and takes the median latency across a kind's payloads, so the largest
payload decides the kind's score.

**Evidence:** `scripts/compression_score.py:34-36`, `scripts/reducer_scoring/preregistration.json:3`,
`scripts/reducer_scoring/scoring.py:88-93`, on main at `b359e50`.

### F2 — With a budget per 1,000 characters, payload size cancels out of the latency score [INFERRED]

With `budget = budget_per_kchar_s x original_chars / 1000`, the score becomes
`budget_per_kchar_s / (budget_per_kchar_s + latency_s / kchars)`. It depends only on the
reducer's time per 1,000 characters. A reducer exactly at the budget scores 0.5. Reasoned from F1's
formula.

### F3 — A provider's time to first byte is 0.039 s per 1,000 request characters at the median [MEASURED]

| | p10 | p50 | p90 |
|---|---|---|---|
| request size (kchars) | 18.2 | 42.6 | 198.5 |
| time to first byte (s) | 1.52 | 1.73 | 2.34 |
| time to first byte per kchar (s) | 0.009 | 0.039 | 0.109 |
| ai-proxy inspector (ms) | 0.27 | 0.37 | 1.08 |

Time to first byte stays nearly flat while request size varies about tenfold.

**Evidence:** `sqlite3 ~/.ai-proxy/capture.db` joining `capture_response` to `capture_request`
on `request_id`, rows with path `/v1/messages%` and status 200; `request_bytes`,
`stages."titanium.ttfb"` and `stages.inspector`. 93 requests on 2026-10-24 through ai-proxy 0.7.0
(`PROXY_CAPTURE=on`, compression off) on the owner's M4 (darwin arm64), mostly Claude Code on
`claude-opus-5-5` in thread mode. Request bytes are counted as characters.

### F4 — Rule-based reducers cost about 0.0002 s per kchar and model reducers 0.3 to 4.8 s [READ]

```chart:bars
title: median reducer seconds per 1,000 characters
shipped rules: 0.0002
rtk CCR: 0.0002
kompress int8: 0.3065
summarizer control: 1.1959
digest gpt stand-in: 4.7804
```

Medians over each candidate's scored payloads (575 to 60,672 characters), untouched payloads left
out. The real Haiku digest is not in this table.

**Evidence:** `scripts/reducer_scoring/results/report.json` on main at `b359e50`, field
`payloads[].latency_s / (original_chars / 1000)`.

### F5 — 0.04 s per kchar separates rule and model reducers, and the value mainly moves kompress [INFERRED]

| candidate | latency score at 0.01 | at 0.04 | at 0.11 |
|---|---|---|---|
| shipped rules | 0.98 | 0.995 | 0.998 |
| rtk CCR | 0.98 | 0.995 | 0.998 |
| kompress int8 | 0.03 | 0.12 | 0.26 |
| summarizer control | 0.01 | 0.03 | 0.08 |
| digest gpt stand-in | 0.002 | 0.008 | 0.02 |

Computed from F2's formula with F4's medians, at F3's p10, p50 and p90. The owner accepted 0.04,
the median, with {0.01, 0.04, 0.11} as the sensitivity set. A reducer at 0.5 then handles its part
as fast as the provider answers per character of request. The value is recorded as
`size_normalized_budget` in `scripts/reducer_scoring/preregistration.json`, before any run that
uses it.

## Still open

- F3 is one day of traffic, mostly one client in thread mode. The median moves with the size mix;
  re-derive it once `capture_response` holds several days and other clients.
- Characters, not tokens. A tokenizer-based budget would treat code and prose differently.
- The real `claude-haiku-5-5` digest latency is in PR #39, not in F4.
- Whether a reducer's latency adds to time to first byte one for one is assumed, not measured: it
  holds while the proxy reduces before forwarding, as it does today.
