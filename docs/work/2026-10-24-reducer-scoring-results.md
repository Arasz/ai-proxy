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
  `--effort low`, the session's effort cleared). It ran at `e4b2b82` (F8).
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

### F1 — The controls clear the run [MEASURED]

Identity kept every answer on every kind, so each of the 84 questions can be answered from its
own payload by the reader. Identity also scores 0 everywhere, because it removes nothing. The
summarizer control failed the retention gate on 12 of 13 payloads. The exception is the small
test failure (`dotnet_test_failure`): its five answers fit in a two-sentence summary. One of 13
is well under the "more than half" void rule. Neither void rule fired. All 468 reader replies,
including the 117 for the Haiku digest, parsed as an answer array, so no answer was lost to formatting. Every candidate has exactly
three runs of every payload, and the rules runner recorded no reducer failure.

**Evidence:** `python3 scripts/reducer_scoring/harness.py report`: `void` is empty in `results/report.json`; identity and summarizer rows from `results/identity.jsonl` and `results/summarizer_control.jsonl`.

### F2 — The shipped rules lead on code and test output; json and build depend on the view [MEASURED]

On code and test output the shipped rules keep every answer and score 0.30 and 0.40 (0.34 when
the untouched test payload is counted), removing about 20% in under 3 ms. In the
pre-registered view they also lead json (0.46) and build output (0.41). Those two leads come
from leaving out a payload the rules ran on and did not change: the 25-item `github_pr_list`
JSON and the 60 KB verbose build log. Counting them, json drops to 0.08, behind the digest
stand-in's 0.09 and the Haiku digest's 0.27, and build drops to 0.008. Latency barely counts at these speeds, so the
score is almost all compression.

**Evidence:** `python3 scripts/reducer_scoring/harness.py report`: `shipped_rules` rows under `kinds` and `kinds_counted` in `results/report.json`, from `results/shipped_rules.jsonl`.

### F3 — The log reducer loses 4 of 5 answers on rtk-proxy's own log [MEASURED]

On `rtk_proxy_log` the rules removed 89% and kept one answer. The output is four of the log's
lines followed by `[retained identifiers] /home/dev/probe/db-a/mem.db -proxy 18080`. The lines
holding `127.0.0.1:18080`, `pixel_compress`, `2443` and `1031` are gone, and the identifier
summary does not bring them back. On `agent_log`, the other log, the rules kept all 8 answers.
Why this log is cut so hard is not established here. The payload and its questions now make a
ready regression case for `LogCompressionHandler`.

**Evidence:** the `rtk_proxy_log` outputs in `results/shipped_rules.jsonl` and its `payloads` row in `results/report.json`.

### F4 — The shipped rules leave 6 of 13 payloads untouched, and 5 are over their minimum [MEASURED]

Both prose payloads, the directory listing, the one-line `github_pr_list` JSON, the 60 KB
verbose build log and the small test failure came back byte for byte. Only the listing is
under its minimum: `CompressionOptions.cs` gives listings `int.MaxValue`. The other five are
far over theirs (prose 512, json 100, shell 200). On those, the rules ran and found nothing to
remove. On the verbose build log that took a median 5.5 ms. So the rules have no score at
all on listings, and score 0 on prose once the untouched payloads count.

**Evidence:** `not_applicable` rows for `shipped_rules` under `payloads` in `results/report.json`, from `results/shipped_rules.jsonl`; listing minimum read in `CompressionOptions.cs`.

### F5 — kompress int8 keeps answers but removes little, and it cuts identifiers [MEASURED]

Where it kept every answer, kompress removed 0.05-12.6% and took 0.2-4.9 s. On the 60 KB
verbose build log it took 69 s per run for 0.4%. It lost answers on build and test output by
dropping subword tokens inside identifiers. For example, the test name
`...Tests.The_last_two_lines_are_anchors_and_survive` came out as
`...Tests._last_two_lines_are_anchors_and_survive`. Its best per-kind score is 0.17, on prose.
Its best per-payload score is 0.18, on `incident_report`.

