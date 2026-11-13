# Research: implemented and planned ai-proxy features

**Date:** 2026-11-06
**Question:** Which ai-proxy features are done, and which are still planned or unresolved on the current main checkout?

## Findings

### F1 — Core proxy, routing, lifecycle, and diagnostics are implemented [READ]

- [x] Forward known provider traffic and pass unknown hosts through.
- [x] Provide `doctor`, `status`, `stop`, `gain`, `mcp`, and harness launcher commands.
- [x] Support proxy project attribution through proxy credentials and strip proxy-only headers.
- [x] Record-only Codex `chatgpt.com` traffic for harness sessions (owner live smoke is still pending).

**Evidence:** `src/AiProxy/Hosting/Integration/ProviderRequestInspector.cs:47-67`; `src/AiProxy/Cli/CliApplication.cs:90-106`; `.ai-badger/state.json:5-11`.

### F2 — Capture, gain reporting, compression, and retrieval are implemented [READ]

- [x] Optionally retain provider exchanges in a bounded SQLite capture store.
- [x] Enable compression with CCR or use pass-through when compression is disabled.
- [x] Report gain and CCR follow-up byte accounting.
- [x] Expose read-only MCP retrieval of retained originals by lines, pattern, or whole text.

**Evidence:** `src/AiProxy/ProxyServiceExtensions.cs:56-90`; `.ai-badger/state.json:33-38` (0.24.0 completion summary); `src/AiProxy/Mcp/RetrievalTools.cs:10-27`.

### F3 — Setup checks, installed-tool smoke, and supported package targets are implemented [READ]

- [x] Provide read-only `doctor` setup checks.
- [x] Exercise installed tool packages in CI on Ubuntu, Windows, and macOS.
- [x] Ship Windows x64/ARM64, macOS ARM64, Linux x64/ARM64, and Linux musl x64 targets.
- [x] Keep Alpine Tree-sitter code views best effort, with original-text fallback.

**Evidence:** `.ai-badger/state.json:51-65,78-84` (task completion summaries); `docs/reference/platforms.md:6-22`.

### F4 — Several owner-approved backlog items remain planned, open, or blocked [READ]

- [ ] Build quoted-echo references (owner chose build; the current task list names it as next work).
- [ ] Restore the prior system proxy after a crash.
- [ ] Clean up replaced roots from Linux NSS databases.
- [ ] Store the response message ID in capture records.
- [ ] Decide/build CSS and HTML code views.
- [ ] Give effect to the documented no-op settings `Proxy:Ccr:UnknownProviders` and `Proxy:Compression:Model`.
- [ ] Measure provider cache marker injection (blocked on paid credentials).
- [ ] Resolve MCP directory hosting, auth, and tenancy before directory listings.
- [ ] Extend compression beyond tool-output regions.
- [ ] Complete the owner live smoke of Codex `chatgpt.com` recording.

**Evidence:** `.ai-badger/state.json:1-3`; `docs/work/2026-11-04-installed-0.19.1-test-and-open-features.md:42-49`; `docs/work/2026-11-04-quoted-echo-references-plan.md:1-7`.

### F5 — Backlog statuses are read from records and have not been independently run through acceptance tests here [UNVERIFIED]

This checklist reconciles current source with the project task state and dated backlog records. I did not run tests or perform the pending live smoke during this inventory.

## Still open

- Confirm whether the open items in the November 4 backlog have changed since that record, beyond the completions summarized in current task state.
- Run focused acceptance checks for the listed done features if runtime proof is needed; this inventory establishes implementation evidence, not a fresh end-to-end pass.
