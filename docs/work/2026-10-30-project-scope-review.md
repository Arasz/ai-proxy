# Project-scope review of ai-proxy at 0.17.2

Date: 2026-10-30. Base commit: `4f1363be94b9ce2a13689a27887a50feaf106349` (origin/main, VERSION 0.17.2).
Every `path:line` below is against that commit. Status: integrated record, before the adversarial pass.

## Executive summary

Seven read-only lanes reviewed the whole codebase in parallel: architecture, compression and CCR,
proxy and security, C# and SQLite, test-suite QA, consumer-surface contracts and CI, and product
design. They filed 67 findings. After deduplication this record holds 29.

One defect is live and damaging. `prose.dedupe` removes every repeated line anywhere in a Prose
part, so source code read through a shell loses braces, namespaces and repeated statements before
the model sees it (F1). It has fired on this machine's real traffic, and no test checks what the
reducer keeps.

The rest are loaded, not fired, or slow-growing. `content.db` keeps every full request body with no
retention on a default-on path (F3). An unknown first argument starts the proxy (F2). The
control-pipe identity theory works but is skipped, so nothing in CI guards "stop the right process"
(F5). Several ADRs and docs describe a system that has moved on (F12 to F14).

The core shape is sound. Core is BCL-only, references point the right way, credentials are never
sent to a different provider, the listener is hard-coded to loopback, every GitHub Action is
SHA-pinned, and no SQL is built from input.

Severity: 2 HIGH, 16 MEDIUM, 10 LOW, 1 NIT.
Grade mix: 29 findings: 18 MEASURED, 9 READ, 2 INFERRED, 0 UNVERIFIED.

## Scope & method

Phase 0 ground truth, measured in a pinned worktree at the base commit:

| Measure | Value |
|---|---|
| `dotnet build` (Debug) | 0 warnings, 0 errors, 5.6 s |
| `dotnet test` | 925 passed, 0 failed, 7 skipped, about 30 s |
| Skips | 6 `TimingDependent.Reason`; 1 `RestartTests.cs:328` (6-case theory, reports as one skip) |
| Production C# | 12,505 lines: Core 6,036, host 4,481, Storage 1,120, Code 804, Generators 64 |
| Test C# | 12,898 lines; test:prod about 1.03 |
| Core package references | none |
| Live install | ai-proxy 0.17.2; `~/.ai-proxy/` capture.db 986 MB, content.db 74 MB + WAL, gain.db 156 KB + WAL |

Hypothesis recorded before the lanes ran: a 1:1 test ratio and a 12.5k-line product did not
suggest bloat, and the lanes found no dead production type (architecture lane scanned 300+ types).
One dead tested method exists (F23). The suspicion of bloat was not supported.

Lanes and models: architecture, compression/CCR and proxy/security ran on Opus; C#/SQLite, QA,
surface/CI and design ran on Sonnet. Each was read-only in its own worktree, briefed with the
verified ground truth and explicit permission to refute the brief. Lanes read the live databases
with `?immutable=1` or `?mode=ro`; `mode=ro` fails on capture.db (WAL header, no shm file), and
`immutable=1` ignores the WAL, so content.db and gain.db row counts lag live state (lanes saw
`content_request` at 348, 426 and 529 rows on different reads).

The orchestrator re-verified at `path:line` every finding that drives work: F1, F2, F5, F7, F8,
F9, F10, F11, F13, F14 and parts of F12 and F21.

## Findings

### F1 — `prose.dedupe` deletes every repeated line anywhere in a Prose part, corrupting code and logs sent to the model [MEASURED]
**Severity:** HIGH. Loaded and fired.
**Evidence:** `src/AiProxy.Core/Content/Handlers/Reduction.cs:14-32` keeps one `HashSet` of trimmed lines for the whole part and drops any line already seen. Probe through the shipped handler set: two C# classes, 46 lines and 10 `{`, went out as 18 lines with one `{` and one `}`; class B's body was gone; nothing marked the removal. Live content.db: 1,569 chunks with path `prose.dedupe`, of which 434 originals contain both `{` and `}`; chunk `01a18c82…/1` is a 144-line C# `cat` sent as 90 lines. Tests reference `prose.dedupe` only as a routing path name (`TextCompressionHandlerTests.cs:155`); none asserts what the reducer keeps.
Unfenced code with no file path (Bash `cat`, `sed`, `git show`) classifies as Prose (`ContentDecompositor.cs` ~102-136). The model then edits from a wrong picture of the file, and build or test output loses repeated lines and their counts. The compression lane received brace-less C# from the live proxy during this review. Smallest fix: collapse only consecutive repeats into one line with a `[×N]` marker.
*Sources: compression F1; orchestrator re-verification and live count.*