**Evidence:** `python3 scripts/reducer_scoring/harness.py report`: `kompress_int8` rows in `results/report.json`, outputs and latencies in `results/kompress_int8.jsonl`.

### F6 — rtk's preview loses answers on every kind, by design [MEASURED]

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

**Evidence:** `python3 scripts/reducer_scoring/harness.py report`: `rtk_ccr` rows in `results/report.json`; previews and `added_chars` in `results/rtk_ccr.jsonl`.

### F7 — The digest stand-in keeps prose, logs and JSON, but not code or build output on the stand-in model [MEASURED]

On `gpt-6.1-sol` the digest kept every answer on prose, logs and JSON, removing 31-67%. It
dropped `System.Text.Json` from the code file, a file name from the listing, and the
`(18,83)` error position from the verbose build log. Its latency, 4-26 s per kind and up to
42 s on `prose_large`, holds its best score to 0.12 at 1 s and 0.21 at 2 s. Haiku would be
faster, so its latency here says nothing about Haiku. F8 has the Haiku run.

**Evidence:** `python3 scripts/reducer_scoring/harness.py report`: `digest_gpt_stand_in` rows in `results/report.json`, digests and reader replies in `results/digest_gpt_stand_in.jsonl`.

### F8 — The Haiku digest is fast; word for word it loses answers on 6 of 7 kinds, mostly by rewording [MEASURED]

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

**Evidence:** `python3 scripts/reducer_scoring/harness.py run haiku_digest`, then report: `haiku_digest` rows in `results/report.json`; digests in `results/haiku_digest.digests.ndjson`, reader replies in `results/haiku_digest.jsonl`.

### F8a — Asking Haiku to find and re-check the facts first keeps no more of them [MEASURED]

`haiku_digest_checked` gave Haiku the find-compress-check prompt at `--effort low`. All 39
digests came from Haiku at low effort. It thought for a median 239 tokens (0-2,501). Compared
with the first Haiku run, word for word it keeps the same answers on every kind except log,
where it drops one more (11/13). Seven of 13 payloads again keep every answer, and json is again
the only kind with a nonzero headline score (0.26 vs 0.27). The answers lost in all three runs
are the same ones: `System.Text.Json`, `4.2 seconds` still written `4.2s`, `(18,83)`,
`2 Error(s)`, `00:00:00.42`, `failed: 7`, `succeeded: 224`. It removes 2-12 points more on
code, prose, log, listing and json and the same on build and test, in 2.0-8.6 s per kind
(first run 1.8-11.1 s). Under the relaxed view it scores 0.17-0.40, slightly higher than the
first run on code, prose, log and listing and slightly lower on json, build and test. The judge accepted
11 of its 28 remaining losses and none of its 17 decoys. The prompt's check step does not
change what Haiku drops or rewords.

**Evidence:** `python3 scripts/reducer_scoring/harness.py run haiku_digest_checked`, then report: `haiku_digest_checked` rows in `results/report.json`; effort and thinking tokens in `results/haiku_digest_checked.digests.ndjson`.

### F9 — The judge is strict on numbers; the relaxed view lets the summarizer control score on build and test [MEASURED]

Over the first six candidates, the judge ran over the 383 answers fact grading still called lost, three times each, and every
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

**Evidence:** `python3 scripts/reducer_scoring/judge_runner.py scripts/reducer_scoring/results`, then report: verdicts in `results/judge.ndjson`, the `judge` check and `score_relaxed` rows in `results/report.json`.

## Still open

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

## Amendment 2026-10-24: sizes in tokens

The harness now reports sizes in estimated tokens, not characters: `original_tokens`,
`reduced_tokens` and the tokens a candidate adds outside its output (rtk's injected tool
definition), and compression is computed from those. Saved rtk records that hold `added_chars` are
converted with the same estimate. No candidate was re-run; `results/report.json` was regenerated
from the saved runs with `python3 scripts/reducer_scoring/harness.py report`. Latency, the budget
and the latency score are unchanged. The character figures above stay as the historical record.

