# Research: how much ai-proxy reduces the tokens it sends upstream

**Date:** 2026-11-19
**Question:** Based on the gain statistics ai-proxy has collected, how much does it reduce the tokens sent to the model providers, after the cost of retrieval is counted?

All token counts below are estimates: measured character counts divided by 2.89 (see F10). Data is
the local gain store `~/.ai-proxy/gain.db` on the owner's Mac, ai-proxy 0.30.0, read on
2026-11-19 at about 10:00 UTC. It covers 2026-11-11 to 2026-11-19; there are no rows for
2026-11-14 to 2026-11-17.

```chart:bars
title: whole period, thousands of estimated tokens
saved on first request: 2950
retrieved text (recalls): 674
follow-up requests: 2637
billed balance: 248
```

```chart:matrix
day, requests, saved K, follow-up K, saved minus follow-up K
2026-11-11, 825, 225, 354, -130
2026-11-12, 3063, 1062, 1220, -159
2026-11-13, 1197, 381, 134, 246
2026-11-18, 1969, 1087, 780, 307
2026-11-19, 273, 195, 149, 47
```

## Findings

### F1 — The first request of each call is 0.7% smaller: 2.9M of 427.3M estimated tokens [MEASURED]

Across 7,325 model requests, the proxy forwarded 424.5M of 427.3M estimated tokens. That is the
gross reduction, before any retrieval cost. 31,668 other provider calls (telemetry, 1.7B bytes)
are not model requests and are not in these totals.

**Evidence:** `ai-proxy gain --text`, ai-proxy 0.30.0, owner's Mac, 2026-11-19 ~10:00 UTC, whole
store with no filters. Row `total`: Req 7,325, Orig 427.3M, Fwd 424.5M, Saved 2.9M, Saved% 0.7%.

### F2 — After retrieval follow-ups, the real balance is about 248K tokens, 0.06% of the original [MEASURED]

When the model asks for text the proxy hid, the proxy answers and sends the whole request upstream
again. Those 270 follow-up requests cost 2.64M estimated tokens. The billed balance (original minus
forwarded minus follow-ups) is 247,698 tokens. This is the number that answers the question; the
`Net` column (2.2M) does not, see F3.

**Evidence:** same `ai-proxy gain --text` run: "Billed balance (all requests): 247,698 tokens ...
follow-ups measured on 7,325 of 7,325 requests". Follow-up count and sum from
`sqlite3 -readonly ~/.ai-proxy/gain.db "select count(*), sum(json_extract(payload,'$.EstimatedTokens')) from gain_event where kind='followup'"`
→ 270, 2,637,249.

### F3 — The `Net` column leaves out follow-up requests, so it overstates the saving about 9 times [READ]

`Net` is saved minus retrieved text minus the injected tool definition. It does not subtract the
follow-up request bodies. The billed balance does. So `Net` 2.2M and billed balance 248K measure
different things, and only the second is a cost-against-doing-nothing figure.

**Evidence:** `src/AiProxy.Core/Gain/GainContracts.cs:51` (`EstimatedNetTokens => Saved - Retrieved - Tool`),
`:54` (`EstimatedBilledBalanceTokens => Original - Forwarded - FollowUp`). Follow-up body is the
whole re-sent request: `src/AiProxy/Ccr/CcrRelay.cs:234-238` and `:373-378`.

### F4 — 97% of the saving comes from the 394 requests where CCR previews ran [MEASURED]

Requests with `ccr.preview` in their rule path saved 2,859,224 tokens out of 40.95M. The other
6,936 requests saved 90,366 tokens out of 386.4M (0.02%). The plain reducers (JSON, Markdown,
blank lines, build and log filters) almost never shrink a request that CCR did not also touch;
the per-rule table in `ai-proxy gain --by-rule` looks bigger only because one request counts under
every rule on its path.

**Evidence:** `sqlite3 -readonly ~/.ai-proxy/gain.db` grouping `kind='request'` rows by
`rule like '%ccr.preview%'`, summing `$.Totals.EstimatedSavedTokens` and
`$.Totals.EstimatedOriginalTokens`. Same machine and time as F1.

### F5 — Retrieval follow-ups use up 89% of the CCR saving [MEASURED]

CCR saved 2.86M (F4); its follow-ups cost 2.64M (F2). 283 recalls read 674K tokens of hidden
text back. The average follow-up carries about 9.8K tokens. Of 270 follow-ups, 174 were round 1,
69 round 2, 21 round 3 and 6 round 4: about one recall loop in three goes past the first round.

**Evidence:** `sqlite3 -readonly ~/.ai-proxy/gain.db "select json_extract(payload,'$.Round'), count(*) from gain_event where kind='followup' group by 1"`
→ 1:174, 2:69, 3:21, 4:6. Recall total from `ai-proxy gain --text` (Recall 673.5K).

### F6 — The daily balance was negative on 11-11 and 11-12 and positive on every day since [MEASURED]

