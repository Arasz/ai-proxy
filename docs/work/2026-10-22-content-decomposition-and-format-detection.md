# Content decomposition, format detection, and the measured reduction recipes

**Date:** 2026-10-22
**Question:** An agent tool output can be a directory listing, a grep result, a JSON blob and a
log tail in one payload. How should the proxy split it, how should it tell the parts apart, and
which reduction rule belongs to which part?

**Provenance.** One research lane was dispatched for the decomposition and detection questions and
its findings are carried here. The reduction recipes were recovered by the orchestrator from the
orchestrator's own benchmark scripts: the source of `~/kompress-work/reducer.py` and
`~/kompress-work/bench_reducers.py` no longer exists on disk, but their compiled forms survive in
`~/kompress-work/__pycache__`, and CPython 3.12's `dis` module reads them. The recipes below are
therefore the measured ones, not a reconstruction of somebody's memory of them.

## Findings

### F1 — The measured reduction recipe is a per-kind structural reduce, then a must-keep overlay [MEASURED]

The benchmark's `structural+overlay` reducer is `overlay_must_keep(structural_reduce(text, kind), text)`,
and `structural_reduce` branches on the payload kind: for a log it keeps the signal lines and
deduplicates, for JSON it re-serialises the parsed value with `(',', ':')` separators, and for
everything else it deduplicates the lines. The `combined` reducer, which is the one the routing
table was read off, is `overlay_must_keep(kompress(structural_reduce(text, kind)), text)`: the
same structural reduce, then the extractive model, then the overlay.

The `combined+lines` variant swaps the token-level overlay for a line-level one that re-attaches
whole original lines carrying a token the reduction lost.

**Evidence:** `dis` output of `bench_reducers.cpython-312.pyc` (the `structural`, `combined` and
`combined_lines` functions) and of `reducer.cpython-312.pyc` (the `structural_reduce` function),
both read with CPython 3.12 on 2026-10-22.

### F2 — The log reducer keeps the lines that carry a signal, plus two anchors at each end [MEASURED]

A line is kept when the message matches
`fail|error|timeout|exhaust|refus|denied|fatal|panic|status=[45]\d\d|warn`, or when its parsed
level is `ERROR`, `FATAL` or `WARN`, or when it is among the first two or last two lines of the
payload. The level is parsed from `^\s*(\d{4}-\d{2}-\d{2}[T ]\S+)\s+(TRACE|DEBUG|INFO|WARN|WARNING|ERROR|FATAL)\b`,
so a line without a timestamp is kept on the signal rule alone.

The comment in the measured script says why the level rule is not enough: a line at INFO level can
still be the one that matters ("request failed status=503"), and a line that merely contains a
number is not a signal either, because every log line has a timestamp. The first version of the
reducer dropped exactly the `status=503` line.

**Evidence:** `reducer.cpython-312.pyc`, the `keep_log_level_lines` function and its docstring,
read with `dis` on 2026-10-22.

### F3 — The deduplication keeps the first occurrence of a repeated line and drops the rest [MEASURED]

`dedup_lines` splits on newlines, counts the non-empty trimmed lines, and for a line that occurs
more than once keeps the first occurrence and drops the rest; a line that occurs once is kept, and
an empty line is kept as it is. The docstring gives the reason: "A log line repeated on every
request is the cheapest byte to lose."

**Evidence:** `reducer.cpython-312.pyc`, the `dedup_lines` function and its docstring.

### F4 — The must-keep overlay re-attaches identifiers the reduction lost, as a labelled trailing block [MEASURED]

The tokens that must never be lost are matched by one regular expression: hexadecimal literals,
bare numbers, `UPPER_CASE` words, dotted lowercase identifiers, absolute paths, file extensions,
command-line flags, `CamelCase` words, and a list of directive words (`must`, `should`, `never`,
`not`, `forbidden`, `only`, and the rest of that list). The overlay collects the tokens present in
the original but missing from the reduced text, in first-seen order, and appends them as
`\n[retained identifiers] token token token`.

The overlay's own docstring names its limit: it restores tokens and not phrases, so "Dana Okonkwo"
and "PR 8842" are answers that need their words adjacent, and a bag of tokens cannot rebuild them.
The line-level overlay exists for that case and re-attaches whole original lines.

**Evidence:** `reducer.cpython-312.pyc`, the `MUST_KEEP_RE` constant and the
`overlay_must_keep` and `overlay_lines` functions.

### F5 — The overlay has a hole, and the hole is the decisions class [READ]

