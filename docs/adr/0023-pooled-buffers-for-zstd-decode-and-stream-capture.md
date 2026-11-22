# 0023 - Pooled buffers for zstd decode and stream capture

Status: Proposed 2026-11-23

## Context

A live proxy had 51 MB of its 64 MB managed heap on the large object heap. 0.35.1 removed the
request-body copies. Two measured costs stay
([memory profile](../changelog/0.35.1-request-path-copies.md)):

- `ZstdRequestBody.Decode` allocates about twice the decoded size: a `MemoryStream` that grows
  from 8 KB by doubling (1,057,184 B to decode 524,288 B).
- `ResponseBuffer.For` allocates 131,344 B for each captured event stream, and keeps it for the
  whole stream. Two writers append to it (Titanium `DataReceived` and the CCR relay copy loop).
  `AfterResponse` reads it and then releases it. Titanium raises `AfterResponse` from separate
  HTTP/1, HTTP/2 and HTTP/3 paths, so the code cannot rely on one order between the last write and
  the release.

.NET 11 has a native zstd decoder in `System.IO.Compression` (`ZstandardDecoder`). Probe on
.NET 11.0.100:

| Frame | `TryGetMaxDecompressedLength` | `Decompress` |
|---|---|---|
| one-shot, size declared | exact size | `Done` |
| streamed, no size | upper bound (655,360 for 524,288) | `Done` |
| two frames | sum of both | `Done` after the first frame only |
| cut short | false | `NeedMoreData` |
| not zstd | false | `InvalidData` |

## Decision

1. `ZstdRequestBody.Decode` uses `ZstandardDecoder` with a 2^24 window limit, in place of
   ZstdSharp. It rents one scratch buffer of the frame bound (at most the 16 MiB limit, the
   limit when there is no bound) from `ArrayPool<byte>.Shared`, decodes every frame into it, and
   copies the exact result out once. The scratch buffer goes back to the pool in `finally`.
   ZstdSharp leaves `src/` and stays a test-only package.
2. `ResponseBuffer` rents its two 64 KB arrays (head and tail) from an `ArrayPool<byte>`. The
   64-byte line head stays a plain array.
3. The guard: `Append`, `Body` and `Release` take one lock per buffer. `Release` returns the
   arrays once and marks the buffer released. An `Append` after `Release` does nothing. `Body`
   always returns a new array, so a pooled array never leaves the buffer.

## Consequences

- A zstd body costs its decoded size once, for both frame kinds.
- A captured event stream costs no 64 KB arrays once the pool is warm.
- The lock costs one uncontended enter per appended chunk.
- A buffer that is never released (an exchange that dies before `AfterResponse`) does not return
  its arrays; the pool rents new ones and the GC collects the lost ones, the same as today.
- The pool keeps up to one scratch buffer per size bucket per core alive, up to 16 MiB, until
  the pool trims it on memory pressure.
