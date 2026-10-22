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

## Findings

### F1 — The routing table the measurements produced: the model does prose and code, the rules do logs and JSON [MEASURED]

One reducer per payload kind, chosen by measurement rather than by taste. A 77M summarizer is
fast and small and useless on this workload; a 149M extractive compressor never rephrases
anything, so what it drops can be re-attached verbatim; and a rule-based filter is faster than
either model by three orders of magnitude on a log. The table, with the numbers behind it:

| payload | reducer | model or rule | size kept | answers kept | cost |
|---|---|---|---|---|---|
| prose | extractive keep-scores | `kompress-v2-base`, ONNX, 149M | 0.902 | 8/8 | 0.36 s |
| log | signal filter, dedup, must-keep overlay | rules only | 0.75 | 8/8 | ~1 ms |
| JSON | compact serialisation, must-keep overlay | rules only | 0.698 | 13/14 | ~0 ms |
| C# file | extractive keep-scores plus overlay | `kompress-v2-base` | 0.949 | 6/6 | 2.5 s |
| directory listing | pass through | none | 1.0 | 6/6 | 0 s |

Nothing compresses a directory listing without losing file names, and nothing should try.

**Evidence:** `models-conversion` record F12 and F20, measured on the M4 with
`chopratejas/kompress-v2-base` → `onnx/kompress-int8-wo.onnx` on the CPU provider,
`google/flan-t5-small` for the summarizer, and the rule reducers in
`~/kompress-work/reducer.py`; 45 rows over five payloads in `~/kompress-work/reducer_benchmark.json`,
each reducer timed three times, median reported.

### F2 — The 77M summarizer is disqualified, because it drops the root cause [MEASURED]

`google/flan-t5-small` loads in 12.3 s (297 MiB) and answers in 0.41 to 0.78 s on the CPU
provider, and on an agent log it returned the single line `INFO worker=api-3 conn=8812
msg="request failed" status = 503 dur_ms=3110`. The ERROR line with the file path, the line number
and the trace id was gone. So was the FATAL line with the waiters count. Over four payloads with
verifiable answers it kept 0 of 8, 0 of 8, 0 of 6 and 1 of 6. On a 7.5 KB C# file it reduced the
file to 0.1% of its size and kept none of the identifiers.

**Evidence:** `models-conversion` record F2 and F8; `~/kompress-work/sanity.py flan-t5-small` and
`bench_reducers.py` on the M4. The agent judge agreed independently: "The model kept the least
actionable line (a generic 503 failure) and discarded the root cause"
(`~/kompress-work/notes/flan-t5-small-judge.md`).

### F3 — A 0.5B instruct model is no better and 12 times slower [MEASURED]