The research record calls the decisions class (a failure verdict, an exit status, a state
transition such as "retried" or "quarantined") the highest-value class, and says the overlay's
regular expression catches `ERROR` and `FATAL` only because they are capitalised. A lowercase
state transition is unprotected, and only the log branch protects it, through the signal filter
rather than through the overlay.

The rules below close the hole, and each one is a rule a test can pin:

| rule | matches | example line | expected |
|---|---|---|---|
| failure verdict | `FAILED\|ERROR\|FATAL\|CRITICAL` | `2026-03-12 10:00:00 FATAL timeout` | matched |
| exit status | `exit [0-9]{1,3}` or `exit code [0-9]{1,3}` | `bash: build failed, exit code 2` | matched |
| lowercase state transition | `\b(retried\|requeued\|quarantined\|aborted\|rolled back\|skipped\|timed out)\b` | `worker-3 retried after 2 attempts` | matched |
| absolute path | `/[^\s]+` | `open /var/log/app.log failed` | matched |
| explicit directive | `must-keep\|do not drop\|keep this` | `must-keep: the trace id` | matched |

**Evidence:** the hole is stated in `docs/work/2026-10-22-on-device-compression-models.md` (branch
`docs/local-prompt-compressors-record`), "What this means for the proxy", the must-keep-overlay
paragraph. The rules and the examples are proposed here, not measured.

### F6 — The format kinds split into "a parse proves it" and "a heuristic guesses it" [READ]

JSON, XML, TOML, YAML, CSV, HTML and Markdown are cheap to prove by parsing, and a parse that
succeeds is proof. A directory listing, a shell transcript and a run of log lines cannot be
proved: they are recognised by shape, and the detection is a guess with a confidence, not a fact.

The pipeline recognises the kinds it has a reducer for: JSON, XML, TOML, Markdown, source code,
a directory listing, a log, a shell transcript and prose. YAML, CSV and HTML are parseable and
would be cheap to add, and they are not in this release, so a payload in one of them arrives
as prose.

The consequence for the pipeline is that a failed parse is not an error. A block that starts with
`{` and does not parse is text, and the pipeline treats it as text.

**Evidence:** the routing table in `docs/work/2026-10-22-on-device-compression-models.md` (branch
`docs/local-prompt-compressors-record`), F1, which routes by payload kind and measures a rule-based
reducer for every kind that has one.

### F7 — Decomposition is not settled by the reduction research, and the answer for v1 is rules [INFERRED]

The compression record settles which reducer runs on which payload. It does not settle whether the
payload should be split at all, because splitting is a different question from reducing: a wrong
split of a JSON blob produces two invalid halves, while a wrong reduction produces a lossy but
valid document.

The v1 answer is to split, and to split by structure rather than by guess: a fenced code block, a
balanced JSON value, a balanced XML element, and the remaining text blocks. A split that cannot be
proved is not made, and the whole payload falls back to a single part.

**Evidence:** reasoned from F6 and from the record's own statement that the model is a no-op on
structured payloads, which means a model cannot be used to find the structure either.

### F8 — There is no canonical tool-output envelope to key on [READ]

The proxy sits in front of several agents and cannot assume one envelope. The formats in the wild
are a JSON envelope, an XML-ish tool block, and plain text with separators, and the agent CLIs do
not agree on any of them.

What the pipeline does is narrower than "no envelope, so read the text": the finder recognises
the four shapes the dialects actually use for a tool's output, which is a Anthropic
`tool_result` block, an OpenAI message with the role `tool`, an OpenAI `function_call_output`, and
a Gemini `functionResponse`. It finds the tool output by where it sits in the body, and it does
not need a marker convention to do that.

**Evidence:** the repository holds no agent-CLI tool-output specification, and the compression
record's payloads are raw `ls`, `grep` and log text with no envelope around them
(`docs/work/2026-10-22-on-device-compression-models.md`, F1, the payload table).

## What this means for the pipeline

- The reduction rule per kind is the measured one: a structural reduce, then the must-keep
  overlay, and never a summariser.
- The decomposer splits by structure, and a part it cannot prove stays whole.
- The overlay grows five rules, and the decisions class stops being a hole.
- The model is used for prose only, if at all, and never to find structure.
- The finder recognises the dialects' tool-output shapes and finds the output by position, so
  no envelope convention has to be agreed on and no marker has to be looked for.

## Still open

- The detector thresholds between a shell transcript and a run of log lines, which are heuristics
  in both directions.
- Whether a JSON payload that a tool printed inside a shell transcript is worth its own part, or
  whether the transcript rule should swallow it.
- Whether the line-level overlay, which costs a whole line per lost phrase, is worth enabling by
  default for source code.
