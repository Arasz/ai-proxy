# Plan draft (decomposition lens)

## Steps
### step-01 — region finder & decomposer seam (core, pure)
- Files: src/AiProxy.Core/RegionFinder.cs, Decompositor.cs, IDecompositor.cs, Decomposition.cs
- Depends on: none
- AC: finder replaces only tool_result regions; rest byte-identical — check: RegionFinderTests.ByteEqualityAfterRebuild asserts rebuilt body == original except replaced regions
- Notes: must catch false-positive region match; guard clauses; XML docs

### step-02 — router + compression handlers (core, pure)
- Files: src/AiProxy.Core/ContentRouter.cs, IContentRouter.cs, ICompressionHandler.cs, Handlers/*.cs
- Depends on: step-01 (needs Decomposition parts)
- AC: router picks handler per ContentType; handler reduces subset — check: RouterTests.HandlesLogJsonSourceCode asserts correct path string returned
- Notes: parallel with step-03 after interface freeze

### step-03 — compositor + executor seam (core, pure)
- Files: src/AiProxy.Core/Compositor.cs, ICompositor.cs, Executor.cs
- Depends on: step-01
- AC: composited body equals original minus dropped parts — check: CompositorTests.PartialReduceMaintainsTemplate
- Notes: integrates finder + router; failure = dropped non-tool text

### step-04 — store port interface (core, pure interface)
- Files: src/AiProxy.Core/IContentStore.cs, ContentRecord.cs, StoragePort.cs
- Depends on: none
- AC: port records original + chunk count; retrieval by requestId — check: ContentStorePortTests.MockRecordsAndRetrieves
- Notes: defines the adapter seam; must carry ChunkCount and Path

### step-05 — SQLite adapter (AiProxy.Storage)
- Files: src/AiProxy.Storage/SqliteContentStore.cs, Schema.cs, Directory.Packages.props entry
- Depends on: step-04
- AC: tables content_request + content_chunk; WAL; one writer; PRAGMA data_version poll — check: SqliteStoreIntegrationTests.TempDbRoundtrip asserts record stored and retrieved identical
- Notes: Dapper + Microsoft.Data.Sqlite versions only in Directory.Packages.props; Log partial class with EventId

### step-06 — queue + worker + pub/sub
- Files: src/AiProxy.Storage/ContentQueueWorker.cs, PubSub.cs, content_queue table access
- Depends on: step-05
- AC: queue row written before forward; worker drains and writes store — check: QueueWorkerTests.DrainsAndStoresWithoutDataLoss
- Notes: producer + poller separate connections; channel rejected (process death loses record)

### step-07 — host wiring + middleware
- Files: src/AiProxy/Middleware/CompressionPipelineMiddleware.cs, Program.cs, DI registration
- Depends on: step-03, step-06
- AC: middleware applies full pipeline; forwards compressed body — check: MiddlewareIntegrationTests.FullPipeline_ForwardCompressed
- Notes: integration join; proves seams 1-5 plus host

### step-08 — retrieval endpoint + config
- Files: src/AiProxy/Controllers/ContentRetrievalController.cs, CompressionConfig.cs
- Depends on: step-05, step-07
- AC: endpoint serves dropped text by GUID; config defaults off/minimum 512 — check: ControllerTests.GetByRequestId_ReturnsOriginalChunk
- Notes: XML docs all public; ArgumentNullException.ThrowIfNull; braces on all conditionals

### step-09 — docs + changelog + VERSION
- Files: docs/changelog/2026-10-22-content-compression-pipeline.md, VERSION, docs/work/README.md update
- Depends on: step-07
- AC: changelog references store port and retrieval endpoint — check: file exists, mentions IContentStore and retrieval endpoint

## Joins
step-07 (integration): full pipeline — region finder → decomposer → router → handler → store (queue then adapter) → compositor → rebuilt body → forward, plus retrieval endpoint returns original chunk. Fails if any seam breaks.

## Parallel groups
- Group A: step-01, step-04 (independent core seams)
- Group B (after A, interfaces frozen): step-02 || step-03
- Group C (after step-04): step-05
- Group D (after B + C): step-06 || step-07
- Group E (after D): step-08 || step-09
