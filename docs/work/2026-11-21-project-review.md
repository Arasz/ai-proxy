# Research: Project-scope review of ai-proxy 0.34.0

**Date:** 2026-11-21
**Question:** What is wrong, at risk or healthy in ai-proxy at base commit `9d9c5b4` (0.34.0), across architecture, the compression domain, proxy security, data access, C# quality, test quality, the CLI and MCP contract, product design and operations?

Method: nine read-only expert lanes, each in its own worktree at `9d9c5b4`, each given the same measured ground truth and permission to refute the brief (lane briefs and raw reports are kept outside the repository with the session). Lanes ran probes on throwaway HOMEs and on `.backup` copies of the live `~/.ai-proxy` databases; nothing live was changed by a lane. Findings that drive expensive work were re-checked by the orchestrator at `path:line` before entering this record. An adversarial pass then tried to falsify the record; its verdicts are in the table at the end.

Ground truth at `9d9c5b4` (measured by the orchestrator, Apple M4): `dotnet build` 0 warnings, 0 errors; `dotnet test` 1,799 total, 1,791 passed, 0 failed, 8 skipped, 35.7 s; production C# 19,752 lines (host 9,340, Core 7,892, Storage 1,589, Code 804, Generators 127), tests 24,524 lines. Core has no package references.

Severity scale: BLOCKER, HIGH, MEDIUM, LOW, NIT. "Loaded, not fired" means the defect is real in code and has not been seen in production.

## Findings

### F1 — A mistyped command starts the proxy, because the start-up code ignores the `Usage` route [MEASURED]

**Severity:** HIGH. Found independently by the CLI-contract lane and the product-design lane.

`EntryPoint.Route` returns `EntryRoute.Usage` for a word that is not a command, and `EntryPointTests` pins that, but `Program.cs` acts only on `EntryRoute.Guard` and sends every other non-command to `ProxyInstance.RunAsync`. `ai-proxy stauts` and `ai-proxy bogus` reached the port bind of a server start in both lanes; they stopped only because the live proxy held the port. On a free machine a typo starts a machine-wide proxy and takes over the system proxy. `UsageExitCode = 2` is dead code, and doctor already uses 2 for "warnings only", so the fix must pick a code that does not collide.

**Evidence:** `src/AiProxy/Program.cs:10-35` (route computed, only `Guard` checked, line 35 `ProxyInstance.RunAsync`); `src/AiProxy/Cli/EntryPoint.cs:16,23`; lane runs of `dotnet AiProxy.dll bogus` and `stauts` with a throwaway HOME, both ending in "Requested endpoint ... is already in use".

### F2 — The gain table fits 80 columns only by wrapping numbers and headers onto a second line, and its width test cannot see that [MEASURED]

**Severity:** HIGH (user-visible regression of an owner ruling).

With the harness rows the live gain.db produces (three in a 2026-11-21 re-run), `ai-proxy gain` at 80 columns prints `Save│d`, `Reca│ll`, `235.4│M`: every row takes two lines. ADR 0014 was written to remove exactly this. The test added in 0.31.0 asserts only that each line is at most 80 characters and that the harness label stays whole; Spectre keeps every line within 80 by wrapping the other cells, so the test passes on the defect. The replay kind table has the same defect, with two headers both truncated to `Previe…`.

**Evidence:** design lane, `gain --endpoint` on a proxy started from a copy of the live gain.db, piped at 80 columns; `tests/AiProxy.Tests/Unit/Cli/GainViewLayoutTests.cs:86-99` (asserts `line.Length <= 80` and that the label cell of each row line matches; a wrapped continuation line has an empty label, so it passes); `docs/adr/0014-gain-tables-fit-the-screen-and-count-each-rule.md:3`.

### F3 — Capture would have stopped for about twelve days after 1 GB, because the age prune cannot free space in time [MEASURED]

**Severity:** HIGH (the 1 GB cap was raised live on 2026-11-21; the 8 GiB cap fires again about 2026-11-30, see F30).