`Qwen/Qwen2.5-0.5B-Instruct` summarises in 4.0 to 6.4 s per call (953 MiB, 26.4 s to load) and
reads well ("The engineer encountered a timeout while attempting to connect to the API endpoint
`/v1/orders/99182`"), and still loses the file path, the line number and the counts. The same
loss as the 77M model, at 12 times the latency and three times the bytes.

**Evidence:** `models-conversion` record F6; `~/kompress-work/sanity.py qwen-0.5b` on the M4, three
timed runs per payload, medians quoted.

### F4 — The three families do not overlap in size, and the typed-decision family starts 25x higher [READ]

The Hub inventory of 52 candidates puts the families on one scale (parameters; GiB at 4 bits).
Summarization runs from 60M (`Falconsai/text_summarization`, 0.03) to 3.8B
(`microsoft/Phi-3-mini-4k-instruct`, 1.78), with `google/flan-t5-small` at 77M (0.04) and
`Qwen/Qwen2.5-0.5B-Instruct` at 494M (0.23) in the useful band. Feature extraction is the only
family with a genuine small end: 22.7M (`sentence-transformers/all-MiniLM-L6-v2`, 0.01), 33.4M
(`BAAI/bge-small-en-v1.5`, 0.02), 47.7M (`ibm-granite/granite-embedding-small-english-r2`, 0.02,
32k context), up to 596M (`Qwen/Qwen3-Embedding-0.6B`, 0.28). Typed-decision models start at 1.5B:
`JackKozmo29/codeguard-jev-style-1.5b` is 0.72 GiB at 4 bits and 3.4 GB as downloaded,
`autotrust/JEV-9B` is 4.17 GiB, `autotrust/JEV-Gemma4-26B-A4B` is 26B total with 4B active at
12.02 GiB.

**Evidence:** `models-conversion` record F1, computed from the Hub's own `safetensors.total`
parameter counts for each of the 52 models, saved in `~/kompress-work/hf_inventory.json`; the
agent's own `du -sk` of the 1.5B model's cache directory, 3.4 GB, in
`~/kompress-work/notes/jev-codeguard-runner.md`.

### F5 — The embedding models are a ranking layer, and their scores are too packed to gate on [MEASURED]

A 33M embedder put the relevant payload last (cosines 0.4747 to 0.5679); the 47.7M granite model
ranked better (0.6409 to 0.7032) but with a top-two gap of 0.053. Both judge agents said the
same thing independently: a cosine score is a ranking signal, not a drop signal. What granite is
good for showed up in its runner note: scoring the sixteen lines of a grep result against a
query at 0.05 s a line, with the compression-related lines at the top. That is a ranking layer
for a query-conditioned pipeline, and a prototype stage at that.

**Evidence:** `models-conversion` record F4, F5, F17; `~/kompress-work/sanity.py bge-small` and
`sanity.py granite-small` on the M4; `~/kompress-work/notes/granite-small-runner.md` for the
per-line scores and the 0.05 s a chunk; `~/kompress-work/notes/granite-small-judge.md` for "a hard
keep/drop threshold is a different claim from ranking".

### F6 — The typed-decision model works mechanically as a chunk judge, and is still the wrong tool [READ]

The 1.5B jev-style model, with a rewritten system prompt, returned
`{"keep":true,"reason":"context carries file paths and timestamps; drop would lose traceability","score":0.87}`,
valid JSON, at 0.8 s a call on a busy machine. It is also 3.4 GB on disk, 16 times the cost per
chunk of the 47.7M embedder, and out of distribution: it was trained on 61,400 pull requests and
its published 93.8% F1 is a code-review number, not a relevance number. Its judge wrote "an
expensive judge for cheap token" and put it back where it belongs, a review gate on compressed
output.

**Evidence:** `~/kompress-work/notes/jev-codeguard-runner.md` and
`~/kompress-work/notes/jev-codeguard-judge.md`; card
`JackKozmo29/codeguard-jev-style-1.5b` → `README.md` for the training set and the benchmark.

### F7 — The reranker is out of domain, and its failure mode is named [READ]

`cross-encoder/ms-marco-MiniLM-L-6-v2` was trained on web-search relevance and never saw an
agent log, a directory listing or a diff, so its score on those payloads is lexical overlap with
the query and nothing else. The judge named the consequence: "ERROR lines score low if they share
few query terms". A chunk must be dropped by a domain filter, never by a relevance score. Its
runner measured a whole-payload logit of 1.46 (sigmoid 0.81) for a grep payload against a matched
query and negative logits for the same payload against an unrelated one, which is the separation
it can offer and the limit of it.

**Evidence:** `~/kompress-work/notes/ms-marco-minilm-judge.md` and
`~/kompress-work/notes/ms-marco-minilm-runner.md`; card
`cross-encoder/ms-marco-MiniLM-L-6-v2` → `README.md` and `config.json` for the training domain
and the 512-token limit.

### F8 — Two independent agents designed the same pipeline the measurements produced [READ]

The design agent's three decisions, D1 the rule-based log reducer first and no neural summarizer
on logs, D2 the embedding models stay at prototype stage with no drop threshold, D3 kompress runs
on prose and not on logs or code, are the routing table in F1. The matrix agent ranked the same
three pipelines in the same order and refused one trade: log ERROR and FATAL retention, "because
any pipeline that repeats that failure mode is out regardless of compression". Its one named
measurement, kompress on a JSON payload, has since been made and settled the ranking in the
branch it predicted (F20 of the source record).

**Evidence:** `~/kompress-work/notes/pipeline-design-architect.md` and
`~/kompress-work/notes/decision-matrix-delegator.md`; `models-conversion` record F18, F19, F20.

### F9 — The two agents whose first attempt failed wrote nothing, and the record says so [READ]

The first architect and delegator attempts ended without producing a file: the architect on a
provider billing-exhaustion router fallback, the delegator mid-turn after concluding that some
models were not in its inventory. Both were re-dispatched offline with the measurements inlined,
and the second attempts produced the design and the matrix. An agent's silence is not a finding,
and a sweep that half failed is part of the result.

**Evidence:** the delegation logs in
`~/.pi/agent/subagent-logs/projects/ba18c794-bc10-488d-8cca-2e1117eec0ba/d-1.jsonl` and
`d-2.jsonl` (zero write calls in both), against the notes that exist in
`~/kompress-work/notes/`; `models-conversion` record F13.

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