**Method label** (`token_method` in `results/report.json`): estimated: 2.8916 chars/token from
116 stateless claude requests (claude-sonnet-5 76, claude-haiku-5-5 29, claude-opus-5-5 6,
claude-sonnet-5-5 3, claude-haiku-4-5-20251001 2; 2026-10-24), holdout median error 2.2%. The
calibration is in [2026-10-24-claude-token-calibration.md](2026-10-24-claude-token-calibration.md);
applying it to opus-5-5 is unverified there. Tokens here are an estimate, not a provider count.

Original and reduced tokens per kind, original to reduced, from `results/report.json`. The
pre-registered view leaves untouched payloads out, so "n/a" marks a kind with no changed payload;
the counted view measures them as nothing removed.

| candidate, view | code | prose | log | listing | json | build | test |
|---|---|---|---|---|---|---|---|
| shipped rules, pre-registered | 7028 to 5764 | n/a | 2853 to 866 | n/a | 304 to 212 | 312 to 230 | 1934 to 1447 |
| shipped rules, counted | 7028 to 5764 | 4331 to 4331 | 2853 to 866 | 199 to 199 | 2159 to 2067 | 21294 to 21212 | 2340 to 1853 |
| kompress int8, pre-registered | 7028 to 6651 | 4331 to 3800 | 2853 to 2850 | n/a | 1855 to 1760 | 20982 to 20899 | 2340 to 2231 |
| rtk CCR, pre-registered | 7028 to 2179 | 4331 to 1142 | 2853 to 2235 | n/a | 2159 to 526 | 20982 to 666 | 2340 to 1267 |
| rtk CCR, counted | 7028 to 2179 | 4331 to 1142 | 2853 to 2235 | 199 to 346 | 2159 to 526 | 21294 to 1125 | 2340 to 1267 |

Because every size is one ratio away from its character figure, compression ratios per payload are
within 0.003 of the character figures (largest difference over all rows); the tool definition rtk adds has a token
estimate that rounds on its own. The token figures matter for the next step: the latency budget is
per 1,000 tokens, not per 1,000 characters.

## Amendment 2026-10-24: rtk CCR with retrieval allowed

**How `rtk_ccr` was graded before.** It is an extractive candidate (`preregistration.json`, gates
`extractive`): no reader ran. A question counted as kept when its expected answer appeared verbatim
(`grading.answered`) in the saved rtk output, the preview plus its `rtk_retrieve` marker. The tool
definition rtk injects was added to the reduced size. Retrieval was never available, so a lost answer
meant "the agent must call `rtk_retrieve`", not "the content is gone" (F6).

**Reader-path deviation (stated plainly).** The pre-registered QA reader is `claude-opus-5-5` at effort
low through `pi`. pi's Anthropic route is out of credit and other pi providers were not used, so both
new arms use `claude -p` with `claude-opus-5-5`, `--effort low`, isolated as `claude_digest_runner.command()`
(no tools, `--setting-sources ""`, `--strict-mcp-config`, `--no-session-persistence`, `--system-prompt`,
JSON output; proxy variables and `NODE_EXTRA_CA_CERTS` removed). Every reader call checked that
`modelUsage` named only `claude-opus-5-5`. Both arms read the same saved `rtk_ccr` outputs
(13 payloads, 3 runs, 3 replies per run, same questions). rtk was not re-run. The answers are therefore
not comparable with the pi-read rows of other candidates in this document; they are comparable with each
other. No quota or rate-limit error occurred (limit was two).

- `rtk_ccr_reader`: today's `READER_SYSTEM`, no retrieval. Graded by the reader's replies (QA), not verbatim.
- `rtk_ccr_retrieval`: the reader may reply exactly `RETRIEVE <id>` for an id in an `rtk_retrieve` marker in
  the text. A valid request gets one more call with preview, the original (`payload_text`) and the
  questions; a second request is not served. An id not in the text is treated as no answer (none
  occurred). Marker ids were checked to be one stable id per payload and unique across payloads; the
  runner cannot read rtk's store, so id-to-content is not independently confirmed beyond that.
