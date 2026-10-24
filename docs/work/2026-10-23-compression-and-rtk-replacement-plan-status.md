# Research: what the compression and rtk-replacement plan has done and what it still owes

**Date:** 2026-10-23
**Question:** For the active implementation plan `aip-compression-and-rtk-replacement`, which
steps are finished with evidence on `main`, which are not, and what blocks each one that is left?

The plan is the task-graph DAG `aip-compression-and-rtk-replacement` (revision 12), stored in
`.ai-badger/task-tracking/tracking.db` and exported to
`.ai-badger/task-tracking/plans/2026-10-23-aip-compression-and-rtk-replacement.md`. Its task
description is
[2026-10-23-compression-state-model-readiness-and-rtk-pipeline.md](2026-10-23-compression-state-model-readiness-and-rtk-pipeline.md).
The older routing plan `aip-machine-wide-provider-routing-defaults` is still open in the same
database, and F9 covers it.

```chart:matrix
title: plan steps by status
step, done, open
docs-pointers, 1, 0
rtk-keep-verbatim, 1, 0
proxy-port, 1, 0
part-isolation, 1, 0
client-inventory, 1, 0
model-gate, 0, 1
rtk-probes, 0, 1
scoring-harness, 0, 1
digest-verdict, 0, 1
model-decision, 0, 1
rtk-replacement-scope, 0, 1
```

## Findings

### F1 — Five of the eleven steps are complete, and each has merged evidence on main [READ]

| step | what it delivered | merged as |
|---|---|---|
| docs-pointers | research records cite the committed kit; 30 cited paths, 0 missing | #29 |
| rtk-keep-verbatim | unanchored keep-verbatim rule in the home `pipeline.toml`, 11/11 rows ok | no PR, home config |
| proxy-port | `PROXY_PORT` for the listener and system proxy, 0.5.0 | #27 |
| part-isolation | a throwing reducer keeps its own part, 0.5.1; test gaps closed later | #31, #33 |
| client-inventory | the agent client inventory record | #30 |

Every step's completion record names its evidence, and every PR listed is merged. `VERSION` on
`origin/main` is 0.6.0.

**Evidence:** `tracking.db`, `plans.payload` for `aip-compression-and-rtk-replacement`, the
`completion.evidence` of each step; `gh pr list --state all` shows #27–#33 `MERGED` on 2026-10-23;
`git show origin/main:VERSION` returns `0.6.0`.

### F2 — model-gate is marked in progress, but its record is merged and the ruling after it voids that record [READ]

PR #28 merged the record
[2026-10-23-model-gate-prose-share.md](2026-10-23-model-gate-prose-share.md). It puts the ceiling
on a prose model's saving at 1.03% of request tokens, or 1.64% if rtk's counts are post-filter.
Both are under the 2% line. The ceiling is that low only because the pipeline reads tool output
and nothing else, so tool results set the upper bound (10.5% of request tokens).

On the same day the owner ruled that compression covers the whole request: system prompt, user
and assistant turns, and tool definitions. That ruling also made any gate computed on tool
results alone void. The step therefore can't be closed on the merged record. It needs a new
measurement over whole requests.

**Evidence:** `docs/work/2026-10-23-model-gate-prose-share.md`, F3 and F4; step `model-gate` in
`tracking.db` has `started_at` 2026-10-23T10:15:11Z and no `completion`; the owner ruling is
recorded in the project memory `compression-scope-whole-request.md` (written 2026-10-23 15:45 UTC).

### F3 — A corpus for the re-run is being collected now: 81 captured request bodies as of 18:10 UTC [MEASURED]

PR #32 (0.6.0) added `PROXY_CAPTURE`, and `~/.ai-proxy/capture.db` exists. That closes the gap
in F1 of the model-gate record ("no stored corpus of request bodies"). The captured rows cover
four minutes, which is not yet a corpus. The step's own instructions ask for a week.

**Evidence:** `sqlite3 -readonly ~/.ai-proxy/capture.db "select count(*), min(captured_at),
max(captured_at) from capture_request"` → `81 | 2026-10-23T18:06:37Z | 2026-10-23T18:10:34Z`.
Run once, on the operator's MacBook Air M4, on 2026-10-23. Only counts were read, never bodies.

### F4 — The pipeline still compresses tool-output regions only, so the whole-request scope is unbuilt and has no plan step [READ]

`CompressAsync` returns the body unchanged unless `ToolOutputRegionFinder` finds a region. Among
the plan's eleven steps, none widens the scope. Doing so also needs a design that meets the
owner's no-cache-invalidation constraint: compress each region as a pure function of its own text,
use no recency rules, and leave signed thinking blocks untouched.

**Evidence:** `src/AiProxy.Core/Content/ContentCompressionPipeline.cs:56-71` on `origin/main`;
the plan's step list in `tracking.db`; the constraint is in `compression-scope-whole-request.md`.

### F5 — ai-proxy has no recoverable (CCR-style) compression or retrieval tool, and the plan defers it to the last step [READ]

