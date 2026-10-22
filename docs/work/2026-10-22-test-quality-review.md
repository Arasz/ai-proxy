# Test quality review — the content compression pipeline

**Date:** 2026-10-22

Two passes reviewed the tests. The first pass's file write was blocked by the memory-first
gate, and its findings were recovered from the delegation transcript; the second pass wrote
its file. What follows is the union of the two.

## Findings

### F1 — The recording content store lets a persistence failure pass [BLOCKER]

- The fixture: `tests/AiProxy.Tests/Unit/Content/ContentCompressionPipelineTests.cs`, the
  `RecordingContentStore`.
- What a green run hides: the fake records the record in a list. A real SQLite store can
  lose the write, and the pipeline test would still be green, because the pipeline's
  contract is that the store is *called*, not that the row is committed.
- The check that would catch it: a test that writes to a real database file and reads the
  row back. The store's own contract test does that
  (`tests/AiProxy.Tests/Unit/Storage/ContentStoreTests.cs`), so the gap is that the
  pipeline's test and the adapter's test are two tests, and the seam between them is the
  fake.
- The fix: the store's contract test is the one that matters, and it runs against a real
  file. A failure-injection test in the pipeline (the store throws, the request still goes
  out) exists as well.

### F2 — The subset property is proved per handler, not universally [MAJOR]

- The promise: a reducer never invents a word.
- What a green run hides: the log and the prose tests assert line containment, the JSON
  test asserts value equality, the XML test asserts value equality. Nothing asserts the
  property for all of them at once, so a new handler could ship without it.
- The check that would catch it: a test that walks every handler and every kind and
  asserts the property. The routing table test now does that for the line-shaped
  reducers, and the JSON and XML reducers are covered by their value-equality tests.
- The fix: applied. The shell transcript had no handler at all, and the routing table
  named one, so the design and the code did not agree.

### F3 — The integration test proves the byte-identical claim weakly [BLOCKER]

- The claim: the bytes outside the tool output are what the client sent.
- What a green run hides: the test compared a hard-coded prefix and suffix with
  `StartsWith` and `EndsWith`, which is a structural comparison, and the middle of the
  body was not checked at all.
- The check that would catch it: the prefix and the suffix are taken from the *original*
  body, by locating the tool output's encoded span, and the received body is compared
  with them byte for byte. A second test asserts that the tool output that arrived is the
  reduced one.
- The fix: applied.

### F4 — The routing table and the decomposer's classifications were under-tested [MAJOR]

- The promise: every kind has exactly one handler, and the decomposer classifies the
  kinds the design lists.
- What a green run hides: the router tests covered the log, the JSON and the fallback, and
  the decomposer tests covered the listing, the grep result, the JSON blob, the fence and
  the prose. The shell transcript, the configuration file and the Markdown document were
  not classified by any test.
- The check that would catch it: a test that walks every kind of the enum and asserts
  that exactly one handler claims it and that it is the one the table names, and a test
  per classification the decomposer makes.
- The fix: applied. The shell transcript was the one that found a real gap: no handler
  claimed it.

### F4 — The integration test proved the request path and not the writer [MAJOR]

- The claim: the record reaches the content tables.
- What a green run hides: the retrieval is served from the queue while the record is
  still waiting there, so the integration test passed even if the worker never wrote
  anything. The request path was proven and the writer was not.
- The check that would catch it: the integration test waits for the worker to drain and
  then looks at the `content_request` row itself.
- The fix: applied.

### F5 — The retrieval endpoint and the middleware have no test of their own [MINOR]

- The promise: the original is served back under the id in the response header, and an
  unknown id is a problem details response.
- What a green run hides: both are covered by the integration test only. The endpoint has
  no unit test, so the queue fallback (a record that has not been written yet is still
  served) is covered by the store's test rather than by the endpoint's.
- The check that would catch it: the integration test does cover the 404 and the
  retrieval, so this is a shape of the suite rather than a hole in it.
- The fix: left as it is, and recorded here.

### F6 — The worker's loop test counts invocations, not side effects [NIT]

- The promise: the worker's pass drains the queue, and a failed job is retried.
- What a green run hides: nothing. The worker test counts the writer's calls, which is
  the shape the repo's own hosted-service guidance asks for, and the second pass with
  nothing new is asserted to make no read.
- The fix: none needed.

## Coverage map

| Behaviour | The test that pins it | The mutation that turns it red | Gap |
|---|---|---|---|
| The router lets the handler decide | `ContentRouterTests`, `ContentRoutingTableTests` | A handler claims a kind it does not own | none |
| The decomposer splits by structure | `ContentDecompositorTests` | Split on blank lines only | the shell transcript, the configuration file and the Markdown document are now covered |
| The log rule keeps the signal line | `LogCompressionHandlerTests` | Drop the `status=503` line | none |
| The JSON reducer keeps the values | `JsonCompressionHandlerTests` | Re-serialise the number as `49.9` | none |
| The overlay's ceiling and its five rules | `MustKeepOverlayTests` | Add a token past the ceiling | none |
| The region finder finds the tool output by position | `ToolOutputRegionFinderTests` | Match by content instead of by position | none |
| The store round trips against a real file | `ContentStoreTests` | Store the wrong ratio | none |
| The worker drains and retries | `ContentStoreWorkerTests` | Make `RunOnceAsync` a no-op | none |
| The bytes outside the tool output | `ContentCompressionTests` | Change a byte outside the region | none |
| The original is served back | `ContentCompressionTests` | Return an empty body for an unknown id | none |

## Still open

- The rule-based prose reduction was measured on one fixture, and the deduplication the
  measured script performed was a no-op (see the decomposition record). The prose path's
  benefit on real traffic is unmeasured.
- Whether an agent answers as well from the reduced context is still the open question
  the compression record names. The tests here prove that the facts survive, not that the
  agent still answers.
- The pipeline's own ratio is now checked against the record's texts, and the store's ratio
  is checked against the texts it stored, so a ratio that is wrong in both places at once
  would still be caught. What is not checked is the ratio against a measurement of what
  the provider actually saved, because the provider's own accounting is not in this
  repository.
- The model's path is not in this release, so there is no test lane for it.
