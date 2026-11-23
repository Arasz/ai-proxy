# 0023 - Pooled buffers for zstd decode and stream capture

Status: Proposed 2026-11-23 (implemented on task/aip-zstd-sizing-capture-buffer-pooling; awaiting owner review)

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
   ZstdSharp. A body with no frame bound is refused at once. Otherwise it rents one scratch
   buffer of the bound, capped at the 16 MiB limit, from `ArrayPool<byte>.Shared`, decodes every
   frame into it, and copies the exact result out once. The scratch buffer goes back to the pool
   in `finally`. The decoder is reset between frames: without `Reset` it reports `Done` again and
   consumes nothing. A frame whose window passes the limit makes the decoder throw `IOException`,
   which becomes `InvalidDataException`. ZstdSharp leaves `src/` and stays a test-only package.
2. `ResponseBuffer` rents its two 64 KB arrays (head and tail) from an `ArrayPool<byte>`. The
   64-byte line head stays a plain array.
3. The guard: `Append`, `Body` and `Release` take one lock per buffer. `Release` returns the
   arrays once and drops the strategy, so a second `Release` and any later `Append` do nothing.
   The head-and-tail `Body` always builds a new array, so a pooled array never leaves the
   buffer. Code reads only `HeadBytes`/`TailBytes` of each rented array, because a pool may lend
   a longer one. The pool overload of `For` is internal, for tests.
4. Returned arrays are not cleared. They hold prompt and response bytes, but only this process
   can rent them, and clearing would cost a write of every byte.

## Measured

BenchmarkDotNet 0.15.8, in-process (it does not yet know .NET 11 as a runtime), Apple M-series,
one stream of 256 KB of SSE appended in chunks, then `Body` and `Release`. The four variants
were generated from this `ResponseBuffer.cs`, with the lock and the pool switched on and off.

| Variant | 256 B chunks | 4 KB chunks | Allocated |
|---|---|---|---|
| allocated, no lock (as before) | 133.8 us | 126.4 us | 320.5 KB |
| allocated, lock | 147.0 us (SD 11.9) | 108.1 us (SD 7.6) | 320.5 KB |
| pooled, no lock | 125.8 us | 106.0 us | 192.5 KB |
| pooled, lock (this change) | 126.8 us | 109.3 us | 192.5 KB |

Eight streams in parallel, 1 KB chunks: 51.9 us before, 49.8 us after, per stream (ratio 0.97,
RatioSD 0.12).

- The lock costs about 1 us per 1,024 appends between the two pooled variants, inside the
  noise. The allocated pair is too noisy to give a figure.
- Pooling saves 128 KB per stream. Gen2 collections stay at about 40 per 1,000 streams in every
  variant: `Body` still allocates a 64 KB scratch tail and a stitched result of up to 128 KB,
  which goes to the large object heap. That is the next cost to remove.

## Consequences

- A zstd body costs its decoded size once, for both frame kinds.
- A captured event stream costs no 64 KB arrays once the pool is warm.
- The lock costs one uncontended enter per appended chunk.
- A buffer that is never released (an exchange that dies before `AfterResponse`) does not return
  its arrays; the pool rents new ones and the GC collects the lost ones, the same as today.
- The pool keeps up to one scratch buffer per size bucket per core alive, up to 16 MiB, until
  the pool trims it on memory pressure.
