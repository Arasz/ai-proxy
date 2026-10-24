# Research: one score per reducer candidate per payload kind

**Date:** 2026-10-24
**Question:** On the same pre-registered payloads and questions, which reducer keeps every answer,
how much does each remove, and how long does it take?

This is task-graph step `scoring-harness` of `aip-compression-and-rtk-replacement`. The harness,
the payloads, the 84 questions and every raw run are in
[scripts/reducer_scoring/](../../scripts/reducer_scoring/). The rules were committed in
`preregistration.json` before any run, and a second amendment was made after the first full run
of all six candidates; nothing already scored was re-scored.

**Machine:** Apple M4, 24 GB, macOS 27.0.1. **rtk-proxy** 1.0.1, **pi** 1.4.0. Five candidates
ran at commit `0d7d19b`. rtk was re-run at `1820d4a` after the code review found its injected
tool definition was not counted (F6). The Haiku digest ran at `a7fe033`, after the second
amendment. `results/environment.json` records this per candidate.
Each candidate ran three times on each payload. Each number is a median over the three runs,
taken field by field, so a row's length, latency and answers can come from different runs.

## How it was scored

- **Score** is the harmonic mean of compression and `budget / (budget + latency)`, and is 0
  when any answer is lost (`scripts/compression_score.py`, floor 1.0, budget 1 s).
- **Extractive candidates** (shipped rules, kompress int8, rtk CCR) keep an answer only if it
  still appears word for word in their output. Their length counts every character the model
  would see, including anything rtk adds to the request outside the tool result.
- **Abstractive candidates** (identity, digest, summarizer control) are judged by a QA reader
  that answers every question three times. A question is lost when two of the three replies
  miss it.
- **n/a** is the pre-registered rule: a candidate whose output equals its input byte for byte
  is reported as n/a, on the reasoning that it was under its minimum. For the shipped rules
  that reasoning is mostly wrong (F4). So each kind is reported twice: the pre-registered view
  leaves untouched payloads out, and the counted view measures them as nothing removed.
- **Kinds pool their payloads.** Characters and answers are summed, and latency is the median
  of the payload medians. The pre-registration did not specify pooling; this choice was made
  after registration and before any run.
