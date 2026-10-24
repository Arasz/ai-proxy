# Research: one score per reducer candidate per payload kind

**Date:** 2026-10-24
**Question:** On the same pre-registered payloads and questions, which reducer keeps every answer,
how much does each remove, and how long does it take?

This is task-graph step `scoring-harness` of `aip-compression-and-rtk-replacement`. The harness,
the payloads, the 84 questions and every raw run are in
[scripts/reducer_scoring/](../../scripts/reducer_scoring/). The rules were committed in
`preregistration.json` before any run. The one change made after that, also before any run, is
the dated amendment in the same file.

**Machine:** Apple M4, 24 GB, macOS 27.0.1. **Commit:** `0d7d19b`. **rtk-proxy** 1.0.1,
**pi** 1.4.0. Three runs per candidate per payload; every number is the median run.

## How it was scored

- **Score** is the harmonic mean of compression and `budget / (budget + latency)`, and is 0
  when any answer is lost (`scripts/compression_score.py`, floor 1.0, budget 1 s).
- **Extractive candidates** (shipped rules, kompress int8, rtk CCR) keep an answer only if it
  still appears word for word in their output.
- **Abstractive candidates** (identity, digest, summarizer control) are judged by a QA reader
  that answers every question three times. A question is lost when two of the three replies
  miss it.
- **n/a** means the candidate's output equalled its input byte for byte, so it did not run on
  that payload.
- **The reader is not the pre-registered one.** No working Anthropic key was available. The
  reader, digest and summarizer ran on `openai/gpt-6.1-sol` through pi, which is the only model
  the owner's ChatGPT plan accepts. The digest row is therefore a **stand-in**: its retention
  and compression are real for that model, but its latency is not Haiku's, and it does not
  settle `digest-verdict`.

## Per kind, at the 1 s budget

| kind | shipped rules | kompress int8 | rtk CCR | digest (stand-in) | identity | summarizer |
|---|---|---|---|---|---|---|
| code | **0.30** (18%, 2 ms) | 0.09 (5%, 3.2 s) | 0 (9/12) | 0 (11/12) | 0 | 0 (2/12) |
| prose | n/a | **0.17** (12%, 2.5 s) | 0 (7/14) | 0.07 (31%, 26 s) | 0 | 0 (2/14) |
| log | 0 (9/13) | 0.002 (0.1%, 1.4 s) | 0 (8/13) | **0.12** (67%, 14 s) | 0 | 0 |
| listing | n/a | n/a | n/a | 0 (5/6) | 0 | 0 |
| json | **0.46** (30%, 0.2 ms) | 0.09 (5%, 1.6 s) | 0 (0/19) | 0.09 (32%, 18 s) | 0 | 0 |
| build | **0.41** (26%, 0.3 ms) | 0 (5/6) | 0 (2/6) | 0 (9/10) | 0 | 0 |
| test | **0.40** (25%, 0.9 ms) | 0 (7/10) | 0 (6/10) | 0 (8/10) | 0 | 0 |

A cell is the score, then (compression, latency) when every answer survived, or (answers kept)
when one was lost. Scores at 0.5 s and 2 s are in `results/report.md`. They change no ranking.
Per kind, payloads are pooled: characters and answers are summed and latency is the median.

## Findings

### F1 — The controls clear the run [MEASURED]

Identity kept every answer on every kind, so each of the 84 questions can be answered from its
own payload by the reader. Identity also scores 0 everywhere, because it removes nothing. The
summarizer control failed the retention gate on 12 of 13 payloads. The exception is the small
test failure (`dotnet_test_failure`): its five answers fit in a two-sentence summary. One of 13
is well under the "more than half" void rule. Neither void rule fired. All 351 reader replies parsed as an answer array, so no answer
was lost to formatting.

### F2 — The shipped rules win every kind they touch except logs [MEASURED]

On code, json, build output and test output the rules keep every answer and score 0.30-0.46,
removing 15-30% in under 3 ms. Nothing else scores above 0.17 on any kind. Latency barely
counts at these speeds, so the score is almost all compression.

### F3 — The log reducer loses 4 of 5 answers on rtk-proxy's own log [MEASURED]

On `rtk_proxy_log` the rules removed 89% and kept one answer. The output is four of the log's
lines followed by `[retained identifiers] /home/dev/probe/db-a/mem.db -proxy 18080`. The lines
holding `127.0.0.1:18080`, `pixel_compress`, `2443` and `1031` are gone, and the identifier
summary does not bring them back. On `agent_log`, the other log, the rules kept all 8 answers.
Why this log is cut so hard is not established here. The payload and its questions now make a
ready regression case for `LogCompressionHandler`.

### F4 — The shipped rules leave 6 of 13 payloads untouched [MEASURED]

Both prose payloads, the directory listing, the one-line `github_pr_list` JSON, the 60 KB
verbose build log and the small test failure came back byte for byte. So the rules have no
score on prose or listings. On the verbose build log they spent 11 ms and changed nothing.

### F5 — kompress int8 keeps answers but removes little, and it cuts identifiers [MEASURED]

Where it kept every answer, kompress removed 0.05-12.6% and took 0.2-4.9 s. On the 60 KB
verbose build log it took 69 s per run for 0.4%. It lost answers on build and test output by
dropping subword tokens inside identifiers. For example, the test name
`...Tests.The_last_two_lines_are_anchors_and_survive` came out as
`...Tests._last_two_lines_are_anchors_and_survive`. Its best score anywhere is 0.17, on prose.

### F6 — rtk's preview loses answers on every kind, by design [MEASURED]

The CCR preview removed 14-98% in at most 5 ms and lost answers on 9 of the 11 payloads it
touched, including 0 of 19 on JSON. The two it kept whole were `code_file` (60% removed) and
`rtk_proxy_log` (14%), but each shares its kind with a payload that lost answers, so no kind
passes. For JSON the preview is not head+tail. A 25-item array becomes one
schema line, `[json array, 25 items] [25× {additions: 250, …}]`, and a nested object becomes
`[… 4 lines elided …]` stubs. Each preview ends with a marker telling the model to call
`rtk_retrieve` for the rest. This harness scores the preview alone, as pre-registered, so it
does not credit what retrieval would get back. The 0 here means "the agent must call
retrieval to answer", not "the content is gone".

### F7 — The digest stand-in keeps prose, logs and JSON, but not code or build output [MEASURED, stand-in model]

On `gpt-6.1-sol` the digest kept every answer on prose, logs and JSON, removing 31-67%. It
dropped `System.Text.Json` from the code file, a file name from the listing, and the
`(18,83)` error position from the verbose build log. Its latency, 4-26 s per kind and up to
42 s on `prose_large`, holds its best score to 0.12 at 1 s and 0.21 at 2 s. Haiku would be faster, so its latency here says nothing
about Haiku. Settling `digest-verdict` needs one Haiku run of `pi_runner.py haiku_digest`.

### F8 — What this does not cover [UNVERIFIED]

One machine, one reader model, and 13 payloads with at least two per kind (one listing). The
pooled kind row weights large payloads more than small ones. Averaging per payload instead
could reorder kinds where a candidate's compression differs a lot between payloads.
Retrieval-aware scoring for rtk, a Haiku digest run and a fix for F3 are the follow-ups.