Saved minus follow-up tokens per UTC day: −130K, −159K, +246K, +307K, +47K (matrix above). On
the first two days the proxy cost more tokens than it saved; from 11-13 it saved more than it cost.

**Evidence:** two `sqlite3 -readonly ~/.ai-proxy/gain.db` queries grouped by
`date(json_extract(payload,'$.At'))`: request rows summing `$.Totals.EstimatedSavedTokens`,
follow-up rows summing `$.EstimatedTokens`.

### F7 — Why the balance turned positive on 11-13 is not known [UNVERIFIED]

Follow-up tokens per CCR request fell from about 8.9K (11-11) and 7.4K (11-12) to 3.3K (11-13)
and 6.0K (11-18). A release, a change in workload, or which projects were active could each
explain it. No one has matched the days to the releases or to the projects.

### F8 — Claude Code traffic is reduced 1.9%; Codex traffic 0.02% [MEASURED]

Claude (anthropic): 6,378 requests, 151.3M original, 2.89M saved. Codex (openai): 951 requests,
276.1M original, 60K saved. Codex sends about 290K tokens per request on average, so it is 65% of
all original tokens while the proxy reduces almost none of them.

**Evidence:** `ai-proxy gain --text` harness rows (claude Saved% 1.9%, codex 0.0%), and
`sqlite3` per-provider sums of `$.Totals.EstimatedOriginalTokens` and `$.Totals.EstimatedSavedTokens`.

### F9 — Why Codex requests are almost never reduced is not known [UNVERIFIED]

Only 228 of the openai gain rows went through the content pipeline (221 `tool_result`, 7 `none`);
7,970 went through passthrough. Whether Codex tool output is too small for CCR, has a shape the
region finder does not see, or is sent in a way the pipeline skips, was not checked.

### F10 — Every token figure is characters ÷ 2.89, not a provider count [READ]

The ratio is a constant. ADR 0005 calls it "measured", but this investigation did not re-measure
it, and no provider usage numbers were compared. A different real ratio scales all figures here by
the same factor, so the percentages hold but the absolute numbers do not.

**Evidence:** `src/AiProxy.Core/Gain/GainReportBuilder.cs:6` (`CharactersPerToken = 2.89m`);
`docs/adr/0005-tool-result-previews-with-in-request-retrieval.md:22`.

### F11 — 85% of model requests are not changed at all [MEASURED]

1,086 of 7,329 requests had a positive saving; 3 had a negative one (−7 tokens in total); the
rest were forwarded unchanged. The gain store gained four requests between F1 and this query.

**Evidence:** `sqlite3 -readonly ~/.ai-proxy/gain.db` counting `kind='request'` rows by the sign
of `$.Totals.EstimatedSavedTokens`.

### F12 — The billed balance is the per-request sum less one 64,602-token tool definition [INFERRED]

Summing saved minus follow-up over every request gives 312,341; the report shows 247,698. The gap,
64,643, is within about 40 tokens of the "Injected tool definition: 64,602 tokens" line. This
reasons from the two measured totals; the report builder code that applies it was not read.

### F13 — The real money effect of follow-ups is probably smaller than their token count [UNVERIFIED]

A follow-up repeats the previous request with a short tail added, so most of it could be a
prompt-cache read, which providers bill at a fraction of normal input. The opposite risk also
exists: changing a tool result can break a cached prefix. There is no provider billing data
(no paid API credential), so neither effect was measured.

## Still open

- Provider-billed numbers: run the same window with real `usage` fields (input, cache read, cache write) to replace the 2.89 estimate and see the cache effect on follow-ups (F10, F13).
- What changed on 11-13 (F7): match the daily balance to the release dates and the active projects.
- Codex (F9): sample openai content-pipeline captures and check why tool output is not previewed. Codex is 65% of all original tokens, so this is the biggest lever.
- CCR break-even: the recall loop rounds (F5) suggest some previews are retrieved in full. A per-preview check (saved vs follow-up per CCR request) would show which kinds of tool output repay the preview.
- The gap 2026-11-14 to 2026-11-17 has no rows: proxy off, or rows reset? Not checked.
- F12: read the report builder to confirm how the tool definition enters the balance.

### Status of these items on 2026-11-21

- 11-13 (F7), the 11-14 to 11-17 gap and F12 are closed in [2026-11-20-gain-open-items.md](2026-11-20-gain-open-items.md): the balance followed the follow-up rate, not a release; no rows were deleted; F12 is confirmed from `GainReportBuilder`.
- Codex (F9) is answered in [2026-11-20-token-return-vs-rtk.md](2026-11-20-token-return-vs-rtk.md) F3: Codex on chatgpt.com is record-only by ADR 0012, and part of the "codex" row was OpenRouter traffic (split by host in 0.31.0).
- CCR break-even is measured per floor in [2026-11-21-replay-baseline.md](2026-11-21-replay-baseline.md) and the ADR 0015 replay grid, with recall applied as a 0.29 to 0.90 bound.
- Still open: provider-billed numbers. Capture has recorded billed `usage` since 2026-11-20 19:32 UTC; the one-week read is due about 2026-11-27.