### F2 — Any unrecognised first argument starts the proxy instead of failing [MEASURED]
**Severity:** MEDIUM. The design lane filed HIGH and the surface lane MEDIUM; settled at MEDIUM because the proxy runs in the foreground and Ctrl+C restores the system proxy, so the harm is a surprise start, not lasting damage.
**Evidence:** `src/AiProxy/Program.cs:24-27` falls through to `ProxyInstance.RunAsync` when `CliApplication.IsCommandInvocation` is false; `src/AiProxy/Cli/CliApplication.cs:20-23` accepts only `-h`, `-?`, `--help`, `gain`, `mcp`, `harness`. Running `ai-proxy --version` and `ai-proxy bogus` with the live proxy up reached the second-instance start path. Unrecognised arguments become configuration through `WebApplication.CreateBuilder`.
`ai-proxy --version`, `ai-proxy gian` or `ai-proxy -p 9090` sets the machine-wide proxy and may raise the CA trust prompt. There is no `--version`. Fix: reject a non-option first token that is not a command, and add `--version`.
*Sources: design F1 (ran it), surface F3 (traced it). Independent convergence.*

### F3 — content.db keeps every full request body and its chunks with no retention, on a default-on path [MEASURED]
**Severity:** HIGH. Fired: grows on every compressed request.
**Evidence:** `src/AiProxy.Storage/SqliteContentStore.cs` contains no DELETE; only `ccr_original`/`ccr_thread` are evicted (`SqliteOriginalStore.cs:16,74`, 14 days). `ContentSchema.cs` stores `original` and `compressed` per request and `original_text`/`compressed_text` per chunk. Live read: 529 `content_request` rows, 139 MB of originals from 2026-10-25 to 2026-10-30, about 28 MB per day. `GET /content/{id}` serves originals (`Hosting/Api/ContentEndpoints.cs:17-30`). content.db is mode 0644 inside a 0700 directory.
At that rate the file passes 10 GB in a year of daily use and keeps full prompts and file contents indefinitely. Fix: apply `OriginalEvictionWorker`'s cutoff to all four tables and create the file 0600 as `SqliteCaptureStore` does.
*Sources: csharp-sqlite F2, proxy-security F6.*

### F4 — capture.db has no retention, size cap or schema version and reached 986 MB in three days [MEASURED]
**Severity:** MEDIUM. Capture is opt-in (`ProxySettings.cs:28`), and the 0.6.0 changelog says "nothing prunes it", so this fires only for an owner who leaves it on, as happened here.
**Evidence:** `pragma page_count` 240,816 × 4,096 = 986 MB; 16,113 `capture_request` rows from 2026-10-23T18:06 to 2026-10-26T20:30, `sum(length(body))` 971 MB, max body 1.8 MB. `SqliteCaptureStore` never deletes; no index on `captured_at`. Schema has no header columns; 0 bodies match a full `sk-ant-…` key shape and 0 contain a private key.
Fix: age cutoff on a timer plus a byte ceiling that stops capture; add the `captured_at` index first.
*Sources: csharp-sqlite F1, proxy-security F7.*

