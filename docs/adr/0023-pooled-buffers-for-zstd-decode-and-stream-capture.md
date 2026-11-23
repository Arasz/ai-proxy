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
2. `ResponseBuffer` keeps every byte in rented arrays. An event stream rents its 64 KB head and
   tail. A whole body (up to 4 MiB) starts at 16 KB and grows by renting an array twice the size
   and giving the old one back. The 64-byte line head stays a plain array.
3. `ResponseBuffer.Body` is replaced by `Read<T>(Func<ReadOnlyMemory<byte>, T> reader)`. The
   reader gets the kept bytes for the length of the call and must not keep them. A whole body is
   lent in place. An event stream is stitched straight into one rented array (no scratch tail)
   that goes back to the pool when the reader returns, also when it throws.
   The guard: `Append`, `Read` and `Release` take one lock per buffer. `Release` returns the
   arrays once and drops the strategy, so a second `Release` and any later `Append` do nothing,
   and `Read` after `Release` lends empty memory. The pool overload of `For` is internal, for
   tests.
   Every use of a rented array goes through a window of the size the code asked for
   (`AsSpan(0, n)`, `AsMemory(0, n)`), never through the array itself, because a pool may lend a
   longer array (`Shared.Rent(100_000)` gives 131,072 bytes).
4. Returned arrays are not cleared. They hold prompt and response bytes, but only this process
   can rent them, and clearing would cost a write of every byte.

## Measured

BenchmarkDotNet 0.15.8, in-process (it does not yet know .NET 11 as a runtime), Apple M-series.
One response appended in 4 KB chunks, read, and released; `Before` is `ResponseBuffer.cs` from
main, `After` is this change.

| Body | | Mean | Allocated | Gen2 per 1,000 |
|---|---|---|---|---|
| whole JSON, 1 MB | before | 206.7 us | 4,191,931 B | 235 |
| whole JSON, 1 MB | after | 67.6 us | 128 B | 0 |
| event stream, 256 KB | before | 148.4 us | 524,824 B | 83 |
| event stream, 256 KB | after | 120.7 us | 280 B | 0 |

The lock was measured on its own first, with four variants generated from this file (pool on or
off, lock on or off), on 1,024 appends of 256 B: about 1 us between the pooled variants, inside
the noise.

## Consequences

- A zstd body costs its decoded size once, for both frame kinds.
- A captured response costs no arrays once the pool is warm, and causes no gen2 collections.
- The lock costs one uncontended enter per appended chunk.
- A buffer that is never released (an exchange that dies before `AfterResponse`) does not return
  its arrays; the pool rents new ones and the GC collects the lost ones, the same as today.
- The pool keeps up to one scratch buffer per size bucket per core alive, up to 16 MiB, until
  the pool trims it on memory pressure.
