# Research: planned features not yet built, and the gain table's width

**Date:** 2026-10-31
**Question:** Which planned features are missing from `src` at 0.19.1, and what shorter number form stops `ai-proxy gain` from splitting numbers in the middle?

Base: `origin/main` at `82a4482` (VERSION 0.19.1).

```chart:bars
title: gain summary table width in columns (Spectre 0.57.2, values from the live store)
A current, N0 and full headers: 128
B compact numbers, full headers: 128
C compact numbers, short headers: 89
E vertical metric and value: 34
```

## Findings

### F1 — The gain summary table needs 128 columns, so a narrower terminal splits numbers mid-digit [MEASURED]

The summary is one row with nine columns. `Number()` prints every count with `N0`, which gives up to eleven characters such as `261,175,524`. When the table doesn't fit, Spectre wraps the cell text at any character, so `261,175,524` turns into `261,17` over `5,524`. The headers wrap too (`Reques` over `ts`). At 130 columns or wider, the table renders on one line per row.

**Evidence:** I ran `script -q /dev/null zsh -c "stty cols $w; ai-proxy gain"` for w = 100, 130 and 200 on macOS, with installed ai-proxy 0.18.0 and the live store. At 100 columns the rows wrap; at 130 and 200 the table is 128 columns wide. When piped, Spectre ignores `COLUMNS` and renders at 80, which matches the user's paste. Code: `src/AiProxy/Cli/GainView.cs:16-19` (the nine columns) and `src/AiProxy/Cli/GainView.cs:145-148` (`N0`).

### F2 — Compact numbers (K, M, B with one decimal) remove the mid-number split even at 80 columns [MEASURED]

With full headers, the headers alone are about 100 columns wide, so compact numbers don't shrink the table. They do keep each number on one line, and only the headers wrap, at word boundaries. With shorter headers ("tokens" moves into the title), the table is 89 columns and renders on one line per row at 100 and above. At 80 the headers still wrap, but the numbers don't.

**Evidence:** A file-based app at `<scratchpad>/gainwidth/app.cs` rendered layouts A to E through `Spectre.Console.Testing.TestConsole().Width(80|100|130)` with the live values (4,593 requests and 261,175,524 original tokens). Results:
- B at 80: 7 lines, the number row is one line: `261.2M │ 250.4M │ 10.8M │ 523.8K │ 24.0K │ 10.2M`.
- C: 89 columns wide at 100 and 130.

### F3 — `TableColumn.NoWrap` makes it worse, not better [MEASURED]

Option D (compact numbers, short headers, `NoWrap = true`) still wraps cells at 80 columns, as `10.8` over `M`, because Spectre shrinks the columns anyway. `NoWrap` isn't the fix.

**Evidence:** The same app at width 80, layout D output: `│ 10.8 │ 523.8K │ 24.0 │ 10.2 │` followed by `│  M   │        │  K   │  M   │`.

### F4 — A vertical metric and value table fits every width and keeps exact numbers [MEASURED]

The summary has only one row, so it can be turned into two columns, Metric and Value, with values right-aligned. The table is 34 columns wide at any terminal width and keeps the full `N0` values. The cost is 13 lines instead of 5.

**Evidence:** The same app, layout E: `maxwidth=34` at 80, 100 and 130.

### F5 — Exact values remain available outside the text view [READ]

`--format json|csv` goes through `GainCsv` and the JSON path, not through `GainView`. Shortening the text view therefore loses no precision for scripts. One unit test pins the `N0` text form, so a compact change makes it go red on purpose.

**Evidence:**
- `src/AiProxy/Cli/GainSettings.cs:17-20` (`-f|--format`, "text, json, csv").
- `src/AiProxy/Cli/GainCommand.cs:89-93`.
- `tests/AiProxy.Tests/Unit/Cli/GainViewTests.cs:89-90` (asserts `1,234,567` and `9,876,543`).

### F6 — Recommendation: compact numbers and short headers in the text view, exact values in json and csv [INFERRED]

This reasons from F1 to F5. The user asked for a shorter form, and only option C keeps the wide one-row view and drops below the common 100-column width. Option E fits everywhere but changes the view's shape. `gain --watch` wraps the view in a `Panel`, which takes four more columns (`src/AiProxy/Cli/GainWatch.cs:19`), so C needs about 93 there. The same `Number()` feeds the daily, weekly, by-dimension and history tables, which have five or six columns. They gain from compact numbers too, but they weren't measured.

### F7 — Two owner-approved rulings from 2026-10-24 are unbuilt: a build-output kind (D5) and a per-region size minimum (D6) [READ]

D5 gives build output its own content kind with an MSBuild-specific reducer, falling back to the log reducer. `ContentType` has no build kind: it has Unknown, Prose, Log, Json, Xml, Toml, Markdown, SourceCode, DirectoryListing and ShellTranscript. D6 applies the size minimum per region, but the pipeline still checks it per part. Both rulings are APPROVE, so they're waiting on work, not on a decision.