Captured bodies grew 33.3 MB (31.7 MiB) per hour, but the cap counts live database pages, which grew 37.9 MB per hour (580 MB at 10:52 UTC from 19:33 the day before). `IsFullAsync` stops all writes at the cap and `EvictAsync` deletes only rows older than 14 days, so a full file stays full until its oldest row ages out: capture would have stopped about 2026-11-21 23:50 UTC and stayed off until about 2026-12-04, with one log line. A 14-day window at this rate needs about 12.7 GB. The owner approved a restart with `Proxy:CaptureMaxBytes=8589934592` on 2026-11-21 10:49 UTC; the code defect remains. A related defect: the request and the response rows check the cap separately, so a request row can be kept without its response (not yet observed: the two unpaired rows on the copy were in flight at backup time).

**Evidence:** `src/AiProxy.Storage/SqliteCaptureStore.cs:30` (1 GB), `:59` (14-day eviction), `:71-74`, `:100-103`, `:132-152`; data lane queries on a `.backup` copy of capture.db.

### F4 — `gain --reset` and `POST /gain/reset` delete the ledger with no audit row and no authentication [MEASURED]

**Severity:** MEDIUM. Found by three lanes (QA, data access, security).

The gain log starts at sequence 49,704; rows 1 to 49,703 were removed in one contiguous cut on or before 2026-11-11 14:53. Only the reset deletes gain rows, so a reset removed them, but nothing records who ran it, when, or with which filter. The loopback API accepts the reset from any local process (the Host header check and the JSON content type block browsers, not local tools or agents). The snapshot guard that protects later backdated inserts is claimed in a comment and not tested.

**Evidence:** `src/AiProxy.Storage/SqliteGainStore.cs:96-115,253`; `src/AiProxy/Hosting/Api/GainEndpoints.cs:19-26`; `sqlite3 -readonly ~/.ai-proxy/gain.db`: `min(sequence)=49704`, count equals max minus min plus one; security lane `curl -X POST .../gain/reset` on a probe instance returned 200.

### F5 — The root CA can sign for five whole domains, not only the provider hosts it decrypts [MEASURED]

**Severity:** MEDIUM.

The live root carries `Permitted: DNS:openai.com, DNS:anthropic.com, DNS:googleapis.com, DNS:openrouter.ai, DNS:chatgpt.com`, so a process that reads `root-ca.pfx` and its password (same 0700 directory) can mint certificates for `oauth2.googleapis.com`, `auth.openai.com` and the whole of chatgpt.com. ADR 0009 chose the registrable parent on purpose and rejected only a compile-time host list; exact hosts from the injected `IProviderHosts` were not considered. The password beside the pfx gives no protection at rest, which the ADR wording overstates.

**Evidence:** `openssl x509 -in ~/.ai-proxy/root-ca.pem -noout -text`; `src/AiProxy/Hosting/Integration/RootCertificateAuthority.cs:128-137` (`TakeLast(2)`); `src/AiProxy/Hosting/Integration/RootCertificateFiles.cs:51-61`; `docs/adr/0009-ai-proxy-owns-a-name-constrained-root.md:25,72-76`.

### F6 — A value-taking option given with no value binds the literal `__default_command` [MEASURED]

**Severity:** MEDIUM.

`stop --data-directory` with no value reported "not running (data directory <cwd>/__default_command)", exited 0 and created lock files in that folder; `gain --rule`, `--provider` and `replay --home` take the same literal. A script that runs `stop` believes the proxy stopped.

**Evidence:** CLI-contract lane runs with a throwaway HOME; Spectre.Console.Cli sentinel leaks into settings; no validator rejects it.

### F7 — One lone-surrogate escape in a tool result turns compression off for the whole request and for every later turn that carries it [MEASURED]

**Severity:** LOW (loaded, not fired: 0 of 5,904 captured bodies on 2026-11-21 hold a real lone-surrogate escape; an earlier count of 5 matched escaped backslashes).

`ToolOutputRegionFinder` calls `reader.GetString()`, which throws on `\ud83d` with no low surrogate; the inspector catches it and forwards the original body. A stateless client re-sends the history, so one bad tool result disables compression for the rest of the session.

**Evidence:** domain lane probe P1 through production DI (149,817 bytes forwarded unchanged; control body compressed to 4,518); `src/AiProxy.Core/Content/ToolOutputRegionFinder.cs:222`; `src/AiProxy/Hosting/Integration/ProviderRequestInspector.cs:279-285`.

### F8 — A retrieval follow-up has no upstream deadline, and keep-alive pings hide the stall from the client [READ]

**Severity:** MEDIUM.

