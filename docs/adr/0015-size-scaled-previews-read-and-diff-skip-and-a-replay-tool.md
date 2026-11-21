# 0015 - Size-scaled previews, read and diff skip, and a replay tool

Status: Accepted (owner rulings 2026-11-20, plan review)

Amends: [ADR 0005](0005-tool-result-previews-with-in-request-retrieval.md) (size floor, preview
budget, which regions are previewed).

## Context

ADR 0005 previews a tool result region only when its reduced text is at least 3,000 estimated
tokens, with a fixed budget of 1,500 tokens (`src/AiProxy.Core/Content/Ccr/CcrOptions.cs:31-34`).
On Claude Code traffic from 11-11 to 11-20 that floor is why ai-proxy saves 1.9% of request size
where rtk's proxy saved 7.1%. rtk previewed 59% of requests and ai-proxy 6%, and rtk's smallest
previewed block was 300 tokens
([token return record](../work/2026-11-20-token-return-vs-rtk.md) F5 to F7). Tool output of 300
to 3,000 tokens is 8.6% of stored Claude body size (F8). A fixed 1,500-token budget is larger than
most blocks in that range, so lowering the floor alone saves nothing (F9).

Follow-ups decide the sign. Each recall re-sends the whole request, about 9.8K tokens (F10).
Numbered file reads are recalled on 72% of previews and diffs on 79%, against 24% for grep output.
Reads and diffs are 91 of 303 previewed blocks but cause 136 of 296 recall calls (F10, F11). Per
day, follow-ups per preview ranged from 0.29 to 0.90, and the billed balance changed sign with
that rate, not with saved tokens
([gain open items](../work/2026-11-20-gain-open-items.md) F1).

The floor is checked twice today: on the reduced text in `CcrPreviewer.cs:22` and on the original
in `CcrPreviewBuilder.cs:25`. The builder returns null only when it hides no line
(`CcrPreviewBuilder.cs:172`), so a preview that is longer than the reduced text it replaces can
still go out.

No tool measures a change to these numbers before release. F9 and F11 are inferred, not measured.
The owner switched capture on 2026-11-20, so full request bodies are now being kept.

## Decision

### Replay

1. A top-level command, `ai-proxy replay`, runs captured requests through the real compression
   pipeline, built from the same composition root as the proxy. It is not a `gain` subcommand,
   because `gain` is one flat command (`src/AiProxy/Cli/CliApplication.cs:90`).
2. Replay runs every request with `CcrMode.Stateless`. In that mode `CcrPreviewer.KeepAsync`
   returns before it touches the store (`CcrPreviewer.cs:44`), and replay passes a store that keeps
   nothing. Each request runs twice, both stateless: once with the preview floor out of reach, to
   measure what the reducers (code views included) remove, and once with the floor under test.
   What the second run removes on top is the preview saving; a region is previewed only when its
   text differs between the two runs. Both savings are reported, per content kind, in separate
   columns.
3. It opens `capture.db`, `content.db` and `gain.db` with `Mode=ReadOnly`. It reads
   `capture_request` bodies (zstd bodies decoded) and `content_request.original`. Replay never
   writes to any of them.
4. The shipped flags are `--minimum`, `--since`, `--source content|capture|both`, `--home`,
   `--include-encoded` and `--json`. `--budget-share` and the skip flag are added with the
   size-scaled preview and skip steps.
5. It prints its denominators before any result: rows read, undecodable, not JSON, no tool output
   region, previewed. It also prints the capture row count beside the gain request count for the
   same window, so a reader sees how much of the traffic the replay covers. `content.db` holds
   only the requests the pipeline changed, so a sample without capture rows cannot show the
   requests a lower floor would newly preview; replay labels such a sample `coverage:
   "content-only"` and warns. Bodies with a content encoding are left out by default because the
   proxy never compresses them (`--include-encoded` adds them).
6. Saved tokens are counted per preview id over its lifetime: the saving of one id is summed over
   every replayed request that sends it, using the preview saving only (what the reducers remove
   is reported apart, per kind). A stateless client re-sends each old tool result, so one
   preview saves on every later turn (token return record F6).
7. Replay cannot see recall. It applies the measured follow-up rates as a prior (point 15).

**Offline exception.** [ADR 0006](0006-one-tool-with-local-gain-and-retrieval-clients.md) keeps the
clients away from storage. Replay is offline and reads the databases directly, so it is the one
exception: only the namespace `AiProxy.Replay` may reach `AiProxy.Storage`, and only through the
read-only `ReplayReader` (which opens every file with `SqliteOpenMode.ReadOnly`) and the rows it
returns. `ClientBoundaryTests` fails when a CLI type reaches storage any other way.

### Size-scaled previews

8. The budget is `min(1500, floor(0.4 x reduced tokens))`, where reduced tokens are the estimated
   tokens of the region after its reducers.
9. The floor and the budget are both computed on the reduced text, and the floor is checked once.
   The reduced text is a pure function of the region's bytes, so the preview stays byte-stable
   across turns, as ADR 0005 requires for stateless re-previews (0005, "Cache placement", the
   stateless paragraph, and the pure-function rule under "Preview format").