A search of `src/` for `retriev`, `ccr` or head+tail returns nothing. The owner asked for
CCR-style recoverable compression with an in-request retrieval tool. In the plan, the only place
that touches it is `rtk-replacement-scope`, which drafts an ADR and waits on `rtk-probes`.
Building it is outside the plan for now; the context says implementation steps arrive "via
plan_replace after the decision steps finish".

**Evidence:** `git grep -n -i -E 'retriev|ccr|head.?\+?.?tail' origin/main -- src` (no hits);
the `task_context` and `rtk-replacement-scope` instructions in `tracking.db`.

### F6 — rtk-probes is unblocked but not started, and F13 and F14 of its source record are still unverified [READ]

`rtk-probes` depends only on `proxy-port`, which is complete. The probes have not run:
F13 (does the CCR threshold of `tokens_gt = 1000` hold?) and F14 (do rtk-proxy's upstream calls
pass through ai-proxy?) are still graded `UNVERIFIED` in the source record, and no later record
answers them.

**Evidence:** `docs/work/2026-10-23-compression-state-model-readiness-and-rtk-pipeline.md:128,135`;
`git grep -E '\bF1[34]\b' origin/main -- docs/work` finds no later record; step `rtk-probes` has
no `started_at`.

### F7 — scoring-harness is unblocked; the scorer exists, the harness does not [READ]

`scoring-harness` depends only on `docs-pointers`, which is complete. `scripts/compression_score.py`
and its tests are on main (#26). No harness exists that runs candidates over payloads with
pre-registered question/answer pairs, and the control cases the step requires (identity, a
summarizer negative control) are not built.

**Evidence:** `git ls-tree -r --name-only origin/main scripts` lists `compression_score.py` and
`test_compression_score.py` and no harness; step `scoring-harness` has no `started_at`.

### F8 — The remaining steps wait on these three [INFERRED]

From the dependency edges in `tracking.db`:

- `digest-verdict` waits on `scoring-harness`.
- `model-decision` waits on `scoring-harness` and `model-gate`.
- `rtk-replacement-scope` waits on `rtk-probes` (and `client-inventory`, which is done).

So the critical path is `scoring-harness` → `model-decision`, plus `rtk-probes` →
`rtk-replacement-scope`. `model-gate` gates `model-decision` and needs days of capture (F3), so
it is the slowest input by wall clock.

### F9 — The routing-defaults plan looks implemented on main, but its tracking still shows it open [INFERRED]

`aip-machine-wide-provider-routing-defaults` shows S1 `https-mitm` unchecked and S2–S3 pending.
Main has all of these pieces:

- the canonical host catalog (four hosts, including `openrouter.ai`)
- HTTPS inspection for those hosts only
- an integration test that decrypts a provider request and trusts the proxy root only through a
  custom chain, never the OS store
- a pass-through test for unknown destinations
- no `PROXY_*_UPSTREAM` or `PROXY_PASSTHROUGH` in `src`, `docs/reference`, `docs/how-to` or
  `README.md`
- changelog `0.4.0-machine-wide-forward-proxy.md`

This reasons from those reads. Nobody re-ran the tests or checked each acceptance criterion
against them, so the plan still needs a close-out pass. It does not need implementation work.

**Evidence:** `src/AiProxy.Core/Providers/ProviderHostCatalog.cs:6-12`;
`tests/AiProxy.Tests/Integration/HttpsInspectionTests.cs:19,42,65`;
`tests/AiProxy.Tests/Integration/ForwardProxyTests.cs:11`; `docs/changelog/0.4.0-machine-wide-forward-proxy.md`;
`git grep -E 'PROXY_[A-Z]+_UPSTREAM|PROXY_PASSTHROUGH'` with no hits; all on `origin/main`.

### F10 — Whether the canonical hosts need subdomain matching is unverified [UNVERIFIED]

Acceptance criterion S2 `canonical-hosts` says "with relevant subdomains where documented".
`ProviderHostCatalog.Find` matches exact hosts only. Nobody checked whether any provider documents
a subdomain that agents actually use.

## Still open

- **The model-gate bound over whole requests.** It needs a few days of `PROXY_CAPTURE` (F3), then
  `ContentDecompositor` run over every message role instead of tool output only. Until that
  exists, `model-decision` can't be ruled.
- **A plan step for the whole-request scope (F4) and one for CCR-style retrieval (F5).** Neither
  is in the DAG. Both need a `plan_replace` and a design that keeps cached prefixes byte-stable.
  Whether to add them now or wait for `rtk-replacement-scope` is the owner's call.
- **Should the routing-defaults plan be closed (F9)?** One run of the named tests, checked against
  each acceptance criterion, settles it.
- **Do the canonical hosts need subdomain matching (F10)?** Settled by reading each provider's API
  host documentation.
- **Does `capture.db` stay small enough to run for a week?** It is 3.8 MB plus a 4.2 MB WAL after
  81 requests. Nobody projected the growth.
