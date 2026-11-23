# 0022 - Absent values are union cases, not null

Status: Accepted 2026-11-22 (orchestrator in away mode; owner may reverse)

## Context

`src/` declares about 250 nullable members (inventory:
[2026-11-22 nullable inventory](../work/2026-11-22-nullable-inventory.md)). Most of them say one
of three things with `null`:

- a result that failed or found nothing (`string? Error(...)`, `CcrParseResult.Request`),
- an argument the caller did not have (`string? filePath = null`),
- an optional field of a record, put in the positional list with a `= null` default.

Every reader must then remember the null check, and the compiler cannot tell the "no value" case
from a bug. `ProviderHost.cs` already shows the other shape: `ProviderHostMatch` is a C# union of
`ProviderHost` and `NoneProviderHost`, and its readers `switch` over the cases.

## Decision

1. A return value that is "a result or a reason there is none" is a `union` of case types. The
   empty case is a named type (`NoneProviderHost`, `CcrRefusal`), never `null`.
2. A parameter that is optional because some callers do not have the value gets an overload
   without it. The overload that takes it takes a non-nullable type.
3. An optional record field leaves the positional list and becomes an `init` property. It can stay
   nullable: the constructor then lists only what every value must have.
4. Null stays where it is the honest boundary type: JSON and SQLite readers, configuration
   binding, BCL `Try*` shapes, and counts a provider can leave out (`ResponseUsage`).

The work goes in waves. Wave 1 applies rules 1 and 3 in `AiProxy.Core`. The inventory lists the
candidates for later waves.

### Wave 2 (owner rulings, 2026-11-23)

5. A check whose every caller throws at once is a guard, not a union. The four CLI
   `*Settings.Error` methods become `void Check(...)`, which throws `ArgumentException` with the
   same message.
6. A check whose caller goes on with the reason uses one shared union,
   `Validation(Valid, Invalid)` in `AiProxy.Core`. Its users are the tool argument schema and the
   gain API.
7. A type that carries constants and factories stays a static class, and its result gets its own
   union. `CcrRetrieval` keeps the tool name, definitions, error codes and factories; the answer
   is `CcrAnswer(CcrFound, CcrFailed)`. DTOs and the gain database keep a nullable error column.
8. A value that sits beside a success is not a union case. `CompressionResult.Failure` (the
   content store failed, the body was still produced) becomes the `init` property
   `StoreFailure`, and `Summary` becomes an `init` property too.
9. The file path of a tool result stays `string?`. It comes from tool-call JSON, it is set in one
   place, and `ContentPart` carries it on. A union on `ContentRegion` would make every reader of
   its text and offsets switch, and an overload of `Decompose` only moves the check to its two
   callers. This is a rule 4 boundary.

### Wave 3 (owner rulings, 2026-11-23)

10. A store lookup keeps a nullable return. The row is absent in SQLite, the port mirrors the
    reader, and each caller turns null into its own answer at once (404, `Forward`, not kept).
11. A request-body scan that returns null when it cannot apply keeps null. Its one caller
    matches the pattern at once (`CcrRequest`, `ChatRequest`, `CcrPlan.WithTool`).
12. An internal value whose only exit is a rule 9 nullable field keeps null along its path
    (`FileRead`).
13. A private helper whose only caller puts in a default returns that default itself
    (`SourceCodeCompressionHandler.View`).
14. A delegate that every production caller supplies is a required parameter. A default that
    production always takes is an explicit argument of the public constructor or an `init`
    default, never `= null` (`GuardCommand`, `TitaniumProxyService`). A null that selects other
    behaviour is not a default: `GainRuntime.Variable` stays nullable, as null makes
    `ProxyDataDirectory.Resolve` read the real environment and ignore case.
15. A domain decision with several reasons for "none" is a union, even when the empty case
    carries no reason (`CcrPreviewResult(CcrPreview, NoCcrPreview)`).

## Consequences

- Readers switch over named cases, so a missed case is a compile error, not a
  `NullReferenceException`.
- Union and case types add a few small declarations per result.
- Records whose optional fields become properties lose them from positional deconstruction.
