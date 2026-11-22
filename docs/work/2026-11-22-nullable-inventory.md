# Nullable inventory, 2026-11-22

Rule: [ADR 0022](../adr/0022-absent-values-are-union-cases-not-null.md). Example:
`src/AiProxy.Core/Providers/ProviderHost.cs` (`ProviderHostMatch` union).

## How the list was made

`python3 scripts/nullable-inventory.py <out.md>` scans `src/**/*.cs` with line regexes. It is a
guide, not a parser: the `optional-param` row also counts locals such as `string? x = null;`.
Counts are from before wave 1. Uses (`?.`, `??`, `is null`) are not counted. They go away when the declaration changes.

| Category | AiProxy | AiProxy.Code | AiProxy.Core | AiProxy.Generators | AiProxy.Storage | Total |
|---|---|---|---|---|---|---|
| Nullable return | 45 | 5 | 39 | 0 | 5 | 94 |
| `= null` parameter or local | 23 | 1 | 39 | 0 | 4 | 67 |
| Nullable property | 44 | 0 | 4 | 1 | 0 | 49 |
| Nullable positional record parameter | 8 | 1 | 11 | 0 | 1 | 21 |
| Nullable field | 11 | 1 | 5 | 0 | 1 | 18 |

## Groups and the shape each one gets

| Group | Examples | Shape | Wave |
|---|---|---|---|
| Result or refusal | `CcrParseResult(Request?, Reason)` | union of the value and a refusal record | 1 (done) |
| Optional argument | `IDecompositor.Decompose(..., string? filePath = null)` | two overloads, the second non-nullable. Deferred: both production callers (`ContentCompressionPipeline.cs:243`, `ReplayEngine.cs:138`) pass `region.FilePath`, which is nullable, so an overload only moves the check. Do it after `ContentRegion` carries a file/no-file union. | 2 |
| Optional record field | `CheckResult.Fix`, `HandledContent.RetrievalId`, `ContentRegion.FilePath`, `ReplayBlock.PreviewId`, `ReplayRow.Host` | `init` property | 1 (done) |
| Validation message | `DoctorSettings.Error`, `GainSettings.Error`, `ReplaySettings.Error`, `StatusSettings.Error`, `RetrievalArgumentSchema.Violation`, `IToolArgumentSchema.Violation`, `GainEndpoints.Validate` | one shared `Valid` / `Invalid(string Reason)` union | 2 |
| Result with an error text | `CcrRetrieval(Text, Error?)`, `CompressionResult(Failure?, Summary?)`, `CcrPreviewer`, `SourceCodeCompressionHandler.View` | union of success and failure records | 2 |
| Body rewrite that may not apply | `CcrRequest.Read`, `CcrRequest.AppendTool`, `ChatRequest.Read`, `ChatRequest.WithTool`, `CcrPlan.WithTool`, `CcrPreviewBuilder.Build` | union with a `NotApplicable` case | 3 |
| Store lookups | `SqliteContentStore.GetAsync`, `ContentQueue.FindPendingAsync`, `SqliteOriginalStore.FindAsync`/`FindThreadAsync` | union of the row and a `Missing` case | 3 |
| Test seams as optional delegates | `TitaniumProxyService(..., upstreamClosed = null, rootCertificate = null)`, `GuardCommand(restoreIfOwned = null, isOwned = null)`, `CliApplication(PromptConsole = null)` | one public constructor without the seams, one internal constructor with them non-nullable | 3 |
| File read path | `ToolCallFiles.ReadFor`, `FileRead.PathFor`, `ToolOutputRegionFinder(FileRead? path)` | `FileRead` union with a `NoFileRead` case | 3 |

## Keep null here

- JSON readers: `JsonExtensions.TryParse`, `StringProperty`, `Int64Property`, `ReadString`.
- Parsing at the edge: `LineNumber.Of`, `RootPemFile.Parse`, `StaleProxyValues.Parse`,
  `ProjectCredential.Read`, `GainCommand.ParseDate`.
- Counts a provider can omit: `ResponseUsage` (`long?`), and `UsageReading`, whose truncated case
  holds usage and an error together, so it is not a two-case union.
- Settings bound from configuration or the command line (most nullable properties in `src/AiProxy`).
- Fields that hold "not yet set" state inside one class, for example `ResponseBuffer._strategy`.
