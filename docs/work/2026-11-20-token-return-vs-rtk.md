# Research: what raises ai-proxy's token return, and how it compares with rtk

**Date:** 2026-11-20
**Question:** Using every local data source (ai-proxy gain, content and capture stores, and rtk's own proxy ledger), what limits the token reduction ai-proxy gets today, what would raise it, and how does it compare with rtk's proxy?

Follows [2026-11-19-token-reduction-performance.md](2026-11-19-token-reduction-performance.md).
All data was read on the owner's Mac on 2026-11-20 at about 19:00 UTC, with ai-proxy 0.30.0 and
rtk 1.2.0. ai-proxy figures are characters ÷ 2.89; rtk figures are bytes ÷ 4. So compare the
percentages between the two tools, never the absolute token counts. The two tools also ran in
different windows (rtk 2026-10-17 to 10-27, ai-proxy 2026-11-11 to 11-20), on different work.

```chart:bars
title: gross reduction on Claude Code traffic, percent of body
rtk llm-proxy (10-17..10-27): 7.1
ai-proxy (11-11..11-20): 1.9
```
```chart:matrix
kind of previewed output, blocks, avg tokens, recalled %
read (numbered file), 72, 6524, 72
diff, 19, 5635, 79
json, 18, 7702, 56
other text, 157, 4902, 51
grep (path:line), 37, 4469, 24
```
```chart:matrix
lever, size of the prize, cost, who decides
lower CCR floor to ~300 tokens, ~4.8% of Claude body (sample), budget must scale, ADR 0005 change
skip read and diff previews, most of 2.7M follow-up tokens, small, ADR 0005 change
CCR for Chat Completions (OpenRouter), ~20% of pi bodies, new relay, new ADR
Codex compression, 50% of all tokens, large, reverses ADR 0012 rule 5
capture on for one week, measures all of the above, none, owner switch
```

## Findings

### F1 — Totals moved little since 11-19: 7,749 requests, 0.7% gross, billed balance 325K [MEASURED]

Claude: 6,798 requests, 160.9M original, 3.1M saved (1.9%). The "codex" row: 951 requests,
276.1M original, 60.2K saved (0.0%). The billed balance (original − forwarded − follow-up) is
325,374 tokens. Daily saved minus follow-up: 11-19 +135K, 11-20 −6K (72 requests so far).

**Evidence:** `ai-proxy gain --text`, 0.30.0, whole store. Daily rows from `sqlite3 -readonly ~/.ai-proxy/gain.db`
grouping `kind='request'` (sum `$.Totals.EstimatedSavedTokens`) and `kind='followup'` (sum
`$.EstimatedTokens`) by `date($.At)`.

### F2 — The "codex" row mixes two clients: Codex and the pi agent on OpenRouter [MEASURED]

`GainHarness.Of` labels every `openai` provider row "codex". 221 of those rows went through the
content pipeline with 56,677,081 original tokens. The content store holds exactly 221 DeepSeek
Chat Completions bodies (model `deepseek/deepseek-v4.1-flash-1017`, tools `read`, `bash`, `edit`,
`message-bus`) totalling 56,677,160 tokens. The match within 80 tokens says those rows are the pi
agent on OpenRouter, not Codex. The other 730 rows (219.4M) are `unchanged`, 723 of them on the
`passthrough` pipeline.

**Evidence:** `src/AiProxy.Core/Gain/GainHarness.cs:15-22`; `sqlite3 -readonly ~/.ai-proxy/gain.db`
grouping `provider='openai'` request rows by `pipeline, layer`; a Python read of
`content.db` `content_request.original` grouped by `$.model`.

### F3 — Codex gets 0% because chatgpt.com is record-only by decision, not by a defect [READ]

ADR 0012 rule 5 says harness-only hosts are never compressed or rewritten, and `chatgpt.com`
is the one harness-only host. This answers 11-19 F9. The 723 passthrough rows (218.8M, about 50%
of all original tokens) are this traffic. Raising Codex return means reversing an accepted ADR.

**Evidence:** `docs/adr/0012-record-codex-chatgpt-subscription-traffic.md:34`;
`src/AiProxy.Core/Providers/ProviderHostCatalog.cs:24` (`["chatgpt.com"] = new(Dialect.OpenAi, HarnessOnly: true)`);
`src/AiProxy/Hosting/Integration/ProviderRequestInspector.cs:408`.

### F4 — CCR runs only on Anthropic streamed `/v1/messages`; pi traffic on OpenRouter gets reducers only [READ]

`CcrPlan` forwards without CCR for any other dialect or path. The pi bodies carry 39.7% of their
size in tool output, 38.4% in blocks of 300 tokens or more, but saved 60K of 56.7M (0.1%).

**Evidence:** `src/AiProxy/Ccr/CcrPlan.cs:72`. Tool-output share from
`scratchpad/levers.py` over `content.db` (221 DeepSeek bodies, `role: tool` messages).

### F5 — rtk's proxy cut 7.1% of Claude Code request size; ai-proxy cuts 1.9% [MEASURED]

rtk, `claude-code` client: 16,271 requests, 2,333.5M forwarded estimate, 178.6M saved, so
178.6 / (2,333.5 + 178.6) = 7.1%. CCR made 177.4M (99.3%) of that. Both figures are gross:
neither subtracts the follow-up requests a recall causes. rtk's `tokens_in` is the forwarded body
at bytes ÷ 4, not a provider count.

**Evidence:** `sqlite3 -readonly "~/Library/Application Support/rtk/llm-proxy/rtk-proxy.db"`
summing `usage_records` by `provider, client`. Meaning of `tokens_in`:
`~/RiderProjects/rtk/proxy/src/forward.rs:101-104,189,234`, `proxy/src/context_analysis.rs:43`.
`rtk gain` "Trimmed in flight: 178.6M removed across the 9996 req it touched".

### F6 — The gap is coverage: rtk previewed 59% of requests, ai-proxy 6% [MEASURED]

rtk had `saved_ccr > 0` on 9,286 of 15,628 successful Claude Code requests. ai-proxy has
`ccr.preview` on 420 of 6,798 Claude requests. Both proxies re-preview every old tool output on
each request, so one elided block saves tokens on every later turn of the session. rtk booked
177M saved from 2,966 distinct blocks that held 1.65M tokens once.

**Evidence:** rtk: `usage_records` `sum(saved_ccr>0)` where `status<400`, and `ccr_decisions`
`count(*), sum(tokens_before-tokens_after)`. ai-proxy: `gain_event` rows with
`rule like '%ccr.preview%'`. ai-proxy walks every tool output in the body each request:
`src/AiProxy.Core/Content/ContentCompressionPipeline.cs:48-87`.

### F7 — The cause of the coverage gap is the size floor: 300 tokens in rtk, 3,000 in ai-proxy [READ]

ai-proxy previews a region only when its reduced text is 3,000 estimated tokens or more, and a
preview may hold 1,500. rtk's smallest elided block was 300 tokens and its average 987. Most rtk
blocks would never qualify in ai-proxy.

**Evidence:** `src/AiProxy.Core/Content/Ccr/CcrOptions.cs:31-42` (`MinimumTokens` 3,000,
`BudgetTokens` 1,500); `src/AiProxy.Core/Content/Ccr/CcrPreviewer.cs:22`. rtk:
`ccr_decisions` `min(tokens_before)` 300, `avg(tokens_before)` 987.

### F8 — In stored Claude bodies, tool output of 300 to 3,000 tokens is 8.6% of body size [MEASURED]

Over 952 stored Claude request bodies (65.1M tokens): blocks under 300 tokens are 1.7%,
300–1,000 are 3.8%, 1,000–3,000 are 4.8%, and 3,000+ (today's CCR target) are 6.0%. Tool output
in total is 16.3%. The rest is system prompt, tool definitions, user text, thinking and tool
calls, which no reducer touches.

**Evidence:** `python3 -I scratchpad/levers.py ~/.ai-proxy/content.db` (session scratchpad;
reads `content_request.original`, counts `tool_result` and `role: tool` text, chars ÷ 2.89).

### F9 — Lowering the floor to about 300 tokens would add roughly 4–5% of Claude body size, before follow-ups [INFERRED]

From F8 (8.6% of body in 300–3,000 blocks) times rtk's measured removal on such blocks
(2.93M → 1.28M, 56%). Two limits apply. The content store keeps only requests some reducer
changed, and those average 68K tokens against 23.7K for all Claude requests, so the sample
leans to long sessions and overstates the share. And the preview budget (1,500) is larger than
most blocks in that range, so the budget must scale with block size or nothing is saved.

### F10 — Read and diff previews are recalled most of the time; grep output rarely [MEASURED]

Of 303 previewed originals, 166 were recalled at least once (55%). By kind: numbered file reads
72% (113 recall calls), diffs 79%, JSON 56%, other text 51%, grep `path:line` output 24%. rtk
recalled 283 of 2,966 blocks (9.5%), and 2,918 of its blocks were Bash output. Each recall in
ai-proxy causes a follow-up that re-sends the whole request (about 9.8K tokens on average, 11-19
F5), so a preview the model reads back costs more than it saved.

**Evidence:** Python over `content.db` `ccr_original` (kind from the first 20 lines) joined to
`gain.db` `kind='recall'` rows on `$.PreviewId`. rtk: `ccr_decisions` grouped by `tool`,
`sum(retrieve_count>0)`.

### F11 — Leaving file reads and diffs out of CCR would remove most follow-up cost for little saving loss [INFERRED]

From F10: reads and diffs are 91 of 303 blocks but 136 of 296 recall calls. rtk, which almost
never previewed reads, kept its recall rate at 9.5%. Not tested by replay; the saving those
91 previews made was not split out.

### F12 — ai-proxy can already record provider usage, cache reads included; it is switched off [MEASURED]

0.7.0 added response usage capture (`input_tokens`, `cache_read_input_tokens`,
`cache_creation_input_tokens`) behind `PROXY_CAPTURE=on`. `capture.db` has 0 rows. rtk got
provider usage from the same Claude Code subscription traffic (cache_read 3,558.8M,
cache_creation 80.1M, 97.8% hit). So the 11-19 blocker "no paid API credential" does not stop a
billed measurement: subscription responses carry `usage`.

**Evidence:** `sqlite3 -readonly ~/.ai-proxy/capture.db "select count(*) from capture_request"` → 0;
`capture_response` schema; `src/AiProxy.Core/Capture/ResponseUsageReader.cs:12`;
`docs/changelog/README.md:68`. rtk: `usage_records` sums by client.

### F13 — Preview ids are content hashes, so a repeated preview should not break the prompt cache [READ]

The id is the first 16 hex characters of the original's SHA-256. Whether the whole preview text
is byte-identical across turns was not checked; capture data (F12) would show it as cache reads.

**Evidence:** `src/AiProxy.Core/Content/Ccr/CcrPreview.cs:6`.

### F14 — After follow-ups, rtk's net was probably about 5%, not 7.1% [INFERRED]

rtk logged 353 recovery calls. At its average forwarded body (143K) that is about 50.6M re-sent,
or 28% of its 178.6M saving, leaving about 5.1% net. This assumes a recovery re-sends a body of
average size, which rtk's ledger does not record.

### F15 — Whether the two windows had comparable work is not known [UNVERIFIED]

The rtk window had 16K requests in about 5 busy days; the ai-proxy window 7.7K in 6. Projects,
session lengths and the rtk CLI hook state (which shrinks Bash output before either proxy sees
it) were not compared.

## Still open

- Turn `PROXY_CAPTURE=on` for one working week. It gives full bodies of all traffic (fixes the
  sample bias in F8 and F9) and provider-billed usage with cache reads (replaces the 2.89
  estimate, settles 11-19 F13 and F13 here).
- Replay the captured week through a build with `MinimumTokens` 300 and a size-scaled budget,
  and with reads and diffs excluded, to turn F9 and F11 into measured numbers.
- Split the gain report's "codex" row by host (chatgpt.com, openrouter.ai, api.openai.com) so F2
  stops hiding the pi traffic.
- CCR for Chat Completions (F4) and for Codex (F3) each need a decision record first; F3 reverses
  ADR 0012.
- 11-19 open items still open: the 11-14 to 11-17 gap, and why the balance turned positive on 11-13.

### Status of these items on 2026-11-21

- Capture is on since 2026-11-20 19:32 UTC and stays on; the one-week window ends about 2026-11-27. Billed usage with cache reads is in `capture_response`.
- Replay is built (`ai-proxy replay`, 0.32.0) and the floor was set from it (0.33.0): floor 1,000 with a budget of `min(1,500, 0.4 x block)` and no previews for numbered reads and diffs under 6,000 tokens. Floor 300 was rejected because its net is negative at 0.90 follow-ups per preview. See [2026-11-21-replay-baseline.md](2026-11-21-replay-baseline.md) and the ADR 0015 grid.
- The "codex" row is split by host (0.31.0, ADR 0014 amendment).
- CCR for Chat Completions on OpenRouter is decided (ADR 0016) and shipped (0.34.0).
- Codex (F3) is still open: the reversal of ADR 0012 rule 5 waits for a replay of captured chatgpt.com bodies, and capture has none yet. One Codex session through the proxy with capture on unblocks it. The switch will ship off.
- The 11-19 items (the gap and 11-13) are closed in [2026-11-20-gain-open-items.md](2026-11-20-gain-open-items.md).
- None of 0.31.0 to 0.34.0 runs live until the NuGet publish is approved; the live proxy is 0.30.0.