The first send has a 10-minute header deadline; the follow-up send and its SSE read use only the client's token with an infinite `HttpClient.Timeout`, while the relay writes a keep-alive every interval. A stalled follow-up (sleep, network change) hangs the turn until the user cancels. No ADR rules this deliberate.

**Evidence:** `src/AiProxy/Ccr/CcrRelay.cs:89-100` (first-send `HeadersDeadline`) against `:433-441` (`SendWithPingsAsync`, client token only) and `:58` (`Timeout = InfiniteTimeSpan`).

### F9 — The `retrieve_original` MCP tool publishes a looser schema than the API enforces, and its errors do not name the allowed values [MEASURED]

**Severity:** MEDIUM.

`tools/list` shows `mode` as any string and `context` as any integer; the API enforces an enum, `minimum: 1`, `context` 0 to 10 and the 16-hex id pattern. A model calling the tool cannot see the constraints, and `bad_input` names only the property.

**Evidence:** CLI-contract lane, MCP stdio `tools/list` and three bad calls against the live proxy; `src/AiProxy.Core/Content/Ccr/CcrRetrieval.cs:15`.

### F10 — `ChatApi.Fields.Choices` is the string `"n"`, and `ChatRound` spells its response field names as literals (74 lowercase literals, 26 distinct) [READ]

