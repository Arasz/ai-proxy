# Test honesty audit — the content compression pipeline

## Findings
### F1 — Fake SQLite store hides adapter failures [BLOCKER]
- Claim: `IContentStore` port lets tests run without DB.
- Fake hides: (a) wrong `PRAGMA data_version` polling loses queued writes; (b) WAL mode missing → writer/reader deadlock; (c) adapter writes `ratio=1.0` for pass-through but never verifies against measured table; (d) `chunk_count` disagrees with `CompressedPart` count because mock returns hardcoded row.
- Check: `StoreTests.Ratio_Against_Real_Adapter` with real `Microsoft.Data.Sqlite` temp DB (not fake) + mutation altering adapter's `ratio` calculation → red.
- Fix: design must require at least one contract test against real SQLite (design-gate-audit F3, F4).

### F2 — Golden test needed for decomposer port from pyc [MAJOR]
- The source `reducer.py` is lost; only `~kompress-work/__pycache__/reducer.cpython-312.pyc` survives (`dis` readable, F1 of decomposition doc).
- What proves port behaves like original: golden fixtures (measured outputs 0.698–0.902) + property tests (subset, dedup order, overlay token order). Golden alone is not enough — property proves structural invariants across fixtures.
- Check: `DecomposerTests.Golden_Matches_Pyc_Disassembly` against fixtures from `reducer.cpython-312.pyc` + mutation altering split rule (e.g., split on `\n\n` instead of structure) turns red.
- Fix: design must list fixture sources (`__pycache__` disassembly + measured size table) and require both golden + property.

### F3 — Subset property ("never rewrites client's words") needs inclusion proof, not spot checks [MAJOR]
- Claim: reduced text ⊆ original text (per chunk; overlay rebuilds from subset + retained tokens).
- Spot-check misses: overlay could inject un-original tokens (`must-keep` directive matched differently); composited template could introduce whitespace not in original; pass-through path silently passes wrong chunk.
- Check: property test `CompressionTests.Reduced_Is_Subset_Of_Original_PerChunk` — for every `CompressedPart`, `compressed.Contains(original[i]) ...` via ordered line/token containment + mutation replacing reduced text with free-form text → red.
- Fix: design must declare the subset invariant per chunk, not globally.

### F4 — Ratio + path echo problem [MAJOR]
- Design stores ratio/path per chunk/per request; test that recomputes from same numbers is an echo (T0-04 / T0-03).
- Independent check: retrieve original from DB, independently compute `len(original)/len(compressed)` from stored `original_text`/`compressed_text`, assert equality against stored `ratio`; assert `path` equals handler table entry for `ContentType`; assert `chunk_count == parts.Count`.
- Check: `StoreTests.Ratio_Independent_Recompute` + mutation changing adapter's stored ratio to 0.5 without changing texts → red.

### F5 — Background worker: loop test must use `RunOnceAsync` + invocation count, not side-effect counts [BLOCKER]
- Per `dotnet-hosted-service-testing`: `StartAsync` doesn't run `ExecuteAsync` on caller thread; first `Advance` lost; side-effect counts encode fake artifacts; use `RunOnceAsync` seam; count invocations.
- Test of the loop that asserts "drained queue" via DB count passes even if loop never drains (echo of fake state). Must assert `RunOnceAsync` invocation > 0 after `content_queue` write, with `FakeTimeProvider` split-advance, and assert PRAGMA `data_version` called before SELECT (w1c).
- Check: `WorkerTests.Drain_Invocations_Only` mutation removing `PRAGMA` gate turns red; mutation making `RunOnceAsync` no-op passes falsely → catch via invocation count assertion.
- Fix: design must name `RunOnceAsync` seam and forbid side-effect-only assertions for loop.

### F6 — End-to-end byte-identical rest: needs byte equality, not structural comparison [BLOCKER]
- Design's last claim: only tool-output regions replaced; rest stays byte-for-byte.
- Weaker test (structural parse equality) lets through: whitespace changes, encoding shifts (UTF-8 BOM), newline normalization, template placeholder formatting differences.
- Check: `EndToEndTests.Body_ByteIdentical_Except_Regions` — split body on `{type_chunkId}` markers; for non-region segments assert `segment == original_segment` byte-by-byte (not `Equals`); mutation altering whitespace outside region turns red.
- Fix: design must specify byte-level invariant + marker-offset proof.

### F7 — Decomposer regexes ported from compiled pyc: no canonical original source [MAJOR]
(derived from F2 + F1 of both docs; gate honesty)
- The decomposer depends on regexes from `reducer.cpython-312.pyc` (dis-verified). No `reducer.py` source. A test that assumes the regex is correct cannot fail for the regex being wrong.
- Fix: design must document that regex source of truth is disassembly of `reducer.cpython-312.pyc`, not reconstructed memory.

## Fakes that are needed, and what each one must model

| Fake | Behaviour it must model | Test that proves it models it |
|---|---|---|
| `FakeIContentStore` (port-level) | SQLite adapter's real contract: `PRAGMA data_version` polling, WAL, `ratio` computed from texts, `chunk_count == parts.Count` | `StoreTests.Ratio_Independent_Recompute` against real adapter; mutation in adapter turns red |
| `FakeDecompositor` (for router/handler) | Splitting by structure (not guess); preserving `ChunkId`; returning `Template` with `{type_chunkId}`; leaving unclassified as `Prose` | `DecomposerTests.Golden_Matches_Pyc_Disassembly` + property test for subset |
| `FakeContentRouter` | Handler claims by `ContentType`; unclaimed returns `PassThrough` | `RouterTests.Handler_Claims_Only_Claimed_Type` |
| `FakeCompressionHandler` (log/JSON/...) | Per-rule structural reduce + overlay; `Path` set to table entry; `Ratio` computed; never rewrites client's words (subset) | `OverlayTests.Ceiling_And_Subset`; mutation to rule output turns red |
| `FakeICompositor` | Rebuild from `Template` + parts; missing handler → part unchanged; byte-equality on non-region segments | `CompositorTests.ByteEqual_Except_Regions` |
| `FakeBackgroundWorker` / `RunOnceAsync` seam | One pass drains queued job; `PRAGMA data_version` called before SELECT; invocation count trackable; no side-effect-only assertion | `WorkerTests.Drain_Invocations_Only` with `FakeTimeProvider` split-advance |

## Still open
- Whether `reducer.cpython-312.pyc` disassembly fully covers the decomposer's regex set (only verified against `reducer.cpython-312.pyc`, not `bench_reducers.cpython-312.pyc` — both need comparison).
- Whether the design names test file for each gate (design-gate-audit F5 notes no file names: design must add `tests/StoreTests.cs`, `tests/DecomposerTests.cs`, `tests/WorkerTests.cs`, `tests/EndToEndTests.cs` before TDD starts per `tdd-mandatory`).
- Whether `AiProxy.Storage` adapter exists (design names it; `ls src/` shows only existing core); adapter must exist before contract tests can run against real SQLite.
- Whether the extractive model artifact (`kompress-v2-base`, 149M, 0.36s) is delivered: design says v1 skips it; without model, prose path must be rule-path (design decision 4 resolved: wait for model); test plan must reflect no-model default.
- The subset property proof requires an ordered token/line containment checker — not defined in design; must be specified before property tests are written.