### F5 — The control-pipe identity theory works but is skipped, so nothing in CI guards "stop the right process" [MEASURED]
**Severity:** MEDIUM. The production check exists; only its gate is off.
**Evidence:** `tests/AiProxy.Tests/Integration/RestartTests.cs:328` `Skip = "No proxy runtime leases public facing interface"`, added in #88. The body already calls public `ProxyLease.AcquireRuntimeLease` (`ProxyLease.cs:3`), so the reason is false. Unskipped: 6/6 pass. Removing `EnsureProxyOwnerIsCompatible` (`ProxyControl.cs:112`) fails 4; removing `EnsureProcessIdentityIntegrity` (`:116`) fails 1; pid is caught at `:150`. With the skip in place, mutants on `ProxyControl.cs:149` (product) and `:141` (start token) survive the full suite at 925/0. The CI RestartTests job passes because it requires only `--minimum-expected-tests 1`.
On Linux the start token is the only identity guard. Fix: remove the Skip.
*Sources: architecture F6 (lead), proxy-security F1 (unskipped and mutated), QA F1 (mutants survive). Three-way convergence.*

### F6 — Timing skips hide the only coverage of upstream-drop termination and marker waiting [MEASURED]
**Severity:** MEDIUM.
**Evidence:** Mutant removing `e.TerminateSession()` (`TitaniumProxyService.cs:344`) survives 925/0; unskipped `UpstreamDropTests.cs:26` kills it through its exception-type assertion (line 61), not the Stopwatch assertion (line 62). Mutant making `SystemProxyMarker.Acquire` never wait survives; unskipped `SystemProxyMarkerTests.cs:39` and `TitaniumProxyServiceTests.cs:217` kill it, and use 300 ms against a 10 s budget with no duration assertion. `Acquire` already takes a `TimeProvider`.
Fix: delete the Stopwatch assertion and unskip; move the marker tests to `FakeTimeProvider`. `ContentDecompositorTests.cs:156` and `RequestCaptureTests.cs:555` skips are justified (real wall-clock assertions).
*Sources: QA F2, F3, F7; proxy-security Healthy (UpstreamDrop passes 3/3 unskipped).*

### F7 — The CCR relay's first upstream send has no timeout and ignores cancellation [READ]
**Severity:** MEDIUM.
**Evidence:** `src/AiProxy/Ccr/CcrRelay.cs:52` `Timeout = Timeout.InfiniteTimeSpan`; `:81` `SendAsync(…, HttpCompletionOption.ResponseHeadersRead, CancellationToken.None)`. Follow-up rounds use `_cancellation`. CCR is on by default (`CcrSettings.cs:10`).
An upstream that accepts and never sends headers holds the client forever, and a client that disconnects first does not release the upstream request. Fix: link the client or session token and set a headers deadline.
*Sources: proxy-security F4; orchestrator verified.*

### F8 — The root CA key opens with an empty password, has no name constraints and uses Titanium's generic CN [MEASURED]
**Severity:** MEDIUM.
**Evidence:** `openssl pkcs12 -in ~/.ai-proxy/root-ca.pfx -noout -passin pass:` exits 0; `TitaniumProxyService.cs:261` `PfxPassword = _options.RootCertificatePassword ?? ""`. Certificate: `CN=Titanium Root Certificate Authority`, CA:TRUE, no nameConstraints, RSA-2048, valid to 2027-11-23. The keychain holds 11 Titanium roots, one current; `src/` has no untrust path.
The file is 0600 in a 0700 directory, but any same-user process can mint a trusted certificate for any host. The proxy decrypts only 15 hosts (`ProviderHostCatalog.cs:7-24`), so permitted-DNS name constraints and a distinct CN keep function and shrink the blast radius. Regenerating requires re-trust. Origin of the 10 stale roots is unknown.
*Sources: proxy-security F2, F3.*

### F9 — Rewritten regions are serialised with the default JSON encoder, escaping `<`, `&`, `'` and all non-ASCII [MEASURED]
**Severity:** MEDIUM.
**Evidence:** `src/AiProxy.Core/Content/ContentCompressionPipeline.cs:87` `JsonSerializer.Serialize(sent)` with no options. Probe: forwarded body carried `<`, `&`, `'` and escaped em dash and `é`. The relaxed encoder is already used at `JsonCompressionHandler.cs:17` and `MessagesApi.cs:15`. Live gain rows show forwarded size growing, for example +1,875 characters on `sourcecode.blank-lines,prose.dedupe`.
Each escape costs 5 bytes and several tokens and partly cancels the reductions. Fix: serialise with `MessagesApi.Json`.
*Sources: compression F3; orchestrator verified.*

