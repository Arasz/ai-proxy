# Research: Three open items in the gain data (11-13 balance, the 11-14 to 11-17 gap, the tool definition)

**Date:** 2026-11-20
**Question:** Why did the daily billed balance turn positive on 2026-11-13, why are there no gain rows for 2026-11-14 to 2026-11-17, and how does the injected tool definition enter the billed balance?

Method: read-only queries on `~/.ai-proxy/gain.db` and `~/.ai-proxy/content.db` with `sqlite3 -readonly`. The column `at` holds .NET ticks. Dates below are local time (`date((at-621355968000000000)/10000000,'unixepoch','localtime')`). Background: `docs/work/2026-11-19-token-reduction-performance.md` (F7, F12, "Still open") and `docs/work/2026-11-20-token-return-vs-rtk.md`. The data is live, so totals grow while this record is read. The per-day table was read at about 20:37 local time on 2026-11-20.

## Findings

### F1 — The balance turned positive on 11-13 because follow-ups per CCR preview fell from about 0.8 to 0.29 [INFERRED]

Billed balance per day is original minus forwarded minus follow-up tokens (see F7). Values: 11-11 -134,917; 11-12 -184,182; 11-13 +232,291; 11-18 +289,808; 11-19 +128,882; 11-20 -56,767 (partial day). Saved tokens per day were not the cause. 11-12 and 11-13 differ by a factor of 2.8 in saved tokens but by a factor of 9 in follow-up tokens (1,220,242 against 134,355).

| Day | CCR preview requests | Follow-ups | Follow-ups per preview | Follow-up tokens | Saved tokens |
|---|---|---|---|---|---|
| 11-11 | 40 | 36 | 0.90 | 354,156 | 224,571 |
| 11-12 | 165 | 125 | 0.76 | 1,220,242 | 1,061,600 |
| 11-13 | 41 | 12 | 0.29 | 134,355 | 380,646 |
| 11-18 | 131 | 84 | 0.64 | 779,945 | 1,087,302 |
| 11-19 | 40 | 21 | 0.53 | 226,757 | 361,857 |
| 11-20 | 11 | 9 | 0.82 | 105,178 | 49,967 |

Follow-up size did not change: the average is 9,762 tokens on 11-12 and 11,196 on 11-13. Only the count per preview changed. Recall rounds also fell: rounds of 2 or more were 48 of 125 follow-ups on 11-12 and 3 of 12 on 11-13. The 11-12 follow-ups came mostly from the `ai-badger` project (82 of 125); on 11-13 that project had 1 follow-up in 163 requests. Five projects were active on 11-12 (one with no project, one a raccoon worktree) and four on 11-13.

The most likely cause is a change in what the agents did that day: fewer long recall chains. This is inferred from the table. It does not show why the chains were shorter. 11-20 is negative again with 0.82 follow-ups per preview, which fits the same reading: the sign follows the follow-up rate, not a one-time fix.

