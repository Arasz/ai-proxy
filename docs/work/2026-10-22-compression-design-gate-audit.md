# Design gate audit — the content compression pipeline

## Findings
### F1 — Region finder's value-set matching can rewrite un-sent text [DEFECT]
- Claim: region matcher keys on three tool-output shapes rather than envelope.
- Verdict: without a path-tracking provenance, a match can emit a rewritten region the client never sent.
- Evidence: design open-decision #2; no ToolOutputRegionFinder spec exists (src/AiProxy.Core/Compression has only ICompressionPipeline, not region finder).
- Check that catches it: `RegionFinderTests.IsMatchOnly_MapsToOriginalRegion` mutation: replace region text with different text of same shape → must stay red (must not rewrite).
- Fix: design must include a provenance field (original substring + offset) per matched region.

### F2 — Must-keep overlay over-protects; measured savings eaten [DEFECT]
- Claim: overlay grows five new rules closing the decisions-class hole.
- Verdict: rules 1+3+5 overlap heavily with log signal rule (FAIL/ERROR/status + lowercase state) → doubling protection eats savings on every log payload (measured 0.75 already tight).
- Evidence: design §The must-keep overlay; measured log 0.75, JSON 0.698 (research F1).
- Check: `OverlayTests.CeilingAt_0_75_On_LogFixture` — mutation adding duplicate rule does not lower size below 0.75.
- Fix: design must state ceiling (e.g., max +5% overhead above structural reduce) per payload, not just rules.

### F3 — Queue + worker: durability promised, only durability guaranteed; no latency test [FEASIBLE|DEFECT]
- Claim: producer writes `content_queue` then forwards; background worker drains.
- Verdict: crash-safe original is guaranteed (WAL + queued row); latency of drain is not guaranteed (poll interval unpinned); no test named.
- Evidence: docs/work/2026-10-22-sqlite-pubsub-and-store-layout.md F3-F5; design section The queue.
- Check: `StoreTests.QueueDrained_Within_5s` with FakeTimeProvider on poll — mutation removing `PRAGMA data_version` gate must turn red (blind timer only).
- Fix: design must pin poll = min(baseline, timeout/2) and name `StoreDrainLatencyTests`.

### F4 — Ratio / path / chunk count: wrong numbers can pass unnoticed [DEFECT]
- Claim: `ContentRecord` carries ratio, path, chunk_count.
- Verdict: ratio can be 0.0 when compressed==original (pass-through path) but design never pins expected; chunk_count unverified against `CompressedPart` count; path string compared to routing table only by name, never validated against handler.
- Evidence: design data model; measured sizes 0.698–0.902 (F1).
- Check: `ContentRecordTests.Ratio_Within_Measured_Range` mutation: set ratio=1.0 for JSON payload → must fail.
- Fix: design must specify assertion per path and require `chunk_count == parts.Count` invariant.

### F5 — "Rest of body stays byte for byte" false of design as written [BLOCKER]
- Claim: only tool-output regions replaced, rest unchanged.
- Verdict: decomposer replaces regions with `{type_chunkId}` placeholders; compositor rebuilds from template + parts — any template or placeholder formatting change breaks byte-for-byte; no proof of identity gate.
- Evidence: design stage diagram (G rebuild the body) and §What v1 does not do.
- Check: `IntegrationTests.Body_ByteIdentical_Except_Regions` — mutation altering whitespace in template must turn red.
- Fix: design must add invariant `original == template-with-placeholders` proof + byte-equality assert for non-region segments.

### F6 — Default-off configuration + 512-char threshold [DEFECT]
- Claim: `PROXY_COMPRESSION` off; `PROXY_COMPRESSION_MINIMUM` 512 chars.
- Verdict: 512 chars is above small JSON/log payloads (measured paths 0.698–0.902 on 7.5KB C#); at threshold=512 a 300-char log that could reduce to ~225 (0.75) never runs, silently losing savings; threshold should derive from payload-type floor, not fixed.
- Evidence: measured payload sizes in F1; config table.
- Check: `ConfigurationTests.Minimum_Leaves_SmallPayloads_Uncompressed` — mutation lowering to 100 must turn green for 300-char log.
- Fix: design must make threshold per-type (e.g., log=200, json=100) with global floor 512 only for prose.

### F7 — Seams conflict with existing `ICompressionPipeline` [BLOCKER]
- Claim: five new seams (IDecompositor, IContentRouter, ICompressionHandler, ICompositor, IContentStore).
- Verdict: `src/AiProxy.Core/Compression/ICompressionPipeline` already defined (CompressionRequest/Result, PassThroughCompressionPipeline); design never explains relation; host registers only `ICompressionPipeline`. Implementing both uncoordinated = two pipelines.
- Evidence: existing source; design seams list.
- Check: `HostTests.Only_One_Registered_Pipeline` mutation: register both → must fail.
- Fix: design must either retire `ICompressionPipeline` in favor of new seams, or specify adapter mapping from new seams to `CompressAsync`.

## Rulings on the five open decisions

1. Default off: RULING — keep off (proxy purpose is transparency, not shrink-by-default; stage changes provider input; measured 0.75 is not universal win). REASON — ADR-style safety; operator opts in per deployment.
2. Region finder value-set matching: RULING — add provenance tracking per region (source offset + length), don't rely on shape-only. REASON — open-decision asks precision; without provenance, value-set rewrite is real (F1).
3. Queue = second writer to SQLite: RULING — count as one writer (queue is a work table, not dataset; producer writes one row, worker writes read-table + chunk; single writer per table pair per design's own F6 of sqlite doc). REASON — sqlite-pubsub doc F6; must document this explicitly.
4. Prose at 0.75 measured justification: RULING — wait for model (0.36s, 149M ONNX, prose only; 0.75 rule path not compelling alone; design's own F4 disqualifies summarizer). REASON — research F1; rule path should be fallback, not v1 default.
5. Overlay ceiling: RULING — cap overlay overhead at +5% of structural-reduce size per payload (measured 0.75/0.698 already near ceiling; 5 rules can't grow past +0.05). REASON — otherwise savings eaten (F2).

## Still open
- Whether `ToolOutputRegionFinder` exists anywhere (not in src/); design must reference spec file.
- Whether the extractive model artifact (`kompress-v2-base` 149M, 0.36s) is delivered; design says v1 doesn't ship it — confirm port contract.
- Whether `AiProxy.Storage` adapter is created; design names project but no src/ entry exists.
- Specific test file names for gate checks (design does not name them); must be added before TDD starts (tdd-mandatory invariant).
