# Research: Replay baseline of preview savings at today's settings

**Date:** 2026-11-21
**Question:** What do today's compression settings save on the stored and captured request bodies, split into reducer and preview savings, and does lowering the preview floor change anything before the size-scaled budget ships?

Method: `ai-proxy replay` (0.32.0, commit `b63f421`) run on an Apple M4 laptop against the live `~/.ai-proxy` data, read-only, window from 2026-11-06. Token counts are estimates (characters ÷ 2.89 of region text), not provider-billed tokens, and they are not comparable 1:1 with `ai-proxy gain` totals (see `docs/how-to/replaying-stored-requests.md`). This record is the fixed "before" table that ADR 0015's tuning step compares against.

## Findings

### F1 — At today's settings previews save 6.10M estimated tokens and reducers 0.15M, so previews are 98% of the saving [MEASURED]

Rows read 3,994: 1,943 replayed, 697 with at least one preview, 1,705 with no tool region, 341 duplicates (same decoded bytes), 5 not JSON, 0 undecodable, 0 encoded, 0 misaligned. Coverage is `capture` (2,544 capture rows against 8,509 gain requests in the same window).

| Kind | Blocks | Previewed | Original | Reducers | Previews |
|---|---|---|---|---|---|
| Prose | 29,279 | 722 | 19,548,710 | 49,766 | 3,105,664 |
| SourceCode | 2,938 | 382 | 4,557,997 | 17,820 | 1,265,337 |
| Markdown | 6,986 | 379 | 7,764,152 | 12,830 | 1,221,963 |
| Json | 2,239 | 67 | 1,848,303 | 47,186 | 467,273 |
| Xml | 134 | 8 | 118,627 | 4,400 | 36,749 |
| BuildOutput | 820 | 0 | 223,002 | 17,940 | 0 |
| DirectoryListing | 378 | 2 | 120,442 | 0 | 3,918 |
| ShellTranscript | 5 | 0 | 974 | 248 | 0 |
| Toml | 8 | 0 | 776 | 0 | 0 |
| **Total** | | | | **150,190** | **6,100,904** |

337 preview ids were sent 1,560 times. One id alone was sent 133 times and accounts for 892,696 of the preview saving (15%), so the total leans on a few long-lived blocks.

**Evidence:** `dotnet src/AiProxy/bin/Debug/net11.0/AiProxy.dll replay --since 2026-11-06 --json` at `b63f421`, run 2026-11-21 about 01:30 local, 53 s wall time.

### F2 — Lowering the floor below about 1,500 tokens changes nothing today [MEASURED]

| `--minimum` | Rows with a preview | Preview ids | Preview saving |
|---|---|---|---|
| 3,000 (default) | 697 | 337 | 6,100,904 |
| 2,000 | 879 | 577 | 8,991,948 |
| 1,000 | 1,031 | 867 | 9,844,935 |
| 300 | 1,031 | 867 | 9,844,935 |

The 1,000 and 300 rows are identical. Lowering the floor from 3,000 to 1,000 raises the replayed preview saving by 61%, but nothing below that point moves.

**Evidence:** the same command with `--minimum 2000`, `--minimum 1000` and `--minimum 300`, same machine and data, run within ten minutes of F1.

### F3 — The fixed 1,500-token budget is what stops previews below the floor [READ]

`CcrPreviewBuilder.Initialize` sets the budget from `options.BudgetTokens` (1,500) whatever the block size, so a block under about 1,500 tokens fits whole inside its own preview and no shorter preview exists. That is why F2 flattens out. ADR 0015's size-scaled budget (`min(1500, floor(0.4 × reduced tokens))`) is required before a floor of 300 can have any effect, which matches the correctness review of the plan.

**Evidence:** `src/AiProxy.Core/Content/Ccr/CcrPreviewBuilder.cs:21-56` (the floor check on the original at :25 and the budget at :52).

### F4 — The replayed saving is not net of recalls [INFERRED]

Replay counts tokens removed from forwarded requests. It cannot see the follow-ups that a preview causes when the agent asks for the block back, which cost about 9.8K tokens each and ran at 0.29 to 0.90 per preview by day (`docs/work/2026-11-20-gain-open-items.md` F1). The 61% gain in F2 is therefore an upper bound for the floor change. The tuning step applies the recall bounds from ADR 0015 before it picks a value.

### F5 — The OpenRouter sample is too small to tune Chat Completions previews [MEASURED]

Hosts across the read rows: `api.anthropic.com` 2,537, `openrouter.ai` 7, and 1,450 rows with no host (content rows, which do not store one). Capture had no `chatgpt.com` rows in the window (0 encoded rows).

**Evidence:** the `hosts` and `rowsEncoded` fields of the F1 run.

## Still open

- Net value after recalls for each floor: needs the size-scaled budget (step `budget`) and the recall bounds of ADR 0015; replay alone gives the gross side.
- Codex bodies: capture holds no `chatgpt.com` rows since it was turned on, so the Codex replay (step `codex-replay`) waits for a Codex session with capture on.
- Concentration: one id is 15% of the preview saving. Whether a few long-lived blocks dominate in other weeks is not known.
