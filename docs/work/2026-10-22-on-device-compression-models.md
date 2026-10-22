# Research: which on-device model shrinks an agent's context, and the routing table that follows

**Date:** 2026-10-22
**Question:** Which small models can run on an M4 to reduce the size of the payload the proxy
relays, without losing the facts an agent needs, and which combination of them survives
measurement on real payloads?

This is the second half of the compression answer. The first record
(`2026-10-22-local-prompt-compressors.md`) settled the format and the artifacts for one
extractive compressor. This one sweeps three model families, runs each through two agents, and
puts the survivors against each other on four real payloads with verifiable answers.

**Provenance.** The measurements were taken in the sibling project `models-conversion` on the same
M4 machine. The full record with all 20 findings is at
`models-conversion/docs/work/2026-10-22-on-device-compression-models.md`; the scripts, the
downloaded models and the eleven agent notes live outside both repositories, in `~/kompress-work`.

## The answer, as a routing table

| payload | reducer | model or rule | size kept | answers kept | cost |
|---|---|---|---|---|---|
| prose | extractive keep-scores | `kompress-v2-base`, ONNX, 149M | 0.902 | 8/8 | 0.36 s |
| log | signal filter, dedup, must-keep overlay | rules only | 0.75 | 8/8 | ~1 ms |
| JSON | compact serialisation, must-keep overlay | rules only | 0.698 | 13/14 | ~0 ms |
| C# file | extractive keep-scores plus overlay | `kompress-v2-base` | 0.949 | 6/6 | 2.5 s |
| directory listing | pass through | none | 1.0 | 6/6 | 0 s |

The model does the prose and the code. The rules do the logs and the JSON. Nothing compresses a
directory listing without losing file names, and nothing should try.

## The finding that disqualifies the obvious choice

A 77M summarizer is fast, small and useless here. `google/flan-t5-small` loads in 12.3 s
(297 MiB) and answers in 0.41 to 0.78 s on the CPU provider, and on an agent log it returned the
single line "INFO worker=api-3 conn=8812 msg="request failed" status = 503 dur_ms=3110". The
ERROR line with the file path, the line number and the trace id was gone. So was the FATAL line
with the waiters count. Over four payloads with verifiable answers it kept 0 of 8, 0 of 8, 0 of 6
and 1 of 6. On a 7.5 KB C# file it reduced the file to 0.1% of its size and kept none of the
identifiers.

