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

1. A common lexer contract and conservative C# scanner represent comments, strings, identifiers, keywords, whitespace and line endings. Unsupported or unterminated constructs cause a whole-file bailout.
2. A pure view builder selects C# only from `.cs`, applies independently controlled comments/imports/renaming, preserves indentation and original number prefixes, and includes the map and supplied hint in the savings comparison. Missing hint returns the original. Unknown paths use conservative lossless handling. No line-joining transform is introduced, so range labels are deferred until one exists.
3. A developer adapter calls this actual core for offline tests. It is not a retrieval service. Tests use a clearly synthetic hint. Document the boundary and verify that the live handler remains unchanged.

No production tool name or handle is invented here. The initial options are comments on, imports on, renaming off. Options remain local to the core until activation can wire them into `CompressionOptions`. No new package or external minifier is needed.

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
| Singletons, keywords, strings and retained comments are not renamed | Rename all text occurrences |
| Map and supplied hint count toward final size; equal/larger returns exact original | Compare code size alone |
| Missing/unknown path never enables C# lossy steps | Sniff language from text |
| Adapter reaches actual reducer with both rename settings | Substitute spike regex or discard options |

## Activation and release remain deferred

The future activation task must complete CCR design and implementation, correlate trusted whole-file tool calls by call ID, assign unambiguous file references, and guarantee the exact original is readable before emitting a compacted view. Storage failure must preserve the original request for retrieval-dependent code. Only then replace the registered blank-line handler and expose global options.

The four design checks remain the release gate: lexer tests; 36 hint-on edit runs across Haiku/Sonnet and renaming off/on through real proxy retrieval; token comparison on both C# payloads including map/hint; and token-based, map-aware QA scoring charging each retrieval as the full file. This inactive build can prove only the lexer/core and offline adapter checks. It claims no model score or edit success. TS/JS, Python, CSS and HTML remain later language PRs in that order.
