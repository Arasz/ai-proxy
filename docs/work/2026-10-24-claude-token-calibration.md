# Research: estimating Claude prompt tokens from characters

**Date:** 2026-10-24
**Question:** With no Anthropic key and no offline Claude tokenizer, what chars-per-token ratio can
the reducer scoring and the capture reports use to state sizes in tokens, and how far can it be trusted?

## Findings

### F1 — Stateless Claude requests carry 2.89 characters per prompt token, with a 2.2% holdout error [MEASURED]

One ratio, the median of per-request `chars / prompt tokens`, fits 116 stateless requests. Prompt
tokens are `input + cache_read + cache_creation` as the provider reported them; chars are the
length of the decoded body text. Five-fold holdout (each request predicted from a fit that
excluded its fold) gives a median absolute error of 2.2%. The per-request ratio runs 2.57 to 2.98
between the 10th and 90th percentile; one haiku-4-5 row sits at 40 and does not move
the median.

**Evidence:** `python3 scripts/calibrate_tokens.py`, which wrote `scripts/tokens_calibration.json`:
`{"ratio": 2.8916, "rows": 116, "models": {"claude-sonnet-5": 76, "claude-haiku-5-5": 29,
"claude-opus-5-5": 6, "claude-sonnet-5-5": 3, "claude-haiku-4-5-20251001": 2}, "first":
"2026-10-24T13:19:10Z", "last": "2026-10-24T15:16:15Z", "holdout_median_abs_error": 0.022}`. Source:
`~/.ai-proxy/capture.db` (read-only), `/v1/messages` with status 200 and usage present, rows with a
top-level `thread` key and rows holding image or document blocks excluded. Python 3.15.0. The
capture store keeps growing, so a re-run gives slightly different rows.

### F2 — Thread-mode rows cannot calibrate chars per token [READ]

A Claude Code thread request has a top-level `thread` object and carries only the new turn, while
the reported input tokens include history the server holds. On the latest 10 captured rows the
chars-per-token figure ranged 0.21 to 2.8 for that reason. The calibration therefore uses stateless
rows only: 112 of 366 usable rows when first counted (the rest thread mode), 116 after the capture grew.

**Evidence:** `scripts/capture_regions.py` (`_mode`), `scripts/calibrate_tokens.py` (`select`), and
the research notes of this task (F4).

### F3 — A token figure is only meaningful for one tokenizer [READ]

Anthropic counts tokens under the tokenizer of the model asked. Opus 4.7 introduced a tokenizer
that counts about 30% more than earlier models for the same prompt; Fable 5.1, Mythos 5.1, Fable 5
and Mythos 5 share it. The docs do not say whether `claude-opus-5-5` or the 5.5 sonnet and haiku
models share it.

**Evidence:** https://platform.claude.com/docs/en/build-with-claude/token-counting

### F4 — Applying the ratio to claude-opus-5-5 traffic is not verified [UNVERIFIED]

The calibration rows are mostly sonnet-5 (76) and haiku-5-5 (29); only 6 are opus-5-5, while most
captured traffic is opus-5-5. The six opus rows have a median near 2.99 against 2.86 for sonnet-5,
too few to say the tokenizers match. If opus-5-5 counts more tokens per character, every token figure
here is too low for it. The method label says "estimated" for this reason.

### F5 — A generic BPE library or count_tokens is not a substitute [INFERRED]

tiktoken implements OpenAI's tokenizers and would count a different vocabulary, so its numbers
would be exact for the wrong model. Anthropic's `count_tokens` is free and exact but needs an API
key, and the owner ruled on 2026-10-24 that no key or paid call is available. A calibrated ratio
with a recorded error band is the remaining option. Reasoned from the two constraints; no tiktoken
count was run.

### F6 — Per-turn thread differencing would give exact new-turn tokens, but the data to link turns is not captured [INFERRED]

New tokens of turn n are `total(n) - total(n-1) - output(n-1)` for consecutive requests of one
thread. `capture_response` stores no response message id, so a request's
`thread.previous_message_id` cannot be matched to the response it follows. The identity itself is
unvalidated.

**Evidence:** `.schema capture_response` in `~/.ai-proxy/capture.db` lists no message id column.

### F7 — The error band holds for whole request bodies, not for a single payload [INFERRED]

The ratio was fitted on JSON request bodies, where newlines and quotes are escaped and the
system prompt and tool definitions dominate. The harness applies it to the raw text of one
payload, such as a build log. How such text tokenizes compared with a JSON body was not measured,
so the 2.2% band does not carry over to per-payload figures. Compression ratios are unaffected,
because one ratio scales both sides. Absolute token sizes per payload carry an unknown error.
Reasoned from F1's method and the harness's use of `tokens.estimate`.

## Still open

- Capture the response message id in `capture_response`, so thread differencing can be validated and
  can calibrate per content kind (log, build, json, prose, code).
- Calibrate on claude-opus-5-5 stateless rows once there are enough, and compare with sonnet-5.
- Per-kind ratios: one ratio hides that code and prose tokenize differently.
- The runtime gate in C# (per-type minimum lengths) stays in characters; it is out of scope here.
