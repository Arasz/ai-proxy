# C# reducer build plan

Date: 2026-10-24

The owner approved [the design](2026-10-24-code-reducer-design.md) and selected the high task loop. After checking the retrieval prerequisite, the owner chose to build the C# core now and keep activation blocked on CCR. This PR builds that inactive core. It does not satisfy the full design's runtime acceptance or release the replacement handler.

## Research checked against the checkout

- `src/AiProxy.Core/Content/ContentPart.cs` and `ContentRegion.cs` carry no source path. `ContentDecompositor.cs` classifies fenced/search output as source, but not ordinary full-file reads. Runtime path correlation belongs to activation.
- `src/AiProxy/Hosting/ContentEndpoints.cs` returns a whole stored request, not an exact original file through a model tool. The parent plan's `ccr-design` and retrieval implementation remain prerequisites.
- `ContentCompressionPipeline.cs` forwards compressed content after storage failure and combines parts whose chunk numbering restarts per region. `SqliteContentStore.cs` only scans the first 64 queued jobs for pending reads. Retrieval integration must settle identity and immediate readability before advertising handles.
- `scripts/code_reducer_spike/fs_server.py` currently uses regex compaction and local file retrieval. Offline experiments are useful but cannot demonstrate runtime retrieval.
- `scripts/reducer_scoring/rules_runner/Program.cs` constructs a Bash call without a file path. The future scoring adapter must provide real call metadata; string-presence scoring alone cannot prove a reader understands a rename map.

Three independent planning lanes examined architecture, tests, and retrieval. Their common recommendation is a pure reducer with an explicit file path and a caller-supplied rendered hint, leaving the live handler, registration, content metadata and store unchanged.

## This build

1. A common lexer contract and a conservative Roslyn syntax adapter represent comments, strings, identifiers, keywords, whitespace and line endings. Unsupported or unterminated constructs cause a whole-file bailout.
2. A pure view builder selects C# only from `.cs`, applies independently controlled comments/imports/renaming, preserves indentation and original number prefixes, and includes the map and supplied hint in the savings comparison. Missing hint returns the original. Unknown paths use conservative lossless handling. No line-joining transform is introduced, so range labels are deferred until one exists.
3. A developer adapter calls this actual core for offline tests. It is not a retrieval service. Tests use a clearly synthetic hint. Document the boundary and verify that the live handler remains unchanged.

No production tool name or handle is invented here. The initial options are comments on, imports on, renaming off. Contextual keyword spellings and native integer types are conservatively excluded from renaming, including escaped spellings, so declaration and reference handling cannot diverge. Options remain local to the core until activation can wire them into `CompressionOptions`. Roslyn provides syntax tokens and trivia without a workspace, compilation or semantic model. Parser SDK dependencies belong in the language adapter, outside the pure core. See [ADR 0004](../adr/0004-language-parsers-behind-the-code-lexer-interface.md).

## Test design

Expected output is hand-derived. Each behavior starts with a failing test and records the nonzero test count; QA executes representative mutations after implementation.

| Behavior | Failure caught / mutation |
|---|---|
| String and character contents survive, including comment-like bytes | Recognize comments inside strings |
| Ambiguous or unterminated syntax bails out exactly | Accept EOF in a literal/comment |
| Blank/trailing whitespace in multiline literals survives | Trim every line |
| Indentation and all-or-nothing number prefixes survive | Trim leading whitespace or detect numbering from first line only |
| Removed inline comments preserve token separation | Delete comment without replacement whitespace |
| Only complete using directives are removed | Drop using statements and declarations |
| Each option controls its own transformation | Ignore/invert option |
| Repeated identifiers rename deterministically with no collisions | Global text replace or omit reserved-name check |
| Contextual keyword spellings, escaped equivalents and native integer types stay consistent across declarations and references | Rename a method call while retaining its declaration |
| Multiline interpolation format text retains spaces and blank lines | Classify format text as ordinary tokens |
| Structured XML documentation spans cover the original bytes | Use trivia Span with FullString |
| Core stays free of parser SDK references | Move Roslyn back into Core |
| Singletons, keywords, strings and retained comments are not renamed | Rename all text occurrences |
| Map and supplied hint count toward final size; equal/larger returns exact original | Compare code size alone |
| Missing/unknown path never enables C# lossy steps | Sniff language from text |
| Missing or empty hint returns exact original | Bypass public-entry hint guard |
| Equal-size output or map overhead removes saving | Compare code-only size or use less-than-or-equal |
| Global/static/alias/multiline usings are distinct from using declarations/statements | Drop using-prefixed lines |
| Unsupported preprocessor syntax on numbered CRLF input bails out exactly | Process directives or normalize before bailout |
| Adapter reaches actual reducer with both rename settings | Substitute spike regex or discard options |