10. The builder returns null when the preview is not shorter than the reduced text. The region
    then goes out as its reducers left it.
11. A preview is also not made when it saves less than a minimum per send. That minimum is sized
    from the recall rate times the follow-up cost (9.8K tokens), spread over the sends a preview id
    gets in replay. The tune step sets the value.

### Read and diff skip

12. A region is not previewed, but still reduced, when its text has one of two shapes:
    - a numbered read: each of the first k lines is in the `FileRead.NumberOf` form (leading
      spaces, ASCII digits, a tab, `src/AiProxy.Core/Content/FileRead.cs:39-59`) and the numbers
      are consecutive. Any start number counts, so a read with an offset is caught too.
    - a diff: within the first 50 or so lines, a line that starts with `diff --git `, or a line
      starting `--- ` followed by one starting `+++ ` and then one starting `@@ -`.
13. The check runs on the region text after the reducers (the code view included) and before the
    preview builder. It applies only below an upper size band that replay sets. Above that band a
    read or diff is previewed as before, because the saving outgrows a follow-up.
14. The skip goes by shape, not by tool name. `ToolCallFiles` names a path only for a read call
    with exactly one path argument (`src/AiProxy.Core/Content/ToolCallFiles.cs:118-157`), and most
    diffs come from `Bash`, so a tool-name rule would miss both.

### Tuning rule

15. The tune step runs replay over a grid: `MinimumTokens` in {300, 500, 1000} times the skip band.
    Net for a cell is saved tokens minus follow-ups per preview times 9.8K per previewed block.
16. It keeps only values whose net has the same sign at 0.29 and at 0.90 follow-ups per preview,
    the lowest and highest daily rates seen (gain open items F1). A value that wins at one rate and
    loses at the other is not chosen.
17. The tune is blocked when replay previews fewer than 300 blocks. The release then keeps the ADR
    0005 numbers.
18. The budget share stays at 0.4 until live recall data by size band exists. It is not in the
    grid.

### Release and the gate after it

19. The release changes preview bytes for every region between the old and new floors and for
    every region whose budget shrinks. Each live session therefore pays one prompt-cache break on
    its next turn after the upgrade, as a preview version change does in ADR 0005. Release notes
    say so.
20. After release the gain ledger is read by size band (300 to 1,000, 1,000 to 3,000, 3,000 and
    up) for follow-ups per preview. If a band's saved tokens minus follow-up tokens is negative
    over the first working week, `MinimumTokens` is raised above that band in a patch release.

## Numbers

The tune step fills this table from the replay grid and attaches the replay output to the PR.

| Setting | ADR 0005 value | Value | Source |
|---|---|---|---|
| `MinimumTokens` | 3,000 | set by the tune step | replay grid, point 15 |
| Budget cap | 1,500 | 1,500 | this ADR |
| Budget share | none | 0.4 | this ADR, point 18 |
| Minimum saving per send | none | set by the tune step | point 11 |
| Skip upper band | none | set by the tune step | point 13 |
| Numbered-read lines checked (k) | none | set by the tune step | point 12 |

## Consequences

Positive: CCR reaches the 300 to 3,000-token blocks that hold most of the gap to rtk. A small
block gets a small preview, so lowering the floor saves tokens instead of adding them. Reads and
diffs, the kinds the model reads back most, stop causing most follow-ups. Every number in this ADR
comes from a replay of real traffic, not from an inferred share.

Negative: every live session pays one cache break on release. More previews mean more chances of
a recall, and replay cannot measure recall, so the first live week is the real test (point 20). A
read whose numbers do not match the `NumberOf` form, such as one from another agent, is previewed
like any other text.

Neutral: the replay command is a new public CLI surface. ADR 0005's retrieval tool, store, loop and
id rules do not change.

## Alternatives considered

- Lower the floor to 300 with the fixed budget: rejected. Most blocks in that range are smaller
  than 1,500 tokens, so nothing would be saved (F9).
- Skip by tool name: rejected. Paths are known only for single-path reads, and diffs come from
  `Bash` (point 14).
- Tune the budget share in the grid too: deferred. Without recall by size band, the grid would fit
  the share to a prior, not to data.
- Ship without replay and tune live: rejected. Each live change costs a cache break per session,
  and F9 and F11 are inferred, not measured.

## Sources

- [What raises ai-proxy's token return](../work/2026-11-20-token-return-vs-rtk.md), F5 to F11, F13
- [Three open items in the gain data](../work/2026-11-20-gain-open-items.md), F1
- Code: `src/AiProxy.Core/Content/Ccr/CcrOptions.cs`, `CcrPreviewer.cs`, `CcrPreviewBuilder.cs`;
  `src/AiProxy.Core/Content/FileRead.cs`, `ToolCallFiles.cs`, `ContentCompressionPipeline.cs`;
  `src/AiProxy/Cli/CliApplication.cs`
