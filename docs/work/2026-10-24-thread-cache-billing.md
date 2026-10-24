# Research: how captured Claude Code turns are billed, and where byte stability still matters

Date: 2026-10-24. Step: `cache-stability-probe` (whole-request plan), passive arm only. The active
arm replays requests upstream and is billed; it waits for an owner budget.

Data: `~/.ai-proxy/capture.db` on the owner's M4 (darwin arm64), `capture_request` joined to
`capture_response`, 2026-10-23 18:06 to the cutoff 2026-10-24 19:30. 679 `/v1/messages` turns carry
usage, from 4 Claude Code sessions. Re-run with `python3 scripts/thread_usage.py --until
2026-10-24T19:30:00` (`--json` for the full rows). The script prints counts and short hashes only, never content.

## Findings

### F1 — Thread history is billed as cache reads, and each new block is written once [MEASURED]

On consecutive turns of one thread, cache read equals the previous turn's cache read plus its
cache creation, to the token: 30,697 + 50,875 = 81,572, then 81,572 + 1,744 = 83,316, and so on.
That identity links 509 of the 665 turns that read the cache, with no response id needed (the
capture has none). So a thread turn bills its whole history as cache reads and its new blocks as a
cache write; uncached input is 2 to 4 tokens at the median. This answers the first look's F5.

Over the 478 thread turns: 89,167,411 tokens read from cache, 1,796,957 written, 1,046 uncached,
378,632 output. Reads are 49 times the writes.

**Evidence:** `python3 scripts/thread_usage.py`; the identity is `link()` in that script, tested in
`scripts/test_thread_usage.py`.

### F2 — A block compressed on first send would be saved again on every later turn [INFERRED]

From F1: a token removed from a block on turn k is one token less written at k and one token less
read on each of turns k+1 to n. In the longest thread (167 turns), a block sent near the start is
read about 160 more times. For a thread client this makes first-send compression worth far more
than the per-request saving the scoring harness measures. The weight of a read against a write is
a price ratio from the provider's pricing page; it is not checked here.

### F3 — Along a thread only the billing header changes in the system prompt [MEASURED]

Over 509 consecutive pairs of turns in rebuilt threads, the only system block that changed is
block 0 (477 times): a short `x-anthropic-billing-header` text with a per-request version suffix and
no `cache_control`. With block 0 left out, no system block changed. The block count changed in 32
pairs. A thread `continue` request carries only that header block; the full system prompt goes
with `create`. Caching still hits behind a changing first block (F1), so the server does not key
the cache on that header. That last point is inferred, not read in any document.

**Evidence:** `system` and `system_after_header` in `python3 scripts/thread_usage.py --json`.

### F4 — Stateless side calls re-send a cached prefix, so their bytes must stay stable [MEASURED]

Side calls without a `thread` re-send their whole prompt. 43 of 47 `claude-haiku-5-5` calls read the
same 6,234 cached tokens and wrote none. 121 of 125 `claude-sonnet-5` calls read and wrote, with a
growing read: a re-sent, growing transcript behind a cached system prompt of about 137,000
characters. For these, any rewrite that is not byte-identical every time would turn reads into
writes. Thread clients do not have this problem for blocks they send once (first look F4).

### F5 — 156 cache-reading turns did not link [MEASURED]

These are mostly stateless side calls, whose read is their own prefix and not a previous total, plus
thread turns where the identity broke. No break was traced to a cause here: server-side context
edits (`context_management`) and cache expiry are both candidates, neither checked.

## What this changes

- For thread clients, compress on first send and leave already-sent content alone. The saving
  compounds over the thread (F1, F2). The cost of a cache miss is low, since a thread client does
  not re-send history.
- For stateless clients, a rewrite must be a pure function of the region's own bytes (F4).
- The system prompt of a thread is not a compression target after `create` (F3).

## Still open

- Active arm: does a deterministic rewrite keep a stateless call's cache reads, and does a
  non-deterministic control show the miss? Does adding the retrieval tool mid-thread break the
  thread's cache? These need billed replays and an owner budget.
- Why 156 turns do not link (F5).
- The read-to-write price ratio for F2, from the provider's pricing page. Belongs to `cache-strategies`.