### F10 — One poison row stalls the content queue, and pending-record lookup sees only the 64 oldest jobs [READ]
**Severity:** MEDIUM. Loaded, not fired: live `content_queue` holds 0 rows.
**Evidence:** `ContentQueue.cs:71` deserialises inside `ReadAsync` with no per-row guard; a throw fails the whole pass and lands in the pass-level catch at `src/AiProxy/Hosting/Integration/ContentStoreWorker.cs:28` (the per-job catch at `:69` wraps persist only). A row that deserialises to null is skipped at `:72` and never deleted. Reads are `ORDER BY job_id LIMIT 64` (`:59-62`). `SqliteContentStore.cs:64-67` finds a pending record by scanning `ReadAsync(64)`.
After a record-shape change between versions, every content write stops. ADR 0002's "a record not yet written is still served" holds only under 64 queued jobs. Fix: per-row catch with dead-letter, delete null rows, look up pending records by `request_id`.
*Sources: csharp-sqlite F3, F4; architecture F3. Orchestrator verified.*

### F11 — The schema ladder cannot alter an existing table, and capture.db and gain.db have no version [READ]
**Severity:** MEDIUM. Loaded, not fired: every change so far added tables only.
**Evidence:** `src/AiProxy.Storage/ContentSchema.cs:20,73-81` returns early at `user_version >= 2`, and every statement is `CREATE … IF NOT EXISTS`, so a new column on an existing table is skipped while the version records it as applied. `SqliteCaptureStore` and `SqliteGainStore` set no `user_version`; live values are content 2, capture 0, gain 0.
Fix: a switch on `user_version` with one `ALTER` per step, before the first column change.
*Sources: csharp-sqlite F5; orchestrator verified.*

### F12 — ADR 0005 still says CCR is off by default and gated per kind; the code ships it on for every kind [READ]
**Severity:** MEDIUM.
**Evidence:** ADR 0005 §Configuration (`Proxy:Ccr:Enabled` default false) and §Activation gate (retrieval-aware re-score, per-kind switch, first captured session showing a working continuation). Code: `CcrSettings.cs:10` `Enabled = true`; `CcrPreviewer.cs:22` previews any region of 3,000 tokens or more; `CompressionOptions` has no per-kind switch. `docs/changelog/0.10.0-ccr-runtime-on-by-default.md` discloses the change. The thread path is live (`ccr_thread` 530 rows).
Two live answers exist. Fix: a superseding decision record that names what it replaces and whether the gate is still owed.
*Sources: compression F4.*

### F13 — ADR 0006's boundary test cannot fail [MEASURED]
**Severity:** MEDIUM. No current violation.
**Evidence:** `docs/adr/0006-one-tool-with-local-gain-and-retrieval-clients.md:17` "A test fails if `AiProxy.Mcp` or `AiProxy.Cli` references `AiProxy.Storage` or SQLite." `tests/AiProxy.Tests/Unit/Mcp/McpDependencyTests.cs:10-17` and `Cli/CliDependencyTests.cs:12-17` assert only same-assembly, `IProxyClient`, and no `AiProxy.Client`. The host assembly already references `AiProxy.Storage` and `Microsoft.Data.Sqlite`, so an assembly-level check cannot see a violation.
Adding `new SqliteGainStore(...)` in `RetrievalTools` would leave both tests green. Fix: a namespace-level check, or amend the ADR.
*Sources: architecture F1; orchestrator verified.*

