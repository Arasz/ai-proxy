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

The work goes in waves. Wave 1 applies one example of each rule in `AiProxy.Core`; the inventory
lists the candidates for later waves.

## Consequences

- Readers switch over named cases, so a missed case is a compile error, not a
  `NullReferenceException`.
- Union and case types add a few small declarations per result.
- Records whose optional fields become properties lose them from positional deconstruction.
