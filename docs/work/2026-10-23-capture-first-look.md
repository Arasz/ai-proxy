# Research: first look at captured Claude Code requests, and what it changes in the cache plan

**Date:** 2026-10-23
**Question:** What do the first `PROXY_CAPTURE` requests show about where request characters go, how
much of each request is re-sent, and what that means for compressing the whole request without
breaking the prompt cache?

## Findings

### F1 — Claude Code on Opus 5.5 keeps the conversation on the server [MEASURED]

Of 67 captured `/v1/messages` requests, 60 are `claude-opus-5-5`. 59 of those carry
`thread.type = continue` with a `previous_message_id`, and one carries `thread.type = create`. A
continuation sends only the new blocks, mostly tool results. No request carries an assistant turn,
and the 17 tool definitions were sent once, in the `create` request. The 7 remaining requests are
Haiku side calls without a `thread` field, which send their whole prompt each time.

**Evidence:** `python3 scripts/capture_regions.py --json` over `~/.ai-proxy/capture.db` on the
owner's M4 (darwin arm64), 2026-10-23, one Claude Code session routed through ai-proxy with
`HTTPS_PROXY`; body shape read by key and type only (`models`, `modes`, per-message block types).

### F2 — 89% of the characters sent were sent for the first time [MEASURED]

432,011 of 485,707 characters were new on their first send (counted by a hash of region kind plus
content, ignoring `cache_control`). Almost all re-sent characters are in the top-level system
prompt: 97,710 sent, 44,620 new. User text re-sent 606 characters. Tool results, tool definitions
and system messages inside the thread were never re-sent.

**Evidence:** same run as F1; `kinds.*.sent_chars` and `kinds.*.new_chars`.

### F3 — The request is split roughly 27/20/20/16/16 across five region kinds [MEASURED]

Share of characters sent: tool results 27.3%, top-level system prompt 20.1%, system messages inside
the thread 19.7%, user text 16.3%, tool definitions 16.1%, tool additions 0.4%. This differs from the
10.5% tool-result share the model gate used. That figure came from rtk token counts, taken while rtk
was compressing and while Claude Code still re-sent history, so the two do not compare directly.

**Evidence:** same run as F1. The 10.5% figure: `docs/work/2026-10-23-model-gate-prose-share.md`.

### F4 — For a thread client, a region compressed on first send is never replayed by the client [INFERRED]

From F1 and F2: the client sends each continuation block once, and the server holds it from then
on. A deterministic compressor applied on first send therefore cannot produce a later byte
mismatch from this client, because the client never sends the region again. That leaves three
places where byte stability still matters:
- the top-level system prompt, which is re-sent (F2)
- stateless side calls, which re-send everything (F1)
- other clients that re-send history

Assistant turns are not a compression target for a thread client, because they never pass through
the proxy on the request side.

### F5 — Whether compressing new content also cuts what the server re-reads per turn is unknown [UNVERIFIED]

If the server bills the thread history as cache reads, then a region compressed on first send
would lower `cache_read_input_tokens` on every later turn of the thread, which would be the larger
saving. Nobody has measured it. The capture did not record response usage until
`aip-capture-usage-and-pipeline-latency` added `capture_response`.

### F6 — Another agent client on this machine re-sends the full history every turn [READ]

A sibling record measured `pi-coding-agent` 1.4.0 with `pi-ai` 1.4.0: the first request carried 2
messages and the request after one tool call carried 4 (system, user, assistant tool call, tool
result). The Anthropic adapter marks `cache_control` on the system prompt, the last tool definition
and the trailing block. The OpenAI adapter sends the full array plus an optional session-based
`prompt_cache_key`. So the stateless pattern described in F4 is not hypothetical here. For such a
client every region already sent comes back each turn, and the byte-for-byte rule binds all of them.
The same record leaves open how widely agents use delta-only, server-side threads. F1 is one such
case.

**Evidence:** `pi-badger-integration/docs/work/2026-10-23-caching-across-llm-providers.md:29-49`
(measurement) and `:73-91` (adapters), sibling repository on the owner's machine.

### F7 — Prefix caching makes any byte change invalidate everything after it [INFERRED]

A provider reuses computed attention state only for a prompt prefix identical from token 0, so a
change at position *k* forces recomputation from *k* onward. Anthropic marks cache breakpoints with
`cache_control`. OpenAI and Gemini cache matching prefixes without being asked, so the cache rule
binds every inspected provider, not only Anthropic. Reasoned from how causal attention works, and
from a chat answer the owner shared on 2026-10-23. That answer's specific numbers (minimum
cacheable tokens, write multipliers, discounts, TTLs) are not checked against provider
documentation and are not used here.

## What this changes in the cache plan

`aip-whole-request-compression-and-retrieval` revision 1 takes these conclusions in:
- `cache-stability-probe` starts passive. It reads `cache_read_input_tokens` from `capture_response`
  per thread turn before any billed replay. Its replay arm covers stateless calls and the
  re-sent system prompt, where byte stability is still at stake (F4), and history-resending
  clients such as pi-agent (F6).
- `scope-design` treats thread mode as the main case: assistant turns are out of scope, new blocks
  are compressed on first send, and the re-sent system prompt and stateless calls keep the
  byte-for-byte rule.
- The tool-result-only ceiling from the model gate is void for this traffic (F3).

## Still open

- F5: whether thread history is billed as cache reads, and whether compressing new blocks lowers
  them on later turns. Needs `capture_response` data over several sessions.
- What part of the top-level system prompt changes between requests (half of it repeats, F2).
  Measure by block, before choosing whether it is compressed or left alone.
- What other clients do (Codex, Copilot, OpenRouter users). This capture is one Claude Code session
  over a few minutes.
- How `context_management.edits`, sent on every Opus request, changes what the server keeps. Not
  read yet.
- The provider numbers in F7: check each against the provider's own documentation before quoting
  them.