**Severity:** LOW, with a trap. Found by three lanes (architecture, C#, domain).

The literals are a consistency issue. The constant is the hazard: a mechanical cleanup that replaces `"choices"` with `ChatApi.Fields.Choices` makes the relay read the `n` field and breaks every Chat Completions retrieval.

**Evidence:** `src/AiProxy.Core/Content/Ccr/ChatApi.cs:13`; `grep` counts in `src/AiProxy.Core/Content/Ccr/Loop/ChatRound.cs`.

### F11 — `ChatRequest.Prepare` rewrites the whole body when the client sends `stream_options` without `include_usage`, against its own byte-stability promise [READ]

**Severity:** MEDIUM.

The doc comment promises every other byte stays the same so the prompt cache matches; the `WithUsage` branch parses and re-serialises the whole body, which can change whitespace, escapes and number forms. Every edit also copies the whole body once more.

**Evidence:** `src/AiProxy.Core/Content/Ccr/ChatRequest.cs:60-62` (promise), `:95` and `:119-125` (re-serialise), `:90-93` (copies).

### F12 — Three EventIds are shared by two Log classes each [MEASURED]

**Severity:** LOW.

1201 and 1202 mean both a per-request API access line and a content-store drain line; 1601 means both "proxy_retrieve answered" and an MCP stdin give-up warning. A log filter on either mixes unrelated events, and nothing enforces uniqueness.

**Evidence:** `src/AiProxy/Hosting/Api/RequestLoggingMiddleware.cs:16,19`, `src/AiProxy/Hosting/Integration/ContentStoreWorker.cs:92,96`, `src/AiProxy/Ccr/CcrRelay.cs:497`, `src/AiProxy/Mcp/DrainOnEofStream.cs:121`.

### F13 — Replay's composition registers the live SQLite stores and the ADR 0006 boundary test follows one hop only [READ]

**Severity:** MEDIUM.

`AddCompression` wires the pure pipeline and the SQLite adapters together; replay is offline-safe only because it overrides two of the stores. If a pipeline type ever takes `IContentWriter` or `IContentQueue`, replay opens the live content.db and nothing fails, because `ClientBoundaryTests` checks only types a client uses directly (the real chain is three hops). `McpCommand`, `HarnessProcess` and `AiProxy.Doctor` are client code outside the checked roots.

**Evidence:** `src/AiProxy/ProxyServiceExtensions.cs:113-175`; `src/AiProxy/Replay/ReplayEngine.cs:54-64`; `tests/AiProxy.Tests/Unit/ClientBoundaryTests.cs:10,39-51`.

### F14 — `content.db` has no size bound, and its first eviction pass holds the write lock as long as the request path waits [MEASURED]

**Severity:** MEDIUM.

`content.db` is 749 MB, all of it younger than the 14-day window ADR 0008 rules, at about 78 MB per day (steady state about 1.1 GB, never shrinking because nothing vacuums). The eviction batch counts rows, not bytes: 1,000 rows of about 500 KB took 0.98 s on a copy, while request-path writes give up after 1 s, so the first big pass can make the original-keep write fail.

**Evidence:** data lane queries and the exact eviction batch from `src/AiProxy.Storage/ContentRetention.cs:11,57-67` run on a `.backup` copy; `src/AiProxy.Storage/ContentSchema.cs:18`.

### F15 — The gain report takes 1.17 s at 9.2K requests and grows linearly; `gain --watch` re-runs it every 2 s inside the proxy [MEASURED]

**Severity:** MEDIUM.

`Sql.Aggregate` scans `gain_event` six times with about 16 `json_extract` calls per row. At current traffic a report costs about 40 s within a year.

**Evidence:** `EXPLAIN QUERY PLAN` and `.timer` on a copy of gain.db; `src/AiProxy/Cli/GainWatch.cs:24`.

### F16 — None of the three databases has a migration ladder [READ]

**Severity:** MEDIUM.

gain.db and capture.db have `user_version` 0 and use `CREATE TABLE IF NOT EXISTS` plus ad hoc probes; content.db declares `SchemaVersion = 3` but its statements are all `IF NOT EXISTS`. Any non-additive schema change will not apply to an older file, silently.

**Evidence:** `src/AiProxy.Storage/SqliteGainStore.cs` (`Sql.Schema`, `NameLayers`), `src/AiProxy.Storage/SqliteCaptureStore.cs:217-228`, `src/AiProxy.Storage/ContentSchema.cs:21`; `PRAGMA user_version` on the copies.

### F17 — The only guard against the decompositor's quadratic JSON scan is a skipped test [MEASURED]

**Severity:** MEDIUM.

Raising `MaxFailedStructureAttempts` from 32 to `int.MaxValue` leaves the full suite green (1,799 total, 0 failed, 8 skipped, re-run by the adversarial pass), because the test that bounds it is a wall-clock test marked `Skip`. The bound is deterministic (a count), so a counting test can replace the timing one.

**Evidence:** QA lane mutation at `src/AiProxy.Core/Content/ContentDecompositor.cs:29`, filter `*ContentDecompositorTests`: 14 passed, 1 skipped; `tests/AiProxy.Tests/Unit/Content/ContentDecompositorTests.cs:156`.

### F18 — The preview identifier overlay fills with line numbers on numbered reads [MEASURED]

**Severity:** MEDIUM.

On a 1,500-line numbered read all 57 overlay tokens were numbers; ADR 0005 says the overlay lists identifiers the model can search for. The bare-number alternative of the overlay pattern matches tab-prefixed line numbers. After 0.33.0 only reads above 6,000 tokens get a preview, so every one of them is affected.

**Evidence:** domain lane probe P3; `src/AiProxy.Core/Content/MustKeepOverlay.cs:135`; `docs/adr/0005-tool-result-previews-with-in-request-retrieval.md:28`.

### F19 — Settings accept values the code cannot handle, and five bound keys are undocumented [READ]

**Severity:** MEDIUM.

`Proxy:Ccr:KeepAliveInterval` has no range: 0 makes a busy loop of pings to the client, -1 ms disables pings, and other negative values throw mid-stream. `Proxy:Compression:Minimum` accepts 0 or negative while `replay --minimum` rejects them. `Compression:CodeViews`, `Ccr:Enabled`, `Ccr:UnknownProviders`, `Ccr:KeepAliveInterval` and `RootCertificatePassword` are bound but absent from the settings reference, so the CCR off switch is not discoverable.

**Evidence:** `src/AiProxy/Configuration/CcrSettings.cs:16`, `CompressionSettings.cs`, `src/AiProxy/Ccr/CcrRelay.cs:436`; `docs/reference/environment-variables.md`.

### F20 — `gain` without a running proxy prints a bare socket error, and `replay` on a missing folder exits 0 with zeros [MEASURED]

**Severity:** LOW.

`gain` shows `Connection refused (127.0.0.1:1)` where `status` says "ai-proxy is not running" and how to start it. `replay --home /nonexistent` prints an empty report, blames missing capture coverage, and exits 0.

**Evidence:** design and CLI-contract lane runs with a throwaway HOME.

### F21 — The gain screen shows a positive Net and a negative billed balance with no on-screen reason [MEASURED]

**Severity:** LOW (wording; the two measures are documented on purpose).

The table's Net is Saved minus Recall; the footer's billed balance also subtracts follow-ups. On the live copy Net read +2.7M and the balance -1.38M two lines apart (values drift with traffic; -1.55M on the 11:52 copy).

**Evidence:** design lane run; `docs/how-to/reading-gain-and-retrieving-originals.md:36-37,65-75` (rules both measures).

### F22 — Two flaky tests lack evidence of their cause; a third times out on a 10 s child-start deadline [INFERRED]

**Severity:** LOW.

`RequestCaptureTests.With_compression_on_...` reads the capture once with no poll while `CaptureAsync` swallows a store error after a 1 s lock wait; under load the row can be missing. `ContentStoreTests.A_writer_that_loses_the_lock_...` fails only on an error message other than "locked" and never records which. `ProxyChild.ExitAsync` waits 10 s for a cold `dotnet` start on Windows. Neither flake reproduced in 22 runs, so the causes are reasoned from the code.

**Evidence:** `tests/AiProxy.Tests/Integration/RequestCaptureTests.cs:111,706,780`; `src/AiProxy/Hosting/Integration/ProviderRequestInspector.cs:371-385`; `tests/AiProxy.Tests/Unit/Storage/ContentStoreTests.cs:205-242`; `tests/AiProxy.Tests/Integration/ProxyChild.cs:86-90`.

### F23 — Smaller defects [READ]

**Severity:** LOW and NIT.

- Plain-HTTP pass-through forwards headers named in `Connection`, and `TE` (Titanium relay; RFC 9110 7.6.1). Measured by the security lane with an echo origin.
- `TitaniumProxyService` (553 lines) owns system proxy, root trust, dispatch and lifecycle; doctor and harness depend on it only for certificate file names.
- `Hosting/Integration` is a 19-file technical bucket over four domains.
- Content decoding lives in three places (gzip/deflate/br in Core, zstd in the host, a zstd sniff in replay).
- `GainHarness` spells hosts and provider names that `ProviderHostCatalog` and the `Dialect` enum already hold; a new catalog host gets no harness row.
- A tool output containing a preview header anywhere is never previewed (three repo files do).
- `lines` retrieval of a numbered read numbers each line twice (ruled by ADR 0005:66).
- Five CLI commands repeat the same `?? throw` runtime lookup; 29 hand-rolled throws in all.
- Windows: no ACL on a configured data directory holding the key files (unmeasured).
- A doc remark cites Titanium 7.0.26 behaviour while 8.0.2 is pinned.
- Three child-process tests run without an isolated HOME.
- The `--data-directory` (status, stop, doctor) and `--home` (replay) flags name one concept twice.
- Three seams (`ICompositor`, `IContentRouter`, `IProxyClient`) have one implementation and no test double.

**Evidence:** lane reports, each with `path:line`.

### F24 — On current traffic the pipeline removes 0.7% of request bytes [MEASURED]

**Severity:** observation.

The 0.34.0 pipeline over the 400 most recent captured Claude bodies took 53.08 MB to 52.70 MB; 82 of 400 bodies changed. This is bytes of whole bodies, not the token estimates of the replay baseline, so the two are not comparable one to one, but it says the floor change of 0.33.0 has not yet been seen live (the live proxy is 0.30.0) and that most tool output in this traffic is already small.

**Evidence:** domain lane probe P6 with production DI over captured bodies from 2026-11-21 09:21 to 09:33 UTC.

### F25 — The proxy has no log file, so capture-stop and eviction warnings exist only on the terminal that started it [READ]

**Severity:** HIGH (the live instance has run with output discarded since 2026-11-21 10:49 UTC, so warning 1502 goes nowhere today).

Only the MCP child configures logging; the proxy uses the default console logger and is not detached. When its terminal closes, or when it runs with output discarded, `Capture stopped ... above the ceiling` (EventId 1502) and eviction failures reach nobody; `status` and `doctor` do not report capture state. This is how F3 would have gone unnoticed.

**Evidence:** `src/AiProxy/Hosting/Proxy/ProxyInstance.cs:15`; `src/AiProxy/Mcp/McpServerProcess.cs:48-52`; `src/AiProxy.Storage/SqliteCaptureStore.cs:249`; grep for `capture` in `StatusView.cs`, `StatusCommand.cs`, `Doctor/*.cs` finds nothing.

### F26 — Nothing checks that VERSION was bumped, and `publish` builds main HEAD rather than the release tag [READ]

**Severity:** MEDIUM.

The required `build-bdd` check only asks that a changelog file exists for the current VERSION, which is always true after the previous release, so a feature PR without a bump passes. `publish.yml` packs main HEAD on dispatch, so a merge between the tag and the dispatch ships newer code under the tagged version, and a no-bump dispatch is a green no-op (`--skip-duplicate`). GitHub's latest release is v0.34.0 while NuGet ends at 0.30.0.

**Evidence:** `.github/workflows/build.yml:343-347`; `.github/workflows/publish.yml:10-12`; `gh release list`; `https://api.nuget.org/v3-flatcontainer/ai-proxy/index.json`.

### F27 — The Windows and macOS lanes are not merge gates [READ]

**Severity:** MEDIUM (low while merges are `--admin` by standing order).

The ruleset requires only the three ubuntu build jobs. `lifecycle (windows-2025)` and `installed-tool` caught real breaks on 11-04, 11-05 and 11-11 but cannot block a merge, and `lifecycle` cancels in progress on main pushes, so a quick second merge can drop the Windows result.

**Evidence:** `gh api repos/Arasz/ai-proxy/rules/branches/main`; `.github/workflows/lifecycle.yml:28-30`; `.github/workflows/build.yml:192-197`.

### F28 — The `labeled` trigger cancels the first CI run of every PR [MEASURED]

**Severity:** LOW.

The labeler adds a label seconds after a PR opens; `build.yml` triggers on `labeled` with `cancel-in-progress`, so the first run is cancelled. No job reads labels. This produced the cancelled `installed-tool` "fail" marks seen on PRs 135 to 137; it never blocked a merge, because that job is not required.

**Evidence:** `.github/workflows/build.yml:22`; runs 44858630660, 44854480420, 44849256745, 44859107202.

### F29 — NuGet high-severity audit warnings are suppressed with no other alert [MEASURED]

**Severity:** LOW.

`NoWarn` holds NU1901 and NU1903 while warnings are errors, and there is no dependabot configuration; `dotnet list package --vulnerable --include-transitive` reports nothing today, so nothing is hidden now.

**Evidence:** `Directory.Build.props:8`; the command above run by the ops lane on 2026-11-21.

### F30 — The 8 GiB live cap postpones F3 to about 2026-11-30 instead of preventing it [INFERRED]

**Severity:** HIGH for the code defect; the planned one-week capture (to about 2026-11-27) fits.

At 37.9 MB of pages per hour a 14-day window needs about 12.7 GB, more than 8 GiB, so the raised cap is reached about 2026-11-30 and capture then stops until rows age out, with its warning discarded (F25). Inferred from the 11-20/21 rate.

**Evidence:** capture.db copy at 2026-11-21 10:52 UTC: `page_count` 141,600 x 4,096, `freelist_count` 0, first row 2026-11-20 19:33; `src/AiProxy.Storage/SqliteCaptureStore.cs:72-92`.

### F31 — A start command with a dangling `--key` also starts the proxy [READ]

**Severity:** HIGH (same root cause as F1).

`EntryPoint` returns `Usage` for `--key` with no value, and `Program.cs` ignores it, so `ai-proxy --Proxy:Port` starts the proxy with default settings. The F1 fix and its tests must cover both branches.

**Evidence:** `src/AiProxy/Cli/EntryPoint.cs:58-61`; `src/AiProxy/Program.cs:10-35`.

## Healthy

These held under probing and should survive any cleanup.

- Layering per ADR 0001, 0002 and 0004: Core has no package references and no I/O; Storage references only Core, Dapper and SQLite; parsers sit behind `ICodeLexer`. No dead types and no orphan DI registrations among 335 types.
- The listener and the API bind to 127.0.0.1 only (measured with `lsof`); the API rejects a foreign Host header, a non-JSON reset and a CORS preflight.
- The root CA is RSA-3072 with `pathLen 0`, a critical name constraint and no IP space; key files are 0600 in a 0700 directory.
- No credential reaches storage: scans of all three databases for key prefixes found none; `Proxy-Authorization` and the project header are stripped before forwarding.
- No upstream certificate callback accepts all certificates; the relay does not follow redirects.
- Body limits hold: 8 MiB inspection, 16 MiB zstd output with a window limit, 4 MiB response capture, a 5-call retrieval cap.
- Compression is deterministic and byte-stable across turns on real data (554 distinct tool results, 0 with two compressed forms), keeps error lines in build logs, and fails open.
- Logging: 56 source-generated log methods, 0 direct logger calls, no `async void`, no fire-and-forget, no static mutable state.
- Strong mutation coverage on preview ids, the billed balance, retention, the capture cap, reset filtering, replay read-only and dialect detection.
- The CLI command list is derived from one registration list (a brief lead that said otherwise was disproved).
- CI: all actions pinned to a SHA, least-privilege permissions per job, one version source (`Directory.Build.props` reads VERSION), an idempotent release tag, OIDC publishing behind a manual `production` gate, and a `changes` job that keeps required checks reporting on doc-only diffs.
- Machine output goes to stdout and valid JSON where `--format json` is offered (stderr routing was checked for the MCP server, not for every command); `--format json` outputs are valid; unknown `PROXY__*` keys stop start-up.

## Disconfirmed leads

- "The CLI command lists drift": false; `CliApplication.Registrations` is the single source.
- "`IRetrievalRound` is wider than its callers need": not shown; every member has a caller and two implementations use it.
- "Nothing prunes capture.db": false; an age prune exists, but it cannot keep the cap (F3).
- "Proxy and CLI contend for gain.db locks": false; the CLI goes through the proxy's API.
- "Net gain counts the tool definition twice": false (`GainReportBuilder.cs:30-33`).
- "`proxy_retrieve` leaks to the client on `max_tokens`": false; release filters it by name.
- "A cancelled `installed-tool` blocks merges": false; it is not a required check.
- "Credentials leak to non-provider origins": withdrawn after re-probing through the proxy.

## Adversarial verification

An independent reviewer, given the sources but not the orchestrator's reasoning, tried to falsify every finding, re-ran the cheap measurements on throwaway instances, and checked each cited line. Every core conclusion held; the errors were in supporting numbers and citations, two of which had been graded MEASURED on a mis-count.

| Claim | Verdict | What changed |
|---|---|---|
| F1 typo starts the proxy | reproduced | a throwaway `stauts` run listened on both ports; F31 adds the dangling `--key` branch |
| F2 gain table wraps at 80 columns | reproduced, corrected | three live harness rows, not six; test lines 86-99 |
| F3 capture cap | corrected | MB vs MiB mixed; the cap counts pages (37.9 MB/h), so the stop was about 11-21 23:50 UTC; the "2 orphan rows" were in flight, not evidence |
| F4 unaudited reset | reproduced | |
| F5 root CA permits five domains | reproduced | |
| F6 `__default_command` | reproduced | |
| F7 lone surrogate | mechanism reproduced, prevalence refuted, softened to LOW | 0 of 5,904 bodies, not 5 of 1,604 |
| F8 follow-up deadline | reproduced, citation corrected | |
| F9 MCP schema | not checked | needs the live MCP server |
| F10 `Choices = "n"` | reproduced, count corrected | 74 literals, 26 distinct |
| F11 `WithUsage` | reproduced, softened | one extra body copy per edit, not three |
| F12 EventIds | reproduced | |
| F13 replay composition | partly reproduced | the three-hop claim was not re-traced |
| F14 content.db | partly reproduced | the 0.98 s timing was not re-run |
| F15 gain report time | not checked | |
| F16 migration ladder | reproduced | |
| F17 skipped guard | reproduced, stronger | the full suite stays green under the mutation |
| F18 overlay numbers | path corrected | |
| F19 settings | reproduced, corrected | -1 ms disables pings rather than throwing |
| F20, F21 | reproduced | F21 values drift with traffic |
| F22, F24 | not checked | F22 correctly INFERRED |
| F25 no log file | reproduced, raised to HIGH | the live instance discards its output |
| F26, F28, F29 | reproduced | |
| F27 | partly | ruleset not re-read |
| Healthy: layering, key file modes, logging, baseline | reproduced | |
| Healthy "errors go to stderr" | softened | not demonstrated for every command |

## Still open

- The cause of the 2026-11-11 gain reset (CLI or HTTP) cannot be recovered (F4).
- Neither flaky test reproduced in 22 runs; their causes are inferred (F22).
- Windows ACLs and Titanium 8.0.2's default upstream validation were not measured.
- Live thread-mode continuation and `--restart` end to end were not exercised.
- `status` and `doctor` views were not read end to end for a capture row (F25).
