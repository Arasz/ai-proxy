# Research: why the shipped rules left five over-minimum payloads untouched

Date: 2026-10-24. Step: `untouched-payloads` (reducer follow-ups plan). Each payload from
`scripts/reducer_scoring/payloads/` was passed through `ContentDecompositor` and the shipped handlers,
with `CompressionOptions.Default`, in a scratch test that printed each part's kind, length and handler
path (deleted after the run). A part comes out unchanged in two ways: it is shorter than its kind's
minimum (`src/AiProxy.Core/Content/ContentCompressionPipeline.cs:199`), or no part of the region got
shorter, and then the region is left as it was (`ContentCompressionPipeline.cs:219` and `:223`).

## Findings

### F1 — `github_pr_list`: the JSON reducer made the document longer, a bug [MEASURED]

The payload is already compact JSON on one line. `json.compact` rewrote it 5,362 to 5,372 characters:
`Utf8JsonWriter`'s default encoder escapes `'` as `\u0027` (six characters for one), and the payload has two apostrophes. The
pipeline saw no part get shorter and kept the original. In a region with several parts, an inflated
JSON part would be composed in whenever another part shrank. **Fixed** in this change: the handler
writes with `JavaScriptEncoder.UnsafeRelaxedJsonEscaping`
(`src/AiProxy.Core/Content/Handlers/JsonCompressionHandler.cs`), since the text goes to a model,
not into HTML. That encoder still escapes characters outside the basic plane (an emoji becomes two
`\u` escapes), so the handler also returns the input when its compact form would be longer. A compact document still cannot shrink without losing values, so `github_pr_list`
stays untouched, now for the right reason.

**Evidence:** test `Compact_json_is_written_back_as_it_was_not_escaped_longer` in
`tests/AiProxy.Tests/Unit/Content/Handlers/JsonCompressionHandlerTests.cs`, red before the fix
(`fix: don\u0027t drop it`), green after.

### F2 — `dotnet_build_failure_verbose`: there is no build kind, so 60 KB goes to the prose reducer [MEASURED]

The decompositor reads the whole MSBuild log as one Prose part (`ContentDecompositor.cs:233`; no
rule recognises MSBuild output). `prose.dedupe` (`ProseCompressionHandler.cs:17`) drops repeated
lines only, and the log has 64 lines, distinct after trimming except blank lines, which dedupe keeps. Two lines, the compiler command
line and its response file, hold 55,759 of the 60,671 characters; nothing in the shipped rules
shortens a line. Not fixed here: it needs a build-output kind or a route to the log/shell
reducers, which is a design change for the owner. The shorter `dotnet_build_failure` is also read as
Prose and loses 236 of 901 characters to dedupe.

### F3 — `incident_report`: Markdown with no comments, correct by design [MEASURED]

One Markdown part of 1,415 characters. `markdown.strip-comments` removes HTML comments and blank-line
runs only (`MarkdownCompressionHandler.cs:35` and `:38`), and the file has neither. The Markdown
reducer is lossless on purpose; this is correct under the shipped rules.

### F4 — `prose_large`: split into 35 fragments, most below their minimum [MEASURED]

The decompositor splits the 11,107-character document into 35 alternating Markdown and Prose parts.
Minimums apply per part (`ContentCompressionPipeline.cs:199`), so 26 of the 35 parts (16 Prose under
512, 10 Markdown under 200) are skipped, though the document is about 22 times the Prose minimum. The
other 9 (8 Markdown, 1 Prose) reach the lossless Markdown and Prose reducers and have nothing to remove. Both causes are by design today;
whether a minimum should apply to the region rather than to each fragment is an owner question.

### F5 — `dir_listing`: read as Prose, not as a listing [MEASURED]

`ls -l` lines do not match the path-line rule (`ContentDecompositor.cs:223`), so the listing is read
as Prose. As a 574-character Prose part it is over the 512 minimum and reaches `prose.dedupe`, which
removes nothing (574 to 574). The `int.MaxValue` listing minimum never applies, because the part is
not typed as a listing. Unchanged in effect, but not for the intended reason.

## What the counted view shows

The harness already writes both views: per kind with untouched payloads left out (n/a) and per kind
with them counted as nothing removed (`scripts/reducer_scoring/harness.py:235`, section "Per kind,
untouched payloads counted as nothing removed" in `results/report.md`).

## Still open, for the owner

- A build-output kind, or routing MSBuild logs to the log reducer (F2).
- Region-level instead of per-fragment minimums (F4).