- **The reader is not the pre-registered one.** No working Anthropic key was available. The
  reader, digest and summarizer ran on `openai/gpt-6.1-sol` through pi, which is the only model
  the owner's ChatGPT plan accepts. The digest row is therefore a **stand-in**: its retention
  and compression are real for that model, but its latency is not Haiku's. The second
  amendment added the real Haiku digest (`claude-haiku-5-5` through `claude -p`, timed by the
  model's own `duration_api_ms`). Its reader is still `gpt-6.1-sol`. That run pinned no
  effort and inherited this session's `CLAUDE_EFFORT=medium`, not the pre-registered thinking
  off. The third amendment defines `haiku_digest_checked` (a find-compress-check prompt at
  `--effort low`), but its run was stopped by the owner after 9 of 39 digests and is not scored.
- **The relaxed view** (fourth amendment, after every result above was seen) is the owner's
  ruling that a small loss is acceptable and a fact in other words is kept. It grades by fact
  (`grading.same_fact`): `4.2s` counts for `4.2 seconds`, `7 failed` for `failed: 7`,
  `224 passed` for `succeeded: 224`, `2 errors` for `2 Error(s)`. Across all 3,780 graded
  answers this keeps every answer the word-for-word check kept and adds 31. A kind then scores
  0 under 70% retention, and above it the score is multiplied by retention. The recorded
  replies were re-graded; nothing was re-run.
- **The judge** (fifth amendment, registered before it ran) handles what the spelling rules
  cannot. For every answer still lost, the reader sees the candidate's output, the question and
  the expected answer, and says yes or no three times; two yes keep the answer, in the relaxed
  view only. Each numeric answer is also asked with one number changed, as a decoy.
- **Per-kind scores are provisional.** The owner has since ruled that scores are normalised to
  payload size and a kind is the mean of its payloads (plan
  `aip-reducer-fixes-from-scoring-results`, step `size-normalized-score`). Until that re-run,
  the per-kind tables pool characters as described above.

## Per kind, at the 1 s budget

A cell is the score, then (compression, latency) when every answer survived, or (answers kept)
when one was lost. Where the two views differ, the counted view is in brackets. Scores at
0.5 s and 2 s are in `results/report.md`. They change no ranking.

| kind | shipped rules | kompress int8 | rtk CCR | Haiku digest | digest (stand-in) | identity | summarizer |
|---|---|---|---|---|---|---|---|
| code | **0.30** (18%, 2 ms) | 0.09 (5%, 3.2 s) | 0 (9/12) | 0 (10/12) | 0 (11/12) | 0 (0%) | 0 (2/12) |
| prose | n/a [0, 0%] | **0.17** (12%, 2.5 s) | 0 (7/14) | 0 (13/14) | 0.07 (31%, 26 s) | 0 (0%) | 0 (2/14) |
| log | 0 (9/13) | 0.002 (0.1%, 1.4 s) | 0 (8/13) | 0 (12/13) | **0.12** (67%, 14 s) | 0 (0%) | 0 (2/13) |
| listing | n/a [0, 0%] | n/a [0, 0%] | n/a [0, 0%] | 0 (5/6) | 0 (5/6) | 0 (0%) | 0 (1/6) |
| json | **0.46** (30%) [0.08, 4%] | 0.09 (5%, 1.6 s) [0.08] | 0 (0/19) | 0.27 (37%, 3.7 s) | 0.09 (32%, 18 s) | 0 (0%) | 0 (2/19) |
| build | **0.41** (26%) [0.008, 0.4%] | 0 (5/6) [0 (9/10)] | 0 (2/6) [0 (6/10)] | 0 (7/10) | 0 (9/10) | 0 (0%) | 0 (5/10) |
| test | **0.40** (25%) [0.34, 21%] | 0 (7/10) | 0 (6/10) | 0 (7/10) | 0 (8/10) | 0 (0%) | 0 (5/10) |

The shipped rules ran in under 3 ms on every kind.

## Per kind, relaxed view

Graded by fact, then by the judge; floor 0.7; score multiplied by retention; 1 s budget. A cell
is the score, then (answers kept) when one was lost. The counted view is in brackets where it
differs.

| kind | shipped rules | kompress int8 | rtk CCR | Haiku digest | digest (stand-in) | identity | summarizer |
|---|---|---|---|---|---|---|---|
| code | **0.30** | 0.09 | **0.61** (9/12) | 0.14 (11/12) | 0.09 (11/12) | 0 | 0 (3/12) |
| prose | n/a [0] | **0.17** | 0 (7/14) | 0.15 | 0.07 | 0 | 0 (2/14) |
| log | 0 (9/13) | 0.002 | 0 (8/13) | **0.27** | 0.12 | 0 | 0 (3/13) |
| listing | n/a [0] | n/a [0] | n/a [0] | **0.31** (5/6) | 0.24 (5/6) | 0 | 0 (1/6) |
| json | **0.46** [0.08] | 0.09 [0.08] | 0 (0/19) | 0.27 **[0.27]** | 0.09 | 0 | 0 (3/19) |
| build | 0.41 [0.008] | 0.006 [0.007] | 0 (2/6) [0 (6/10)] | **0.42** (8/10) | 0.20 (9/10) | 0 | 0.21 (7/10) |
| test | **0.40** [0.34] | 0.07 (8/10) | 0 (6/10) | 0.33 (9/10) | 0.13 | 0 | 0.21 (7/10) |

## Findings

### F1: The controls clear the run [MEASURED]

Identity kept every answer on every kind, so each of the 84 questions can be answered from its
own payload by the reader. Identity also scores 0 everywhere, because it removes nothing. The
summarizer control failed the retention gate on 12 of 13 payloads. The exception is the small
test failure (`dotnet_test_failure`): its five answers fit in a two-sentence summary. One of 13
is well under the "more than half" void rule. Neither void rule fired. All 468 reader replies,
including the 117 for the Haiku digest, parsed as an answer array, so no answer was lost to formatting. Every candidate has exactly
three runs of every payload, and the rules runner recorded no reducer failure.

### F2: The shipped rules lead on code and test output; json and build depend on the view [MEASURED]

On code and test output the shipped rules keep every answer and score 0.30 and 0.40 (0.34 when
the untouched test payload is counted), removing about 20% in under 3 ms. In the
pre-registered view they also lead json (0.46) and build output (0.41). Those two leads come
from leaving out a payload the rules ran on and did not change: the 25-item `github_pr_list`
JSON and the 60 KB verbose build log. Counting them, json drops to 0.08, behind the digest
stand-in's 0.09 and the Haiku digest's 0.27, and build drops to 0.008. Latency barely counts at these speeds, so the
score is almost all compression.

### F3: The log reducer loses 4 of 5 answers on rtk-proxy's own log [MEASURED]

On `rtk_proxy_log` the rules removed 89% and kept one answer. The output is four of the log's
lines followed by `[retained identifiers] /home/dev/probe/db-a/mem.db -proxy 18080`. The lines
holding `127.0.0.1:18080`, `pixel_compress`, `2443` and `1031` are gone, and the identifier
summary does not bring them back. On `agent_log`, the other log, the rules kept all 8 answers.
Why this log is cut so hard is not established here. The payload and its questions now make a
ready regression case for `LogCompressionHandler`.

### F4: The shipped rules leave 6 of 13 payloads untouched, and 5 are over their minimum [MEASURED]

Both prose payloads, the directory listing, the one-line `github_pr_list` JSON, the 60 KB
verbose build log and the small test failure came back byte for byte. Only the listing is
under its minimum: `CompressionOptions.cs` gives listings `int.MaxValue`. The other five are
far over theirs (prose 512, json 100, shell 200). On those, the rules ran and found nothing to
remove. On the verbose build log that took a median 5.5 ms. So the rules have no score at
all on listings, and score 0 on prose once the untouched payloads count.

### F5: kompress int8 keeps answers but removes little, and it cuts identifiers [MEASURED]

Where it kept every answer, kompress removed 0.05-12.6% and took 0.2-4.9 s. On the 60 KB
verbose build log it took 69 s per run for 0.4%. It lost answers on build and test output by
dropping subword tokens inside identifiers. For example, the test name
`...Tests.The_last_two_lines_are_anchors_and_survive` came out as
`...Tests._last_two_lines_are_anchors_and_survive`. Its best per-kind score is 0.17, on prose.
Its best per-payload score is 0.18, on `incident_report`.

### F6: rtk's preview loses answers on every kind, by design [MEASURED]

rtk adds a 426-character `rtk_retrieve` tool definition to every request, including the two
whose tool result it left untouched. Counting those characters, the preview removed 4-97% in
at most 6 ms. It lost answers on 9 of the 11 payloads whose tool result it changed, including
0 of 19 on JSON. The two it kept whole were `code_file` (54% removed) and `rtk_proxy_log`
(7%). Each shares its kind with a payload that lost answers, so no kind passes. For JSON the
preview is not head+tail. A 25-item array becomes one schema line,
`[json array, 25 items] [25× {additions: 250, …}]`, and a nested object becomes
`[… 4 lines elided …]` stubs. Each preview ends with a marker telling the model to call
`rtk_retrieve` for the rest. This harness scores the preview alone, as pre-registered, so it
does not credit what retrieval would get back. Here a 0 means the agent must call retrieval to
answer, not that the content is gone.

### F7: The digest stand-in keeps prose, logs and JSON, but not code or build output [MEASURED, stand-in model]

On `gpt-6.1-sol` the digest kept every answer on prose, logs and JSON, removing 31-67%. It
dropped `System.Text.Json` from the code file, a file name from the listing, and the
`(18,83)` error position from the verbose build log. Its latency, 4-26 s per kind and up to
42 s on `prose_large`, holds its best score to 0.12 at 1 s and 0.21 at 2 s. Haiku would be
faster, so its latency here says nothing about Haiku. F8 has the Haiku run.

### F8: The Haiku digest is fast; word for word it loses answers on 6 of 7 kinds, mostly by rewording [MEASURED]

On `claude-haiku-5-5` the digest took 3.0-14 s of wall time per payload (3.1-12 s per kind), 1.2
to 3.6 times faster than the stand-in per kind. Wall time is the boundary `pi_call` times for the
stand-in. The scores in the tables use the model's own API duration (`duration_api_ms`), which is
1.6-13 s per payload (1.8-11 s per kind) and 1.7 to 4.7 times faster than the stand-in; so
the Haiku latencies in the tables are the API-duration figures, not like-for-like with the
stand-in. It kept every answer only on json, removing 37% in 3.7 s API time (4.9 s wall), for
a score of 0.27 at 1 s and 0.36 at 2 s. That is the best json score in the counted view. On
every other kind the word-for-word check lost one to three answers. Seven of 13 payloads kept
every answer, with per-payload scores of 0.12-0.47.

The reader's misses fall into three groups:

- **Reworded, fact kept.** The digest wrote `p99 4.2s` and `7 failed, 224 passed`; the reader
  copied those, and `4.2 seconds`, `failed: 7` and `succeeded: 224` were marked lost. The prompt
  asked for facts "exactly as written", so Haiku did not follow it here.
- **Dropped.** `System.Text.Json` (the first using directive), `00:00:00.42` (elapsed build
  time) and `512ms` (one test's duration) are not in the digest. These are low-value facts.
- **Kept, but the reader could not use it.** The digest kept `ProviderHostCatalog.cs(18,83)`,
  but flattened the build log so the reader answered `(33,16)`, the other file's position,
  for "the error in the second file". For `2 Error(s)` the digest wrote `2 errors` and the
  reader replied `UNKNOWN`.

Graded by fact and judged, Haiku keeps every answer on prose, log and json, and loses at most
two per kind elsewhere. Under the relaxed view it scores 0.14-0.42 on every kind. It leads log,
listing and build, and in the counted view json.

### F9: The judge is strict on numbers; the relaxed view lets the summarizer control score on build and test [MEASURED]

The judge ran over the 383 answers fact grading still called lost, three times each, and every
one of its 294 replies parsed. It accepted none of the 222 decoys, so a changed number is not
waved through. It accepted 39 real answers: 13 of 26 for Haiku, 23 of 199 for the summarizer,
2 of 9 for kompress, 1 of 11 for the stand-in, and none of rtk's 126 or the shipped rules' 12.
Most are the fact in other words: `2 errors`, `7 failed, 224 passed`, `HTTP 503`,
`pool.py:214`, `finished in 0.42 seconds`, `line 33, column 16`. A few are lenient. `unknown` was
accepted for `Dialect.Unknown`. The summarizer got `failed: 7` once by adding up "six … and
one", and got `5000` ms with no 5000 in the text. Kompress's `second last line` was accepted
for the substring `the second to last line`, which is wrong.

The relaxed view changes no lossless result. It lets lossy candidates score. rtk's preview
keeps 9 of 12 code answers (all 6 on `code_file`, 3 of 6 on `code_file_large`) and removes
54-78%, so it scores 0.61 on code, the highest code score in any view. The shipped rules keep
9 of 13 log answers (69%, F3), just under the floor, so log stays 0. And the summarizer
control, built to fail, scores 0.21 on build and test at 7 of 10. Per payload it passes only 2
of 13 (`dotnet_test_failure` and `dotnet_build_failure_verbose`), so the "more than half" void
rule does not fire. Pooling two payloads into one kind is what lifts it over the floor. On
build and test, a relaxed score is not evidence that a candidate beats a two-sentence summary.

### F10: What this does not cover [UNVERIFIED]

- One machine, one reader model, and 13 payloads, with at least two per kind except listings
  (one).
- Kind pooling weights large payloads more than small ones. Averaging per payload instead could
  reorder kinds where a candidate's compression differs a lot between payloads.
- The word-for-word check credits a short answer (`32`, `214`, `dhl`) wherever it appears in
  the output. It cannot check relations: "which test class has the most failures" is graded on
  presence alone.
- That ai-proxy saw no request during the pi runs was not checked positively. The runner clears
  the proxy variables and sets `NO_PROXY=*`, and pi's config sets no base URL.
- A pi error that exits 0 and does not start with one of the three known error prefixes would
  be scored as output. Every raw reply and output in this run was checked and is real content.
- The Haiku digest was graded by the `gpt-6.1-sol` reader, not a Haiku reader, and ran at an
  inherited effort, not a pinned one.
- Fact grading covers the spellings seen in this run (units, label-first counts, passed and
  succeeded, Error(s), positions). The judge covers the rest but is a model: its errors are
  listed in F9 and were not corrected by hand.
- The 0.7 floor and the retention multiplier were chosen after the results were seen.
- The follow-ups are retrieval-aware scoring for rtk and a fix for F3.

## Reviews

A code review and a test review (opus, separate worktrees) read the harness at `e080f7a`.
The record above incorporates what they found:

- the counted view, because n/a hid payloads the rules ran on;
- rtk's tool definition counted, and rtk re-run;
- a void when a run is missing or a reducer threw;
- a run environment kept per candidate;
- three figures corrected: the 5.5 ms latency on the verbose build log, the per-kind vs
  per-payload kompress best, and the cell legend.

The test review's surviving mutants are now killed by tests. The ones that would have changed
the report were these:

- grading branch;
- runs read;
- kind grouping;
- headline budget;
- score formula;
- kind pooling;
- median selection.
