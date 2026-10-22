# .NET review — the content compression pipeline

## Findings
### F1 — Quadratic split in ContentDecompositor [BLOCKER]
- ContentDecompositor.cs: split/concat per part; O(n²) on large payloads
- Fix: use ArrayPool/Span slicing; avoid per-part string allocations

### F2 — Double JsonDocument+Utf8JsonReader pass [MAJOR]
- ToolOutputRegionFinder.cs: two passes over same payload
- Fix: single pass with Utf8JsonReader only

### F3 — Regex in MustKeepOverlay / log reducer [MAJOR]
- MustKeepOverlay.cs + LogCompressionHandler.cs: regex per part
- Fix: compiled/static regex or token scan

### F4 — Storage adapter not atomic (request + chunks) [BLOCKER]
- SqliteContentStore.cs: two statements, no txn wrapping both
- Fix: wrap request row + chunk rows in SqlTransaction

### F5 — ContentStoreWorker connection lifetime [MAJOR]
- ContentStoreWorker.cs: SqliteConnection held lifetime; PRAGMA per pass
- Fix: open/close per job; drop PRAGMA loop

### F6 — Trimming breaks Dapper/SQLite dependencies [BLOCKER]
- Directory.Build.props + AiProxy.csproj + .github/workflows/build.yml: no <TrimmerRootDescriptor>; Dapper and SQLite.Interop trimmed
- Fix: root descriptor for Dapper + SQLite; publish self-contained test required

## Performance notes
| Path | Cost | The case that shows it |
|---|---|---|
| ContentDecompositor split | O(n²) allocations | Large tool-output payload |
| ToolOutputRegionFinder | double pass | Every JSON payload |
| MustKeepOverlay / Log | regex per segment | Log-heavy payload |

## Still open
- CompressionResult seam usage by host (CompressionResult.cs + host wiring)
- Source-generated Log class verification (explicit EventIds)
- ConfigureAwait / ValueTask usage audit full