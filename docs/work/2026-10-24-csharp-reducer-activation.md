# Next task: activate the C# reducer with exact-file retrieval

Date: 2026-10-24
Status: next after the [inactive C# core build](2026-10-24-csharp-reducer-build.md); runtime activation blocked on CCR design and implementation.
Task: `aip-activate-csharp-reducer-with-retrieval`

The owner explicitly asked to retain these findings as the next step. They are release requirements, not optional cleanup.

| Gap | Required behavior | Acceptance check |
|---|---|---|
| Tool-call file paths do not reach the reducer | Correlate each result to its originating call by call ID and carry the full-file path through region, decomposition and part. Choose language by extension only. Uncertain, partial, multiple-path and missing-call cases use conservative fallback. | Integration fixtures with two different file calls and reversed results prove the correct path follows each result. Remove ID correlation and observe failure. Plain `.cs` reads route as source, while unknown paths never select lossy C#. |
| `/content/{id}` returns the whole request rather than the original file | A real model retrieval tool resolves the advertised reference to exactly the original file that generated that compacted view. Preserve the existing whole-request endpoint contract. No arbitrary host-filesystem reads. | Through the real proxy, fetch both files from one request and compare exact bytes. Include same basenames, duplicate local chunk numbers, missing/expired references, queued writes beyond 64 and concurrent writer handoff. Every published hint must resolve unambiguously. |
| Failed store writes still allow compressed output | Original content is retained and immediately readable before publishing a retrieval-dependent compacted view. If that guarantee fails, forward the original request without a successful file hint. | Inject store failure, queue failure and unavailable lookup; assert original request bytes and no published retrieval hint. Remove the failure fallback and observe failure. Preserve the intended policy of unrelated reducers. |

## Ordering

1. Complete the existing whole-request plan's `ccr-design` and implement its real tool contract. This task consumes that contract; it does not invent a competing tool. Include in-request retrieval behavior, missing/expired original handling, client modes and streaming in the contract.
2. Implement trusted file context and unambiguous file identity/readability, with failing integration tests first.
3. Wire the staged C# reducer, render the real hint, replace the old handler, and expose defaults in `CompressionOptions`: comments on, imports on, renaming off.
4. Repeat all four [approved design acceptance checks](2026-10-24-code-reducer-design.md#acceptance), including 36 model edit runs with renaming off/on through actual proxy retrieval, token comparison and map-aware scoring with full-file fetch cost. Record VERSION and changelog only for the resulting release.

The task graph records these gates separately from the inactive core task. Other languages remain separate later PRs in the approved order.