A 0.5B instruct model is no better and 12 times slower. `Qwen/Qwen2.5-0.5B-Instruct` summarises
in 4.0 to 6.4 s per call, reads well ("The engineer encountered a timeout while attempting to
connect to the API endpoint `/v1/orders/99182`"), and still loses the file path, the line number
and the counts.

The 149M extractive compressor has the opposite profile. It never rephrases anything, so what it
keeps is still the client's text, and what it drops can be re-attached verbatim. That is why it
belongs in a proxy: a proxy that mutates a request should be able to put the original back.

## What the three families are, and what they cost on disk

The Hub inventory of 52 candidates puts the three families on one scale (parameters; fp16 GiB at
4 bits).

- **Summarization** runs from 60M (`Falconsai/text_summarization`, 0.03 GiB) to 3.8B
  (`microsoft/Phi-3-mini-4k-instruct`, 1.78 GiB), with `google/flan-t5-small` at 77M (0.04 GiB)
  and `Qwen/Qwen2.5-0.5B-Instruct` at 494M (0.23 GiB) in the useful band.
- **Feature extraction** is the only family with a genuine small end: 22.7M
  (`sentence-transformers/all-MiniLM-L6-v2`, 0.01 GiB), 33.4M (`BAAI/bge-small-en-v1.5`, 0.02),
  47.7M (`ibm-granite/granite-embedding-small-english-r2`, 0.02, 32k context), up to 596M
  (`Qwen/Qwen3-Embedding-0.6B`, 0.28).
- **Typed-decision models** start at 1.5B. `JackKozmo29/codeguard-jev-style-1.5b` is 0.72 GiB
  at 4 bits and 3.4 GB as downloaded; `autotrust/JEV-9B` is 4.17 GiB; `autotrust/JEV-Gemma4-26B-A4B`
  is 26B total with 4B active, 12.02 GiB at 4 bits. A 25x jump from the smallest summarizer that
  no quantisation closes.

## The two model families that did not earn a place

The embedding models answer a question a context reducer does not have. "Which of these chunks is
relevant to the user's question" is not "is this chunk redundant". A 33M embedder put the
relevant payload last (cosines 0.4747 to 0.5679), and the 47.7M granite model ranked better
(0.6409 to 0.7032) but with a top-two gap of 0.053, which is too tight to threshold. Both judge
agents said the same thing independently: a cosine score is a ranking signal, not a drop signal.
The granite runner showed what it is good for: scoring the sixteen lines of a grep result against
a query at 0.05 s a line, with the compression-related lines at the top. That is a ranking layer
for a query-conditioned pipeline, and a prototype stage at that.

The typed-decision model is the most interesting rejection. The 1.5B jev-style model works
mechanically as a chunk judge: with a rewritten system prompt it returned
`{"keep":true,"reason":"context carries file paths and timestamps; drop would lose traceability","score":0.87}`,
valid JSON, at 0.8 s a call. It is also 3.4 GB on disk, 16 times the cost per chunk of the 47.7M
embedder, and out of distribution: it was trained on 61,400 pull requests, and its published 93.8%
F1 is a code-review number, not a relevance number. Its judge wrote "an expensive judge for cheap
token". Its place is a review gate on compressed output, not chunk selection.

The reranker `cross-encoder/ms-marco-MiniLM-L-6-v2` was rejected for a sharper reason. It was
trained on web-search relevance and never saw an agent log, a directory listing or a diff, so
its score on those payloads is lexical overlap with the query and nothing else. The named failure
mode: "ERROR lines score low if they share few query terms". A chunk must be dropped by a domain
filter, never by a relevance score.

## What the agents added

Two agents per model, one running and one judging, plus two that designed the pipeline and built
the decision matrix. The design agent's three decisions, D1 the rule-based log reducer first and
no neural summarizer on logs, D2 the embedding models stay at prototype stage with no drop
threshold, D3 kompress runs on prose and not on logs or code, are the routing table above, and the
matrix agent ranked the same three pipelines in the same order. Its one refusal to trade quality
for size is the one the measurements support: log ERROR and FATAL retention, because "any
pipeline that repeats that failure mode is out regardless of compression".

The matrix agent also named the single measurement that would settle the ranking: kompress on a
JSON payload. That measurement has since been made, and the answer is that kompress keeps a JSON
payload at ratio 1.0 with 13 of 14 answers kept, so it collapses there exactly as it collapses on
logs, and the hybrid stands.

## What this means for the proxy

Five constraints on any compression stage in this project.

**A content router, not a single model.** The reducer is chosen by payload kind, and two of the
four branches use no model at all. That keeps the latency budget at about a millisecond for logs
and JSON, which is most of a tool-output stream.

**The summarizer is out.** Any stage that paraphrases a payload before the provider sees it can
drop the error the agent needed, and no prompt I tested stopped it. If a future stage wants to
summarise, it has to be measured on answer retention first, on payloads with answers.

**Extractiveness is the safety property.** The compressor the proxy should use never rewrites the
client's text, so a dropped identifier can be re-attached and the original can be served back
from a store, the way Headroom's retrieval store does.

**A must-keep overlay is cheap insurance.** Re-attaching the identifiers, numbers, paths and
directive words the extractor dropped cost 0.7% of the payload and lifted must-keep recall on a
C# file from 0.9487 to 1.0. The overlay restores tokens and not phrases, so a name or a pull
request number needs its line kept whole, which is a bigger cost and only worth it when the
reducer dropped a few lines.

**Compression changes the request, and the proxy has to say so.** Chunking flips about 19% of the
keep decisions, the reducer's own measurements are a proxy rather than a downstream answer, and
the proxy should log the reducer, the keep rate and the estimated saving on every request it
changes.

## Still open

- Whether an agent that reads the reduced context answers as well as one that reads the original.
  Answer retention proves the answer string survived; it does not prove the agent still answers
  correctly. That needs a question-answering run with a model that can be trusted with the answer.
- Whether the log reducer's signal list is general. It was tuned against one log shape with three
  workers and one error type, and the failure mode is a dropped root cause on a log format it has
  not seen.
- Whether the prose branch can do better than 0.902 with the facts intact. A larger summarizer
  with a must-keep constraint, or a query-conditioned selection step, might reach 0.5 size with
  the answers kept. Nobody measured that.
- The same sweep on the Intel plus NVIDIA machine, where the 1.5B and 4B models that are out of
  reach on the M4 become reachable, and where the 0.8 s a chunk of the generative judge would
  shrink.

## Method note

The orchestrator's own measurements are in `~/kompress-work`: `hf_inventory.py` (52 models),
`sanity.py` (per-family runs on four payloads), `reducer.py` (the rule-based reducers and the
overlays), `bench_reducers.py` (the 45-row reducer benchmark over five payloads) and
`reducer_benchmark.json`. The eleven agent notes are in `~/kompress-work/notes`. The two agents
whose first attempt failed wrote nothing and were re-dispatched offline; the record says so.
