# 0024 - The gain report reads each value once and is cached until the ledger changes

Status: Accepted 2026-11-23

## Context

A 10-minute trace of a live 0.35.4 proxy (103 proxied requests) measured:

| Measure | Value |
|---|---|
| CPU | 142 s (24% of one core) |
| Allocated | 391 MB; 11 gen0, 1 gen1, 3 gen2 collections; 24 ms pause |
| CPU in `GET /gain` | about 99% of busy samples, nearly all in `sqlite3_step` |
| Allocations from `GET /gain` | about 78% (strings, `GainTotals`, boxed `Int64`, LINQ iterators) |

`ai-proxy gain --watch` asks for the whole report every 2 seconds. Each report runs nine aggregate
passes over the ledger (65,000 rows, 34 MB). Each pass calls `json_extract` sixteen times per row on
a JSON text, and the result comes back as JSON text that .NET parses again. One report took 0.55 s.
The proxy traffic itself, with the pooled buffers of [ADR 0023](0023-pooled-buffers-for-zstd-decode-and-stream-capture.md),
was under 1% of the CPU. GC pause time was small. The cost was work, not collection.

The watcher process also allocated about 2.6 MB/s: Spectre.Console measures the text of every cell
each time the view gets a new target, also when nothing changed.

## Decision

1. **Read each value once.** The `metrics` step of the report statement reads the sixteen totals
   into plain columns, one time per row. Every pass sums columns. The statement returns the sixteen
   sums as columns, so `SqliteGainStore` builds `GainTotals` without JSON. Detail rows (history,
   failures, recalls, other traffic) keep their payload text.
2. **Cache the last report.** The store keeps the last report with its query and a ledger version
   (`MAX(sequence)` and `COUNT(*)` of `gain_event`). A query with the same filters and the same
   version gets the cached report with a new `GeneratedAt`. An insert raises the maximum sequence and
   a reset lowers the count, so either one makes the next query run the statement again. The
   version is read from the database, so a write from another process also counts.
3. **Sum and group with loops.** `GainReportBuilder.Sum` and `Attribute` add into local values and
   one dictionary, not through LINQ chains that make a new `GainTotals` for each row.
4. **The watcher redraws only on change.** `gain --watch` gives the live display a new target only
   when the report, except `GeneratedAt`, is different from the last one it showed.

## Consequences

- One report on the measured ledger takes about 0.17 s when it is not cached. A cached report
  costs one indexed read.
- The cache holds one report (about 75 KB as JSON) for each store. A different filter replaces it.
- A writer that changes rows in place, not through insert or delete, does not change the version.
  The store does not do this after the schema step, which runs before the first query.
- The live view's clock does not move when the report does not change.

## Alternatives

- **Keep running totals in a table that each write updates.** Rejected: every write on the request
  path would also update about nine aggregate rows, and resets would have to subtract.
- **Cache with a fixed time to live.** Rejected: a report could show stale numbers after a write,
  and the tests could not tell a hit from a miss without waiting.
- **Poll less often in the watcher.** Rejected as the only fix: other clients still pay the full
  cost, and the view gets slower.