### F14 — Docs drift: README shows a code identifier for the port, and trust-checked evidence anchors point at moved files [MEASURED]
**Severity:** MEDIUM.
**Evidence:** `README.md:29,66,108,124` print `ProxyRuntimeOptions.DefaultPort` where `8080` belongs; introduced by f06a2ff. `evidence=` anchors `src/AiProxy/Hosting/ProxyInstance.cs:137` (×4, including ADR 0007:13 and `docs/reference/environment-variables.md:146,168`), `src/AiProxy/Hosting/ProcessIdentity.cs:8` and `src/AiProxy/Hosting/ProxyControl.cs:11` (ADR 0007:30) name files that moved to `Hosting/Proxy/`; `ProxyInstance.cs` is now about 40 lines. ADR 0001 describes a credential rule and middleware forwarder that do not exist (Titanium relays, ADR 0003) and says registrations "do not activate CCR"; ADRs 0001 and 0002 are still `Status: Draft`.
No gate resolves evidence anchors, and the docs gates run only in the local pre-push hook (F21). Fix: restore 8080, re-anchor, amend and accept ADR 0001/0002, add an anchor check.
*Sources: design F3, surface F1/F2, architecture F4/F5, proxy-security F9.*

### F15 — A misspelled `Proxy:*` option is silently ignored [READ]
**Severity:** MEDIUM.
**Evidence:** `src/AiProxy/Hosting/Proxy/ProxyInstance.cs` binds `GetSection("Proxy").Get<ProxySettings>()` with no `ErrorOnUnknownConfiguration`; `Validator.ValidateObject` covers only `[Range]` on `Port`. Every documented key does bind.
`PROXY__PROT=9090` or `PROXY__COMPRESSION__ENABLE=false` starts on defaults. Fix: bind with `ErrorOnUnknownConfiguration = true`.
*Sources: surface F4.*

### F16 — Live retrieval rate is about 36% of distinct previews, above ADR 0005's 25% gate [MEASURED]
**Severity:** MEDIUM. Small sample: five days, one user; the denominator is previews, not reader calls.
**Evidence:** 55 distinct `[proxy preview <id>` ids in `content_request.compressed`; 20 distinct recalled ids in `gain_event kind='recall'`. 28 recalls: 26 `lines`, 1 `pattern`, 1 `whole`; 72,265 estimated tokens retrieved; 2 `limit_reached`.
Signals that the 1,500-token preview may be too thin. Needs the reader-call denominator before it justifies a change.
*Sources: compression F5.*

### F17 — The Log and ShellTranscript reducers never run on live traffic [MEASURED]
**Severity:** MEDIUM.
**Evidence:** content.db chunk counts: Prose 1,050, Markdown 544, Xml 386, SourceCode 195, Json 88, DirectoryListing 2, Toml 1, Log 0, ShellTranscript 0. ShellTranscript needs a prompt marker on line 0, which Claude Code Bash results never carry. `sourcecode.blank-lines` ran 103 times and saved 15 characters.
The scoring record's per-kind gains (log 24.6%, build 26%, test 25%) do not describe production traffic. Most Bash output lands in Prose, where F1 applies.
*Sources: compression F2.*