## Activation and release remain deferred

The future activation task must complete CCR design and implementation, correlate trusted whole-file tool calls by call ID, assign unambiguous file references, and guarantee the exact original is readable before emitting a compacted view. Storage failure must preserve the original request for retrieval-dependent code. Only then replace the registered blank-line handler and expose global options.

The four design checks remain the release gate: lexer tests; 36 hint-on edit runs across Haiku/Sonnet and renaming off/on through real proxy retrieval; token comparison on both C# payloads including map/hint; and token-based, map-aware QA scoring charging each retrieval as the full file. This inactive build can prove only the lexer/core and offline adapter checks. It claims no model score or edit success. TS/JS, Python, CSS and HTML remain later language PRs in that order, using Tree-sitter as approved by the owner on 2026-10-24. Native loading, grammar versions and each language's safe transformations must pass their own acceptance checks.

## Offline adapter contract

The developer console reads one JSON object from stdin and writes one JSON object to stdout. Required `text` is a string; optional `filePath` and `retrievalHint` are strings or null. Optional booleans `removeComments`, `removeImports` and `renameIdentifiers` default to true, true and false. Unknown or duplicate properties, wrong types and malformed input fail with a nonzero exit code, a diagnostic on stderr and no output view.

Successful output carries `text`, `changed` and `supported`. The map is already rendered inside `text`; the adapter adds no hint or retrieval service of its own. `changed=false` means the exact original was returned. `supported` reports the lexer result, not runtime retrieval readiness. Missing hints or unknown paths produce the safe original result.

Contract tests run in the existing xUnit project under `CodeAdapter`; the console project is part of the solution so CI builds it. Actual console smoke runs exercise both existing C# scoring payloads with renaming off/on and a synthetic hint. These are offline support checks, not the deferred model acceptance.

### Run the offline adapter

Build with `dotnet build`, then run `dotnet run --no-build --project scripts/code_reducer_core/CodeReducerCore.csproj < request.json`. The input file contains one request object, for example:

```json
{"text":"// source text\nclass Example { }\n","filePath":"Example.cs","retrievalHint":"[fixture: retrieve before editing]","renameIdentifiers":false}
```

The hint above is synthetic. It promises no runtime retrieval. A small input may return unchanged because the hint costs more than the removed text. Missing context also returns the original safely. A successful command returns status 0; rejected JSON returns status 1 with a diagnostic on stderr and empty stdout.

Adapter contract tests cover actual-core option forwarding, malformed and invalid contracts, and safe missing-context results. Final combined validation and fixture measurements are recorded below after integration. These are offline support checks, not token scores or model edit evidence.

## Integrated validation

The final source at `a3048d4` passed the Release build and test gates. Independent QA ran 75 reducer/adapter cases with no failures or skips, then restored a clean worktree after six intended mutations. Parser error handling, identifier ValueText grouping, interpolation text protection, duplicate JSON rejection and empty error output all caught their mutations. The directive-only mutation initially survived; an active-directive regression caught it in both lexer and reducer tests after the gap was fixed. QA then repeated the actual CLI cases that had exposed contextual method/property renaming and interpolation-format whitespace loss; both preserved the source facts.

Independent code and architecture reviews found no remaining blockers. The host, live handler, compression pipeline and storage have no changes in this build. Roslyn is referenced only by the new language adapter, composed by the offline CLI and tests.

The Release console ran twice per mode on each C# fixture with the 41-character synthetic hint `[fixture retrieve_original before editing]`. All four results were supported, smaller and deterministic. Sizes below count the entire view including map and hint.

| Fixture | Original characters | Renaming off | Renaming on |
|---|---:|---:|---:|
| `code_file.txt` | 7,509 | 5,414 | 4,571 |
| `code_file_large.txt` | 12,812 | 11,288 | 9,142 |

These are character measurements from offline checks. They establish neither token savings nor agent edit success. Runtime retrieval and all remaining design acceptance checks belong to the activation task.