**Evidence:**
- `docs/work/2026-10-24-retrieval-activation-gates-feedback.md:47-63` (D5 and D6: APPROVE).
- `src/AiProxy.Core/Content/ContentType.cs:7-34`.
- `src/AiProxy.Core/Content/ContentCompressionPipeline.cs:255` (`part.Text.Length < options.MinimumFor(part.Type)`).

### F8 — The CSS and HTML code views planned in ADR 0004 are absent [READ]

`src/AiProxy.Code` has lexers for C#, JavaScript, TypeScript and Python, plus the Tree-sitter scanner. ADR 0004 lists CSS and HTML as later language PRs.

**Evidence:** `ls src/AiProxy.Code` (CSharpCodeLexer, JavaScriptCodeLexer, PythonCodeLexer, TypeScriptCodeLexer, TreeSitterLanguage, TreeSitterScanner); `docs/adr/0004-language-parsers-behind-the-code-lexer-interface.md:16`.

### F9 — Two settings are bound but do nothing, and say so: `Proxy:Ccr:UnknownProviders` and `Proxy:Compression:Model` [READ]

`UnknownProviders` has no reader anywhere in `src` or `tests`; its doc comment says "bound but without effect until another provider has its own loop". `Model` is "reserved for the extractive model artifact, which this version does not include; accepted and ignored". Both are honest placeholders for features that aren't built.

**Evidence:** `src/AiProxy/Configuration/CcrSettings.cs:12-13` (grep for `UnknownProviders` returns only this line); `src/AiProxy/Configuration/CompressionSettings.cs:22-23`.

### F10 — The Linux NSS cleanup of the replaced root is absent [READ]

No code calls `certutil`. The 0.19.0 changelog lists this as a known gap, and the review plan lists it as Still open.

**Evidence:** `grep -rn certutil src --include='*.cs'` returns nothing; `docs/changelog/0.19.0-name-constrained-root-ca.md:27`.

### F11 — `capture_response` stores no response message id, so thread differencing and per-kind calibration can't use it [READ]

The calibration record asks for the message id. The capture table has the request id, status, byte and token counts, the usage error and the stages, but no message id. A message id exists only in `ccr_thread`, a different table.

**Evidence:** `docs/work/2026-10-24-claude-token-calibration.md:78-79`; `src/AiProxy.Storage/SqliteCaptureStore.cs:236-249`. These were read by the inventory lane and not re-read by me.

### F12 — Proactive provider caching (Anthropic `cache_control` injection, Gemini CachedContent) is researched but not built [READ]

No source file mentions `cache_control`. The research record leaves this to a billed experiment that hasn't run, because there are no paid credentials.

**Evidence:** `grep -rln cache_control src` returns nothing; `docs/work/2026-10-24-provider-cache-strategies.md:167-175`.

### F13 — Directory listings for the MCP server (Smithery bundle, hosted remote MCP, npm or PyPI wrappers) are not built [INFERRED]

This rests on the inventory lane's negative grep in `src` for mcpb, Smithery and server.json. Nobody checked the repo-level packaging files. The plan is `docs/work/2026-10-26-mcp-directory-listings.md:42-48`, and its hosted half needs owner rulings on auth, tenancy and retention.

### F14 — Two docs still list shipped work as pending [READ]

- `docs/work/2026-10-24-csharp-reducer-activation.md:3-5` says "Status: next ... blocked on CCR", and `docs/work/2026-10-24-csharp-reducer-build.md:52-56` says activation, TS/JS and Python "remain later PRs". All of these shipped: 0.10.0 (CCR), 0.11.0 (C# on by default), 0.12.0 (Python) and 0.14.0 (JavaScript and TypeScript).
- The 0.19.0 changelog's Known gaps still says rotation needs a manual delete. 0.19.1 fixed that, but changelogs are point-in-time records, so that one is correct as history.

**Evidence:**
- `docs/changelog/0.11.0-csharp-code-view-on-by-default.md`.
- `docs/changelog/0.19.1-root-rotation.md`.
- The lexer listing in F8.

### F15 — The other open items are measurements, not features [READ]

The review plan's Still open list holds the F16/D7 organic rates (due after about 2026-11-07), Titanium `ConnectionTimeOutSeconds` ("not planned until measured") and the per-kind chars-per-token ratio. None of these is a missing capability.

**Evidence:** `docs/work/2026-10-30-project-scope-review-plan.md`, section "Still open".

## Still open

- The width of the other gain tables (daily, weekly, by-dimension, history) wasn't measured with compact numbers. Run the same TestConsole app on their column sets.
- Compact-number rules aren't decided: the cut-over point (I used 10,000 and up becomes K), negatives (net tokens can be negative), and whether `Requests` stays exact.
- docs/features, docs/tutorials, docs/meta, the docs/work records from 2026-10-21 to 2026-10-23, and the 34 KB review findings file were not swept for planned features. A full sweep of those could add items.
- F11, F13 and the region-minimum half of F7 rest partly on the inventory lane's reading; only the lines cited were re-checked.