### F18 — SQLite queue mechanics live in the host and in the queue port's signature [READ]
**Severity:** MEDIUM.
**Evidence:** `src/AiProxy/Hosting/Integration/ContentStoreWorker.cs:2,12,48` uses `Microsoft.Data.Sqlite` and holds a `SqliteConnection`; `IContentQueue.ReadDataVersionAsync(SqliteConnection, …)` in `ContentQueue.cs`. ADR 0002 assigns these mechanics to the adapter.
This is why the host references SQLite (and why F13's assembly check is blind). Fix: move the drain pass into Storage behind a connection-free method.
*Sources: architecture F2.*

### F19 — Gain reports gross saved tokens and does not net retrievals or the injected tool [MEASURED]
**Severity:** LOW. Honestly labelled (`net-billed-savings: false`, estimated at 2.89 chars/token, `GainReportBuilder.cs:6`).
**Evidence:** gain.db: request rows `EstimatedSavedTokens` 416,163; CCR preview rows 419,595, so non-CCR rows net −3,432. Recalls 72,265 tokens, reported separately. 11 rows with rule `unchanged` differ by exactly +642 bytes, the length of `CcrRetrieval.Definition` plus a comma, about 222 estimated tokens against ADR 0005's 147. Headline seen by the design lane: 80,416 saved of 50,840,280, about 0.16%, with no percentage shown.
Fix: show saved − retrieved and a `ccr.tool` rule beside the gross figure; correct the ADR figure.
*Sources: compression F6, F7; design F5.*

### F20 — A second `ai-proxy` start shows a raw file-lock exception [MEASURED]
**Severity:** LOW.
**Evidence:** Output: `ai-proxy: The process cannot access the file '/Users/arasz/.ai-proxy/process.lock' because it is being used by another process.` `ProxyInstanceLifetime.cs:28-31` translates the IOException only under `--restart`.
Fix: "ai-proxy is already running (pid N, port P). Use `ai-proxy --restart` to replace it."
*Sources: design F2.*

### F21 — CI runs the suite three times identically, skips tests for some code paths, and leaves docs and release gates local-only [READ]
**Severity:** LOW.
**Evidence:** `.github/workflows/build.yml:120,186,215` all unfiltered on the same runner; the `build-bdd` comment (`:188-192`) claims a different runner, contradicted by `global.json` `"runner":"Microsoft.Testing.Platform"`. `CODE_REGEX` omits `scripts/CodeReducerCore/*.cs` (referenced at `AiProxy.Tests.csproj:22`), `global.json` and `Directory.Build.targets`, so such PRs skip tests. Docs-index and pin gates live only in `.lefthook/pre-push/verify.sh`. `release.yml` checks only the VERSION shape, not that `docs/changelog/${VERSION}-*.md` exists. Cross-OS coverage is RestartTests only (`lifecycle.yml:58`). RELEASING.md omits that merging a VERSION bump tags and creates a GitHub release.
*Sources: surface F5-F8, QA F6. No partition hole: no Category filter exists.*

### F22 — The CCR relay follows redirects with the caller's `x-api-key` [INFERRED]
**Severity:** LOW.
**Evidence:** `CcrRelay.cs:45-50` leaves `AllowAutoRedirect` at its default `true`; forwarded headers include all non-hop-by-hop client headers (`:70-71`). Reasoning: .NET strips only `Authorization` on redirect. Titanium's path returns 3xx to the client, so the two paths differ. Fix: `AllowAutoRedirect = false`.
*Sources: proxy-security F5.*

### F23 — Rules are duplicated between tested helpers and the code that ships [READ]
**Severity:** LOW.
**Evidence:** `ProviderHostCatalog.ShouldInspectHttps`/`ShouldInspectHttpsAuthority` (`ProviderHostCatalog.cs:34-45`) are called only from tests; production's decrypt gate is a copy at `TitaniumProxyService.cs:348-355`. `CcrRelay.cs` ~337-405 re-parses `"id"`/`"mode"` instead of using `CcrRetrievalParser` (`Core/Content/Ccr/CcrRetrievalParser.cs:11,59`). `ProxyApplication.cs:19-22` repeats `HasApiConfiguration()`; `ProxySettings` is bound twice (`ProxyInstance.cs:16`, `ProxyServiceExtensions.cs:33`). The gain-header strip at `CcrRelay.cs:70` duplicates `ProviderRequestInspector.cs:43` (equivalent mutant).
A test that covers code that does not ship lets the shipped copy drift.
*Sources: proxy-security F8, architecture F7/F8, QA F5.*

### F24 — Project invariants are broken in a few dozen mechanical places [MEASURED]
**Severity:** LOW.
**Evidence:** 36 `ArgumentNullException.ThrowIfNull` on non-nullable parameters with `<Nullable>enable</Nullable>` (`Directory.Build.props:5`), for example `SqliteCaptureStore.cs:37`, `ContentRouter.cs:14-15`, `Reduction.cs:16`, `ContentEndpoints.cs`. Five classic constructors: `SqliteCaptureStore.cs:28`, `SqliteContentStore.cs:22`, `CompressionExecutor.cs:10`, `ContentRouter.cs:12`, `TitaniumSystemProxyRegistrar.cs:13`. Static classes with I/O: `ProxyLease`, `ProxyInstance`, `McpServerProcess`, `ProxyApplication`.
*Sources: csharp-sqlite F7/F8, compression F8, architecture F10, surface F10.*

### F25 — `Hosting/` is a technical umbrella and `Integration/` a technical bucket [MEASURED]
**Severity:** LOW.
**Evidence:** `Hosting/` holds 31 of the host's 56 files; `Integration/` mixes Titanium wiring, request inspection, capture, the root CA and the SQLite drain worker; `Proxy/` means instance lifecycle; `HarnessProcess.cs` and `McpCommand.cs` sit at the project root. `Guard/` and `Marker/` already name their concept.
*Sources: architecture F9.*

### F26 — The gain CLI and MCP command have rough empty, help and failure states [MEASURED]
**Severity:** LOW.
**Evidence:** `gain --project` with no data prints zeros and no hint about `X-Ai-Proxy-Project` or `harness`. `gain --help` lists seven "Unavailable" rtk-compat flags and leaves `--by-method`, `--by-rule`, `--by-layer`, `--provider`, `--method`, `--rule`, `--layer`, `--endpoint` undescribed (`GainSettings.cs:96-108`). Tables wrap GUIDs and cut rule names at 80 columns (non-tty). `--graph` drops some zero days. `ai-proxy mcp --endpoint http://127.0.0.1:1` starts without a reachability check and prints host info lines; `--endpoint notaurl` leaks `(Parameter 'endpoint')`. `docs/tutorials/` has no tutorial. Conflicting period flags are not rejected up front.
*Sources: design F4, F6-F10; surface F10.*

### F27 — Loopback endpoints trust every local OS account [INFERRED]
**Severity:** LOW. Matters only on a shared host.
**Evidence:** `lsof` shows `127.0.0.1:8080` and `127.0.0.1:17432`; `curl …:17432/health` returns 200 unauthenticated; routes `/gain`, POST `/gain/reset`, POST `/retrieval`, `/content/{id}`. AllowedHosts=loopback (`ProxyApplication.cs:27-30`) blocks DNS rebinding and CSRF.
*Sources: proxy-security F10.*

### F28 — `RequestCaptureTests.No_header_or_query_value_reaches_the_capture_file` flakes and is the accidental guard on capture-write lock waiting [MEASURED]
**Severity:** LOW.
**Evidence:** Red once in three runs on an unskipped tree, green in three baseline runs. Mutant setting the `SqliteCaptureStore.cs:56` write timeout to 30 s is killed only by this unrelated-named test. Cause not isolated.
*Sources: QA F4, F8.*

### F29 — Small storage inefficiencies [READ]
**Severity:** NIT.
**Evidence:** `ContentSchema.Open` does not dispose the connection if a pragma throws. `SqliteCaptureStore.cs` ~47 copies `Body.ToArray()` (up to 1.8 MB). Gain predicates `(@X IS NULL OR col = @X)` and no `(kind, at)` index; `gain_event` has no retention (423 rows live). `Cli/GainWatch.cs:13` blocks a pool thread, deliberately and bounded.
*Sources: csharp-sqlite F6, F9, F10, F11.*

## Healthy

Recorded so a simplification pass does not remove them.

- Core is BCL-only with no I/O; project references point inward; ADR 0004's layering test is meaningful because Core is its own assembly.
- Storage implements Core ports (`IContentStore`, `IOriginalStore`, `IGainStore`, `IRequestCaptureStore`).
- Host matching is an exact `OrdinalIgnoreCase` lookup (`ProviderHostCatalog.cs:30`); nothing rewrites the upstream; dialect only selects body handling; `X-Ai-Proxy-Project` is stripped before forwarding. No credential can reach a different provider.
- Listen address is hard-coded loopback (`ProxyRuntimeOptions.cs:17`); non-catalog hosts tunnel without decryption; data dir 0700, pfx 0600, capture.db 0600; relay `UseProxy=false`.
- Control pipe: per-user `$TMPDIR`, `CurrentUserOnly`, 16 KiB frame cap, 3 s deadline; ADR 0007's decision text matches the code.
- Crash guard ignores SIGINT/HUP/QUIT/TERM (`Program.cs:12-15`); restore paths are mutation-constrained (QA M4, M9).
- CCR: preview id `SHA256(original)[..16]`, prefix-stable; failed keep falls back and reports `ccr.original-not-kept`; handler failure sends the original; ledger read failure returns retryable 529; base64, `is_error` and `cache_control` untouched; retrieval loop is a declared state machine (`RetrievalLoop.cs:56`).
- Storage: bounded lock waits (1 s request path, 30 s worker); all SQL parameterised; every Dapper parameter matches a placeholder; queue commits before forwarding and deletes after persist; gain reset snapshots in a transaction.
- Logging: 34 `[LoggerMessage]` sites, no direct `logger.LogX`, no `async void`.
- CI: every `uses:` SHA-pinned; least-privilege permissions; OIDC trusted publishing; idempotent release; six RIDs consistent; `check-docs-index.py` and the pin gate both fail on their defect (watched red).
- CLI: consistent `ai-proxy: <message>` on stderr with exit 1; `harness` errors name the next step; local API returns problem details.

## Disconfirmed hypotheses and withdrawn findings

- Bloat: no dead production type in 300+ scanned; test:prod 1.03.
- The skipped RestartTests theory was suspected to hide a missing check. It hides only a gate; the check exists and the theory passes.
- Credential sent to a different provider; suffix host matching; another OS user driving the control pipe; a closed terminal leaving the system proxy set; ProxyProbe exposing 0.0.0.0: all refuted with evidence.
- No busy_timeout: bounded per command. SQL injection: none. Dapper parameter drop: none.
- CCR preview over budget; re-previewing a marked region; thresholds differing from ADR 0005: all refuted by probe.
- DNS rebinding of `/gain/reset`: blocked by AllowedHosts.
- `ai-proxy --restart --help` restarting the live proxy: it prints help only.
- CI test partition hole: none; no Category filter exists.

## Lane disagreements settled

- F2 severity: HIGH (design) vs MEDIUM (surface). Settled MEDIUM, reasoning in F2.
- QA lane mutation tally: the lane wrote "7 killed, 4 survived"; its own table shows M1, M3, M4, M7, M8, M9 killed (6) and M2, M5, M6, M10, M11 survived (5, of which M2 is equivalent). The table is used here.
- `content_request` row counts 348 / 426 / 529: same table read at different times and with `immutable=1` (WAL ignored) vs `mode=ro`. Not a disagreement about the defect.
- Dead code: architecture found no dead type; proxy-security found a dead method pair (F23). Both are right: the scan was per type.

## Still open

- Whether a rewritten thread `previous_message_id` continuation succeeds upstream; capture stops 2026-10-26.
- Reducer-scoring record (`2026-10-24-reducer-scoring-results.md`) not audited for in-sample vs held-out scoring.
- Streaming relay block renumbering and held `tool_use` blocks not deep-read.
- Titanium `ConnectionTimeOutSeconds` unset: whether a long silent generation is cut is unmeasured.
- Root regeneration at expiry (2027-11-23) and origin of 10 stale keychain roots.
- First-run start output, CA-untrusted client experience, Windows and Linux flows, Windows ACLs.
- F10 poison row not reproduced by test; F28 flake cause not isolated.
- CCR fallback path (ADR 0005 F13) not mutated.

## Owner questions

1. F1: limit `prose.dedupe` to consecutive runs with a `[×N]` marker, or turn it off until that ships?
2. F2: add `--version` and reject an unrecognised first argument?
3. F3/F4: retention age for `content_request`/`content_chunk`, and a default age or byte cap for capture?
4. F8: name-constrain and rename the root CA, accepting one re-trust?
5. F10: dead-letter, drop, or halt on a poison queue row?
6. F12: supersede ADR 0005 with the shipped defaults, or is the per-kind gate still owed?
7. F13: enforce ADR 0006 at namespace level, or drop the claim?
8. F15: should unknown `Proxy:*` keys fail startup or warn?
9. F19/F26: show net and percentage saved in the gain headline; hide the "Unavailable" flags?
10. F21: keep `build-bdd` as a duplicate required check, or retarget it; add a CI docs job?
11. F25: rename `Hosting/` and split `Integration/` by concept?
12. F27: is a shared multi-user host in scope?
