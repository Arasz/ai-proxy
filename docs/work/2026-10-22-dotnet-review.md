# .NET review — the content compression pipeline

## Findings
### F1 — Quadratic split in ContentDecompositor [BLOCKER]
- ContentDecompositor.cs: split/concat per part; O(n²) on large payloads
- Fix: use ArrayPool/Span slicing; avoid per-part string allocations

**What was done, and where the quadratic actually was.** The split and the template are
linear: the scan advances, the parts' substrings add up to the length of the text, and the
template is appended once. The quadratic was in the search for structure. A JSON value that
never balances is only known to be unbalanced when the end of the text is reached, so every
line that opens a value and closes none costs a scan to the end, and a payload of such lines
costs a scan per line.

Measured on this machine, a payload of lines that each open a value and close none:

| lines | bytes | before | after |
|---|---|---|---|
| 4000 | 71 KB | 164 ms | 20 ms |
| 8000 | 143 KB | 397 ms | 13 ms |
| 16000 | 293 KB | 1454 ms | 12 ms |
| 32000 | 597 KB | 5917 ms | 23 ms |

Each doubling of the payload quadrupled the time, which is the quadratic. The proxy reads
a body of up to eight megabytes, so the shape was minutes of one request's CPU.

The decomposer now gives up looking for a structure after a small number of failures, and
treats the rest of the text as text. A well-formed payload finds its structure on the first
try, so the failures are what tell the two apart. The test that pins it is a performance
gate: a megabyte of such lines has to be decomposed in under five seconds, and it took
thirteen before the fix.

### F2 — Double JsonDocument+Utf8JsonReader pass [MAJOR]
- ToolOutputRegionFinder.cs: two passes over same payload
- Fix: single pass with Utf8JsonReader only

**Not taken, on purpose.** The two passes are what make a region findable by its position
rather than by its content. One pass over the raw bytes cannot tell a tool's output from a
message the client wrote that happens to repeat it, and that is the defect the design gate
audit raised as F1. The second pass is the price of the precision.

### F3 — Regex in MustKeepOverlay / log reducer [MAJOR]
- MustKeepOverlay.cs + LogCompressionHandler.cs: regex per part
- Fix: compiled/static regex or token scan

**Already the case.** Every pattern in both files is a static field with
`RegexOptions.Compiled`, so the pattern is built once and not once per part.

### F4 — Storage adapter not atomic (request + chunks) [BLOCKER]
- SqliteContentStore.cs: two statements, no txn wrapping both
- Fix: wrap request row + chunk rows in SqlTransaction

**Done.** The request row and its chunks are written in one transaction, and a test proves
it: a record whose chunk cannot be written leaves no request row behind.

### F5 — ContentStoreWorker connection lifetime [MAJOR]
- ContentStoreWorker.cs: SqliteConnection held lifetime; PRAGMA per pass
- Fix: open/close per job; drop PRAGMA loop

**Not taken, on purpose.** The pragma is only meaningful compared across two reads on the
same connection, which is what the SQLite documentation says: "it is only meaningful to
compare the `PRAGMA data_version` values returned by the same database connection at two
different points in time". A connection per job would make the comparison meaningless and
the poll would read every tick. The gate is now pinned by a test that counts the reads.

### F6 — Trimming breaks Dapper/SQLite dependencies [BLOCKER]
- Directory.Build.props + AiProxy.csproj + .github/workflows/build.yml: no <TrimmerRootDescriptor>; Dapper and SQLite.Interop trimmed
- Fix: root descriptor for Dapper + SQLite; publish self-contained test required

**Not a defect as the project stands.** The project does not trim and does not publish AOT:
`PublishTrimmed` and `PublishAot` are set nowhere, so the reflection Dapper uses is not
removed. The build lane that packs every runtime identifier is the check that would catch
it, and it runs on every pull request. A root descriptor becomes necessary the day the
project turns trimming on, and not before.

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