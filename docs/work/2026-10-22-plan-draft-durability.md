# Plan draft (durability lens)

## Steps
### step-d1 — SQLite adapter + IContentStore port (AiProxy.Storage)
- Files: src/AiProxy.Storage/, Directory.Packages.props
- Depends on: none
- AC: d1a store writes/read round-trip — check: `dotnet test --filter "FullyQualifiedName~StoreRoundTrip"` passes (fails if value wrong)
- AC: d1b WAL + busy-timeout — check: concurrent writer/readers do not throw SQLiteException
- Notes: single-writer rule; separate conn for PRAGMA data_version poll

### step-q1 — content_queue table + queue writer
- Files: src/AiProxy.Storage/QueueWriter.cs, schema sql
- Depends on: d1
- AC: q1a write commits before forward — check: test asserts `content_queue` row exists after `StoreAsync` returns, before response finishes
- AC: q1b process dies between queue and forward — check: queued row survives; retrieval by requestId finds original
- Notes: queue is the durability seam; must never lose original

### step-w1 — BackgroundService worker + poll (PRAGMA data_version)
- Files: src/AiProxy/Worker.cs, tests using FakeTimeProvider / RunOnceAsync
- Depends on: q1
- AC: w1a worker drains queued job — check: `RunOnceAsync` invocation count > 0 after queue write (fails if 0)
- AC: w1b failed job is retried/not lost — check: job remains in queue after failure; retry observable on next poll
- AC: w1c PRAGMA poll not blind timer — check: test asserts worker calls `data_version` before SELECT
- Notes: read dotnet-hosted-service-testing reference; no side-effect assertions only counts

### step-r1 — retrieval path (GetAsync unknown id)
- Files: src/AiProxy.Core/IContentStore.cs, adapter
- Depends on: d1
- AC: r1a unknown id returns null — check: assert null; fails if throws or returns empty
- Notes: caller must handle missing; never invent original

### step-j1 — integration (request path -> queue -> worker -> retrieval)
- Files: integration test
- Depends on: q1, w1, r1
- AC: j1 full pipeline end-to-end — check: compressed body forwarded, original retrievable by id

## Joins
step-j1 — proves queue durability, worker guarantee, and retrieval together; must observe that original survives process-crash between queue write and worker run (simulated by reading DB directly after writer exits).

## Parallel groups
- d1 standalone
- q1 depends d1; w1 depends q1; r1 depends d1 — r1 can parallel with w1 after d1; j1 joins all
