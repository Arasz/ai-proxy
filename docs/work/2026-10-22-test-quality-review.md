# Test quality review — the content compression pipeline

## Findings
### F1 — RecordingContentStore lets persistence failures pass [BLOCKER]
- tests/AiProxy.Tests/ContentCompressionPipelineTests.cs (fixture)
- Real SQLite store could lose writes; fake records in-memory
- Mutation: inject `throw new IOException()` in real store; check that `RecordingContentStore` never surfaces it — no red test
- Fix: add failure-injection test against real store

## Coverage map
| Behaviour | The test that pins it | The mutation that turns it red | Gap |
|---|---|---|---|
| Routing | Integration/ContentCompressionTests.cs:22 | Change route table entry — passes (weak) | Office-hours routing untested |
| Decomposer | PipelineTests — partial | Remove classification — still green | All three classes unverified |
| Overlay ceiling | none | — | Missing |

## Still open
Audit fakes table: only 1 of 3 cases answered (RecordingStore); RecordingWriter unverified. Subset property proved only for single handler; not per handler. Integration passes weak claim only; depends on worker poll gate = flake risk.
