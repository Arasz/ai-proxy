# Implementation review — the content compression pipeline

## Findings
### F1 — Pipeline stores record even when store fails (failure handling claims not met) [MAJOR]
- `ContentCompressionPipeline.cs:98-105`: catches Exception (not OperationCanceled), assigns failure but still writes record and returns `CompressionResult` with `True`. Changelog claims original kept; failure path keeps compressed but failure ignored by caller.
- Evidence: catch block does not prevent return of `compressed`.

### F2 — Region finder's two passes disagree on count if string token spans cross newlines (design claim: agree) [MAJOR]
- `ToolOutputRegionFinder.cs:174-198`: second pass uses `Utf8JsonReader` over raw bytes; first pass uses `JsonDocument`. Escaped quotes inside strings handled differently can mis-align counters.
- Lines 187-195 count tokens; no cross-check that counts match.

### F3 — Rebuild byte splice does not handle overlapping replacements (correctness) [MAJOR]
- `ContentCompressionPipeline.cs:116-131`: `Rebuild` writes segments sequentially by `Start`. If regions overlap, latter replaces overwrite earlier without error; design claims non-overlapping only by region finder, not enforced.

### F4 — Log handler uses regex that may miss lines with tabs (minor) [MINOR]
- `LogCompressionHandler.cs`: line start check `\s*` handles tabs; OK. No blocker.

### F5 — ADR 0002 claim: worker polls on PRAGMA data_version; code uses Timer-based loop, no PRAGMA gate [MAJOR]
- `ContentStoreWorker.cs`: no `PRAGMA data_version` call visible; relies on periodic poll.
- Evidence: grep returns none.

## Claims checked
| Claim | Where made | Verdict | Evidence |
|---|---|---|---|
| Original kept in SQLite before forwarding | changelog 0.3.0 | PARTIAL | Store called after rebuild (line 100); failure swallowed |
| Layering core/adapter/host per ADR 0001/0002 | ADR 0001/0002 | PASS | Core no refs to Dapper; Storage references it |
| Two-pass region finder agrees | design doc | FAIL | No agreement assertion; second pass different parser |
| Byte-identical rest | changelog §What did not change | PARTIAL | Rebuild splice; overlap not enforced |
| Worker PRAGMA gate | ADR 0002 | FAIL | No PRAGMA in worker |

## Still open
- Whether `ContentStoreWorker` implements PRAGMA gate (not found).
- Whether subset invariant (reduced ⊆ original) has property test (audit F3 says missing).
- Whether adapter writes correct `ratio` independently verified (audit F4 says missing).