**Evidence:** none for the cause (inferred from F1's table and F2). The table comes from `sqlite3 -readonly ~/.ai-proxy/gain.db` grouped by local date, counting `kind='request' and rule like '%ccr.preview%'`, `kind='followup'`, and summing `$.EstimatedTokens` and `$.Totals.EstimatedSavedTokens`.

### F2 — No release on 11-12 or 11-13 changed CCR behaviour [READ]

Tags near the window: v0.28.2, v0.28.4, v0.28.5 on 11-11; v0.29.0 (.NET 11, Runtime Async) 11-12 09:39; v0.29.1 (harness NO_PROXY metadata address) 11-12 21:37; v0.30.0 on 11-18. The main-branch log for 11-12 to 11-13 holds the .NET 11 move, a scaffold refresh, the NO_PROXY fix and two docs commits. None names CCR, preview or recall. 11-13 is the first full day after v0.29.1, but the changes in 0.29.0 and 0.29.1 are a runtime move and a bypass fix, so a release is not the likely cause. The 11-11 releases (0.28.2, 0.28.4) relabel gain layers, so they change labels, not the totals used here.

**Evidence:** `git tag --sort=creatordate --format='%(creatordate:iso) %(refname:short)' | tail -15` and `git log --format='%ad %s' --date=iso origin/main | awk '$1>="2026-11-09" && $1<="2026-11-15"'`. Changelog files: `docs/changelog/0.28.2-gain-layer-labels.md`, `0.28.4-gain-layer-rulings.md`, `0.29.0-dotnet-11-runtime-async.md`, `0.29.1-harness-bypass-metadata-ip.md`.

### F3 — The gain log has no gap in its sequence numbers, so no row was removed from 11-14 to 11-17 [MEASURED]

`gain_event` holds 46,635 rows. The sequence runs from 49,704 without a hole: `count(*)` equals `max(sequence)-min(sequence)+1`. Rows for 11-13 end at sequence 80,553 and rows for 11-18 start at 80,554. A removed row would leave a hole, because the table uses AUTOINCREMENT. The `other` kind (non-LLM traffic, 38,126 rows in total) has no row in the window either: per-day rows exist only for 11-11, 11-12, 11-13, 11-18, 11-19 and 11-20.

**Evidence:** `sqlite3 -readonly ~/.ai-proxy/gain.db "select count(*), max(sequence)-min(sequence)+1 from gain_event"` printed `46635|46635`. Per-day min and max sequence by local date, same database.

### F4 — The content file also has no request rows for 11-14 to 11-17, and its 14-day retention does not explain it [MEASURED]

`content_request` has rows for 11-11 (86), 11-12 (474), 11-13 (201), 11-18 (462), 11-19 (123) and 11-20 (41), and none between. Retention in the content file is 14 days, so rows from 11-11 (9 days old) are still present and rows from 11-14 to 11-17 would be younger than those. Retention cannot remove the middle of a range and keep both ends.

**Evidence:** `sqlite3 -readonly ~/.ai-proxy/content.db "select substr(created_at,1,10),count(*) from content_request group by 1"`. Retention value at `src/AiProxy.Storage/SqliteOriginalStore.cs:16` (`TimeSpan.FromDays(14)`) and rules at `src/AiProxy.Storage/RetentionRules.cs:14-19`.

### F5 — Nothing in the code prunes `gain_event` by age; only an explicit reset deletes rows [READ]

The retention rules list the content and capture tables only. The sole `DELETE FROM gain_event` is `DeleteThroughSnapshot`, run by `ResetAsync`, which removes rows up to a sequence snapshot and a cutoff time and takes the query filters. This explains why the log starts at sequence 49,704 on 11-11 15:53: 49,703 earlier rows were deleted, most likely by a reset. A reset takes rows up to "now", so it cannot remove 11-14 to 11-17 and leave 11-18 onward intact with a continuous sequence.

**Evidence:** `src/AiProxy.Storage/RetentionRules.cs:14-27` (no gain table), `src/AiProxy.Storage/SqliteGainStore.cs:96-110` (`ResetAsync`), `src/AiProxy.Storage/SqliteGainStore.cs:252-254` (`DeleteThroughSnapshot`). `grep -rniE "retention|prune|DeleteOlder|vacuum|DELETE FROM" src`.

### F6 — The proxy recorded nothing from 11-14 to 11-17; whether it was stopped or idle is not known [INFERRED]

F3, F4 and F5 rule out removal in both files. What is left is that the proxy wrote no rows in the window. The proxy writes an `other` row for any passed-through request, and the machine normally produces thousands per day (2,900 to 13,900 on active days), so an idle proxy with traffic running through it is unlikely. That points to the proxy being stopped or the system proxy being off. This reasons from F3 to F5 and from the daily `other` counts.

**Evidence:** none direct (inferred). See F3 for the daily `other` counts.

### F7 — The lock files carry no history for the window [MEASURED]

`process.lock` and `startup.lock` are empty files dated 2026-10-27. `system-proxy.lock` was rewritten 2026-11-20 20:33 and holds the current restore snapshot only. `capture.db` holds requests for 11-20 only, so its own retention is shorter and it cannot show the window. No log of start and stop times is kept in `~/.ai-proxy`. No macOS system log was read.

**Evidence:** `ls -lT ~/.ai-proxy` and `sqlite3 -readonly ~/.ai-proxy/capture.db "select substr(captured_at,1,10),count(*) from capture_request group by 1"` (printed one row, `2026-11-20|153`).

### F8 — The injected tool definition enters the billed balance because forwarded tokens include it and saved tokens exclude it [READ]

`RequestTotals` stores `EstimatedForwardedTokens = Tokens(forwardedCharacters)`, which includes the tool definition. It stores `EstimatedSavedTokens = Tokens(original) - Tokens(forwardedCharacters - toolCharacters)`, which removes the tool characters before it subtracts. So saved is larger than original minus forwarded by exactly the tool tokens. The billed balance is `EstimatedOriginalTokens - EstimatedForwardedTokens - EstimatedFollowUpTokens`. It uses forwarded, so it pays for the tool definition. A sum of saved minus follow-up over requests does not. The gap is the tool size: balance equals (sum of saved) minus (sum of tool tokens) minus follow-up tokens. The net figure `EstimatedNetTokens` subtracts the tool tokens by name.

**Evidence:** `src/AiProxy.Core/Gain/GainReportBuilder.cs:28-34` (`RequestTotals`, line 32 holds both expressions), `src/AiProxy.Core/Gain/GainContracts.cs:51` (`EstimatedNetTokens`) and `:54` (`EstimatedBilledBalanceTokens`). Row sums in `GainReportBuilder.cs:97-104` add the fields without change.

### F9 — The live data confirms F8 to within rounding [MEASURED]

Over all 7,920 request rows: saved 3,165,943; original minus forwarded 3,095,748; tool 70,152. Saved minus tool is 3,095,791, which is 43 tokens from original minus forwarded (per-row rounding of `Tokens`). Follow-up tokens are 2,820,633. So the billed balance is 3,095,748 - 2,820,633 = 275,115, while saved minus follow-up is 345,310; the gap is 70,195, within 43 of the tool total. This is the same shape as the 11-19 figures in F12 of the earlier record.

**Evidence:** `sqlite3 -readonly ~/.ai-proxy/gain.db "select sum(json_extract(payload,'$.Totals.EstimatedSavedTokens')), sum(json_extract(payload,'$.Totals.EstimatedToolTokens')), sum(json_extract(payload,'$.Totals.EstimatedOriginalTokens')-json_extract(payload,'$.Totals.EstimatedForwardedTokens')) from gain_event where kind='request'"` printed `3165943|70152|3095748`; follow-up sum `2820633` from `kind='followup'` on `$.EstimatedTokens`. Run on 2026-11-20 about 20:37 local time, one run.

### F10 — F12 of the 11-19 record is correct and can be upgraded from INFERRED to READ [READ]

F12 said the report's billed balance equals the per-request sum less one tool definition. The code confirms the mechanism (F8) and the live data matches (F9). One correction: the gap is the sum of tool tokens over all requests that injected the definition, not "one definition". The 64,602 tokens in 11-19 was the total over the requests then in the log; it is 70,152 now. Wording "one 64,602-token tool definition" should read "the tool-definition tokens summed over requests".

**Evidence:** `src/AiProxy.Core/Gain/GainReportBuilder.cs:32`, `src/AiProxy.Core/Gain/GainContracts.cs:54`, and the F9 query. The old finding is at `docs/work/2026-11-19-token-reduction-performance.md:129-133`.

### F11 — Why agent behaviour changed on 11-13 [UNVERIFIED]

F1 shows fewer follow-ups per preview, but the rows do not record why. A check against the agent sessions of 11-12 (the `ai-badger` project, 82 follow-ups) would show whether one long session produced the cluster.

## Still open

- Q1 cause: group the 11-12 follow-ups by hour and by round to see whether one session made most of them. That would turn F1 from a pattern into a cause.
- Q2: whether the proxy was stopped or idle. A macOS unified log query (`log show`) for the process or the system proxy setting in the window would settle it; it was skipped as too slow. A start and stop line in the proxy log would remove the question for future gaps.
- Why the log starts at sequence 49,704: a reset is the only code path that deletes (F5), but no record of the reset command was found.
- The 11-19 record should be amended in a later change: F12 grade and wording, and the "Still open" items for F7, the gap and F12.
