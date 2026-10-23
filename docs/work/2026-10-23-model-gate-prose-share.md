# Research: the most a prose compression model could save on real Claude Code traffic

**Date:** 2026-10-23
**Question:** Is the share of request tokens that a prose or source-code compression model could
even touch large enough (about 2% saved or more) to keep the extractive-model track?

## Findings

### F1 — No stored corpus of request bodies exists on this machine, so the planned part-by-part measurement was not run [MEASURED]

The two sources named for the corpus hold no request text. `~/.ai-proxy` does not exist. The rtk
proxy keeps counts only: `rtk-proxy.db` has `usage_records`, `ccr_decisions` and `app_settings`
(the decisions table carries token counts and shapes, no body column), and `rtk-proxy-memory.db`
has an empty `messages` table (0 rows). The unit measured here is therefore the per-request token
accounting rtk wrote for each request (`ctx_total`, `ctx_tool_result`), not the output of
`ToolOutputRegionFinder` plus `ContentDecompositor` on bodies. The Claude Code session transcripts
under `~/.claude/projects` do hold tool-result text, but reading them was refused by the
permission classifier as private data, and the record does not work around that.

What that does to the bound: the share of Prose parts at or above 512 characters was not
computed. What was computed is a ceiling on it (F3), which is enough for the gate and
not a substitute for the real share.

**Evidence:** `sqlite3 -readonly` on `rtk-proxy.db` (`.schema`, `pragma table_info`), on
`rtk-proxy-memory.db` (`select count(*) from messages` returned 0) and `ls ~/.ai-proxy` (no such
directory). MacBook Air, Apple M4, macOS 27.0.1.

### F2 — The corpus is 14,441 requests on three days between 17 and 23 October 2026, 2.18 billion request tokens [MEASURED]

`usage_records` holds 14,755 rows from 2026-10-17 to 2026-10-23. Only three days carry a
non-zero tool-result breakdown (17, 18 and 23 October); the rows on the other days come from small
clients and have none. Those three days hold 14,441 requests, nearly all from the `claude-code`
client. Tokens are rtk's own count, not chars/4. Per request, tool results are at most 63% of the
request.

| day | requests | request tokens | tool-result tokens | tool-result share |
|---|---|---|---|---|
| 17 Oct | 2,732 | 604.6M | 35.8M | 5.9% |
| 18 Oct | 8,995 | 1,369.1M | 152.8M | 11.2% |
| 23 Oct | 2,714 | 211.1M | 40.9M | 19.4% |
| all three | 14,441 | 2,180.2M | 228.9M | 10.5% |

Each request counts the
whole resent conversation, so history is weighted by how many times it was sent, which is what the
provider bills and what the proxy would see.

**Evidence:** `select substr(ts,1,10), count(*), sum(ctx_total), sum(ctx_tool_result) from
usage_records where ctx_total>0 group by 1` on `rtk-proxy.db`, opened read-only. Range of `ts` in
the table: 2026-10-17T14:19 to 2026-10-23T10:17 UTC.

### F3 — The ceiling on the model's saving is 1.03% of request tokens, under the 2% line [INFERRED]

The compression stage only ever reads tool output (F4), so no prose or source-code part exists
outside the tool-result tokens. Prose share is therefore at most the tool-result share, 10.5%, and
the bound is share times (1 - ratio):

| part kind | best size ratio | ceiling share | ceiling on saving |
|---|---|---|---|
| Prose, if every tool-result token were prose | 0.902 | 10.5% | 10.5% x 0.098 = 1.03% |
| SourceCode, if every tool-result token were source | 0.949 | 10.5% | 10.5% x 0.051 = 0.54% |

Both rows describe the same tokens, so they do not add: the most any model could save is the
larger row, 1.03%. The real figure is lower, because tool results are not all prose: logs, JSON,
shell transcripts and directory listings go to rule-based reducers, and prose parts shorter than
512 characters are skipped. This reasons from the F2 token counts, the F4 scope and the ratios
0.902 and 0.949 in `docs/work/2026-10-22-on-device-compression-models.md`; it does not rest on a
measured prose share.

### F4 — The pipeline touches tool output regions and nothing else [READ]

`ContentCompressionPipeline.CompressAsync` finds regions with `ToolOutputRegionFinder`, decomposes
each region's text, and sends only parts at or above `CompressionOptions.MinimumFor(type)` to a
reducer; the Prose minimum is `DefaultMinimum = 512`, SourceCode is 500. User text, assistant text,
thinking and tool-call inputs are never decomposed.

**Evidence:** `src/AiProxy.Core/Content/ContentCompressionPipeline.cs:57-69,160-186`;
`src/AiProxy.Core/Content/CompressionOptions.cs:8,18-19`.

### F5 — The ceiling stays under 2% even if rtk had already shrunk the tool results, but one day crosses it [INFERRED]

It is not recorded whether `ctx_total` counts tokens before or after rtk's own filtering. rtk saved
162.7M tokens on these requests (`tokens_saved`). If the breakdown is post-filter, the pre-filter
tool-result share is (228.9 + 162.7) / (2,180.2 + 162.7) = 16.7%, and the ceiling becomes
16.7% x 0.098 = 1.64%, still under 2%. Per day on that reading: 17 Oct 0.79%, 18 Oct 1.80%, 23 Oct
2.82%. The last day is over the line on the ceiling alone (on the plain reading it is 1.90%), which
says the ceiling is loose, not that the saving reaches 2%: it assumes every tool-result token is
prose on the heaviest day.

**Evidence:** same queries as F2 plus `sum(tokens_saved)`; arithmetic in a one-line `python3`
calculation from those sums.

### F6 — The type mix of the tool results and the real prose share are unmeasured [UNVERIFIED]

How much of the 228.9M tool-result tokens is Prose, SourceCode, Log, Json or Markdown was not
measured, for the reason in F1. No figure for the type mix is claimed. The chars/4 token
approximation the plan allowed was not needed, since rtk's own counts were used.

## Still open

- The real Prose share and type mix. It needs the original request bodies or tool-result text run
  through `ToolOutputRegionFinder` and `ContentDecompositor`. Settled by either letting the
  Claude Code transcripts be read for that one purpose, or running ai-proxy in capture mode for a
  few days and measuring its content store.
- Whether rtk's `ctx_*` columns are pre-filter or post-filter, which moves the ceiling between
  1.03% and 1.64%. Settled by reading the rtk source or comparing one logged request to its counts.
- Whether the gate wants the ceiling or the measured share. At 1.03% to 1.64% the ceiling is below
  2%, so the extractive-model track can be dropped on this evidence; the exception is the
  heaviest-tool-result workloads such as the 23 Oct day, where it is not settled.