- A run counts as retrieved, for the compression charge, when any of its three reader calls retrieved;
  the full original is then charged to `reduced_tokens`. Latency is rtk's saved `latency_s` plus the
  median wall time of the first (retrieval-asking) call of the retrieving calls: the extra round trip.
  Reader wall time per call is in `reader_wall_s` of each record (median 4.0 s without retrieval;
  3.3 s first call and 3.5 s second call with it).
- Retrieval rate is retrieving calls over all reader calls of the kind, including the payloads rtk left
  untouched (no marker, 0 retrievals): `dir_listing` and `dotnet_build_failure`.

**Per kind, pre-registered view** (untouched payloads left out of compression; `results/report.json`
`kinds`). Answers kept = questions where fewer than two of three replies miss, median run. Compression
is 1 - (preview + tool definition + fetched original) / original, floored at 0. Latency is the median
over the kind's payloads. Retrieval column is `rtk_ccr_retrieval` only.

| kind | answers, no retrieval | answers, retrieval | compression, no retrieval | compression, retrieval | latency s, no retrieval | latency s, retrieval | retrieval rate |
|---|---|---|---|---|---|---|---|
| code | 9/12 | 12/12 | 0.69 | 0.06 | 0.0021 | 1.51 | 8/18 (44%) |
| prose | 0/14 | 14/14 | 0.74 | 0.00 | 0.0006 | 3.31 | 17/18 (94%) |
| log | 5/13 | 13/13 | 0.22 | 0.00 | 0.0009 | 1.58 | 9/18 (50%) |
| listing | n/a | n/a | n/a | n/a | n/a | n/a | 0/9 |
| json | 0/19 | 19/19 | 0.76 | 0.00 | 0.0008 | 2.90 | 18/18 (100%) |
| build | 6/10 | 10/10 | 0.95 | 0.00 | 0.0029 | 1.64 | 8/18 (44%) |
| test | 5/10 | 10/10 | 0.46 | 0.00 | 0.0006 | 3.82 | 15/18 (83%) |

The `listing` kind is n/a in the pre-registered view (rtk left `dir_listing` untouched); in the
counted view both arms keep 6/6 and charge the tool definition (compression 0). The `build` and
`listing` rows above use the pre-registered view; the counted view (`kinds_counted`) differs only
there (build no retrieval 6/10, compression 0.95; retrieval arm 10/10). Every score at the 1 s budget
is 0.0 except `rtk_ccr_retrieval` code (0.10), because compression is 0 or tiny whenever the original
is fetched. The retrieval arm keeps every answer on every kind.

By payload, retrieval was 0/9 on `code_file` and `rtk_proxy_log` (the preview already held the
answers: 6/6 and 5/5) and 7-9 of 9 on the other nine marked payloads. Whenever the reader retrieved,
compression went to 0: the original was fetched whole, plus the preview and tool definition.

**Tool definition against the saving.** rtk injects one `rtk_retrieve` tool definition on every request,
426 characters, 147 estimated tokens (estimated, not provider-counted). Over the 13 payloads the preview
saved 33,295 tokens in total against 1,911 tokens of definitions (13 x 147). Per request the saving
exceeded 147 tokens by a clear margin on 9 payloads (`code_file` 1,552; `code_file_large` 3,591; `prose_large` 3,235;
`dotnet_build_failure_verbose` 20,463; `github_pr_list` 1,751; `dotnet_test_failures` 1,203;
`agent_log` 633; `rtk_proxy_log` 279; `incident_report` 248). On `json_payload` (176) and
`dotnet_test_failure` (164) the saving was 29 and 17 tokens above it, and on the two untouched payloads
the saving was 0, so the definition was a pure cost of 147 tokens. Those figures hold only when the
reader does not retrieve; once it retrieves, the original is charged in full and the saving is gone.

**Evidence grade: MEASURED** for everything above, from `results/rtk_ccr_reader.jsonl`,
`results/rtk_ccr_retrieval.jsonl` and `results/report.json` (`python3 scripts/reducer_scoring/harness.py report`,
`rtk_ccr_reader` and `rtk_ccr_retrieval` rows), run with `claude` 2.1.310. Token sizes are estimates.
Only one run set exists; the reader is the stated stand-in, not the pre-registered route.
