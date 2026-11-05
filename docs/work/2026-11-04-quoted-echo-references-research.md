# Research: replacing quoted echoes in user turns with references

**Date:** 2026-11-04
**Question:** When a user pastes part of an earlier answer or tool result into a new message, how often does it happen, what does it cost, and can the proxy (or a client hook) safely replace the copy with a reference the model still understands?

Plan: [2026-11-04-quoted-echo-references-plan.md](2026-11-04-quoted-echo-references-plan.md). Scripts and raw output live in the session scratchpad (`echo_scan.py`, `usage.py`, `prompt-test-results.txt` under `/private/tmp/claude-501/-Users-arasz-RiderProjects-ai-proxy/3db712c4-e20c-40c3-8e6b-5c95fd32f9d6/scratchpad/echo/`). They are not committed, so anyone re-running this should copy them first.

```chart:bars
title: transcript user turns holding an echo of assistant or tool text, by minimum span (chars, markdown-normalised)
80: 36
200: 15
500: 3
```

```chart:matrix
title: reference formats (1 = good, 0 = poor)
, resolved without a tool, deterministic, survives compaction, tokens low
message index + line, 1, 1, 0, 1
row/col position, 0, 1, 0, 1
retrieval id only, 0, 1, 1, 1
short summary + id, 1, 0, 1, 0
path:line, 1, 1, 1, 1
```

## Findings

### F1 — In 3,558 local Claude Code transcripts, 36 user turns (0.9%) carry an echo of at least 80 characters, and 15 carry one of at least 200 [MEASURED]

The corpus is every top-level session file in `~/.claude/projects/*/*.jsonl` (subagent files excluded), spanning 2026-10-03 to 2026-11-04, 17,398 model calls. A user turn counts when a person typed it: not a tool result, not `isMeta`, not a compaction summary, not a task or peer notification, with harness tags such as `<system-reminder>` stripped. 1,625 turns that repeated an earlier user turn of the same file word for word (resumed sessions copy history) were dropped, leaving 3,925 typed turns. Most of those are short: the median is 22 characters, and only 171 turns reach 80 characters. Measured against the turns long enough to hold an echo, the rate is higher, about one in five (36 of 171).

| Minimum span | Turns | Conversations | Echo chars p50 / p90 / max | Est. tokens, all echoes |
|---|---|---|---|---|
| 80 | 36 | 22 | 234 / 850 / 2,223 | 4,717 |
| 200 | 15 | 12 | 405 / 828 / 850 | 2,334 |
| 500 | 3 | 3 | 808 / 850 / 850 | 783 |

Split by source at 80 characters: 23 turns echo assistant text, 17 echo tool text (some echo both). The echo makes up a median 66% of the turn that holds it, which fits the owner's example: a pasted paragraph and a one-line request.

**Evidence:** `python3 -I echo_scan.py transcripts md` (and `ws`), MacBook, read-only file reads, run 2026-11-04 while the proxy was live. Output `sum-transcripts-md.json`. Token estimates divide characters by 2.89 (`src/AiProxy.Core/Gain/GainReportBuilder.cs:6`, calibrated in [2026-10-24-claude-token-calibration.md](2026-10-24-claude-token-calibration.md)).

### F2 — Re-sending those echoes costs about 0.06% of the prompt tokens of the conversations that contain them [MEASURED]

An echo stays in history, so every later call carries it again. Summing echo tokens times the number of later calls in the same file gives 785,538 estimated tokens at the 80-character threshold (310,887 at 200). The same 22 conversations billed 1,288,129,788 prompt tokens over their unique assistant messages (98,647 uncached input, 1,264,346,975 cache read, 23,684,166 cache write). The echo share is 0.061%, and most of it is billed as cache reads at 0.1x or less ([provider cache strategies](2026-10-24-provider-cache-strategies.md) F3). This is an upper bound: the later-call count does not subtract calls after a compaction dropped the echo.

**Evidence:** `resent_tokens_sum` in `sum-transcripts-md.json`; `python3 -I usage.py raw-transcripts-md.json`, which sums `message.usage` fields per unique `message.id` in the 22 hit files.

### F3 — The proxy capture store shows more tool echoes, but its "user text" is mostly harness-injected, so it overstates human echoes [MEASURED]

`capture.db` holds 16,113 requests from 2026-10-23 to 2026-10-26 (3,650 to `/v1/messages`). Keeping the longest request per thread gives 1,303 threads and 2,339 user text blocks. Assistant echoes are rare there too (15 turns at 80 characters, 3 conversations; 2 at 200). Tool echoes look common (322 turns at 80, 79 at 500), but a sample of the 500-character hits shows subagent prompts ("This agent is isolated in the worktree ...", 38,000-character blocks), source files and XML configs that sit in user-role text blocks the harness built, not text a person pasted. The capture body has no marker that separates typed text from injected text, so the transcripts in F1 are the better source for this question. `content.db` (`content_request`, 203 rows) holds only requests the compressor touched and adds nothing.

`sqlite3 -readonly` and `mode=ro` both failed to open `capture.db` (error 14, cannot open the file). It was opened with `immutable=1`, which takes no lock; the file had not been written since 2026-10-26, so this is safe.

**Evidence:** `python3 -I echo_scan.py capture md`, output `sum-capture-md.json`; schema at `src/AiProxy.Storage/SqliteCaptureStore.cs:227-246` and `src/AiProxy.Storage/ContentSchema.cs:159-200`.

### F4 — Markdown normalisation matters more than fuzzy matching: terminal copies lose `**`, bullets and backticks [MEASURED]

Two normalisations were compared on the same corpus. Whitespace plus `>` quote markers only (`ws`) finds 7 turns at 200 characters and none at 500. Adding removal of `*_`#|`, list bullets, numbering and box-drawing characters (`md`) finds 13 and 2 for assistant text. The difference is copies from the terminal render, where the model's `**MAJOR**` arrives as `MAJOR`. Line-hash shingles were not built; see F12.

**Evidence:** `assistant>=200` and `assistant>=500` rows in `sum-transcripts-ws.json` against `sum-transcripts-md.json`; normalisers `norm_ws` and `norm_md` in `echo_scan.py`.

### F5 — At 80 characters, a third of the hits are commands, paths or code, not quotes pointing at an answer [MEASURED]

Reading the first 100 characters of each hit at 80 characters (40 hit rows over the 36 turns): about 20 are the pattern the plan describes, a pasted paragraph of an earlier answer followed by an instruction. The rest are a hook-printed command the user pasted back (`task_tracker.py reattach ...`, six rows), file paths, code the user pasted from the editor that a `Read` result already held, a pasted `dotnet-install` log, and owner-gate answers whose decision titles match the review page. Code and logs echo tool text; real quotes echo assistant text. The short hits are where the false positives sit. From 200 characters up, the assistant-source hits are almost all genuine quotes.

**Evidence:** hit listing printed by the inline loop over `raw-transcripts-md.json` (`snip` field), 2026-11-04. Snippets were read by hand; no secrets appeared and none are quoted here beyond the owner example.

### F6 — The model resolves a message-index reference and a summary-plus-id reference, and fails on row/column and on a bare id [MEASURED]

Six `claude -p --model sonnet --tools ""` calls, one per format, on a four-message transcript where the second assistant message lists a MAJOR, eight MINORs and a test-review finding. The final user turn pointed at the MAJOR and said "fix the issue". The question asked which issue the user meant, with "UNRESOLVED" allowed.

| Format in the user turn | Chars | Est. tokens | Result |
|---|---|---|---|
| verbatim echo (baseline) | 135 | 47 | MAJOR only (correct) |
| `[quote of m2, lines 2-2]` | 24 | 8 | MAJOR only (correct) |
| `[quote ref:context:start{8,0}:end{8,121}]` | 41 | 14 | UNRESOLVED |
| same, row corrected to 5 | 41 | 14 | UNRESOLVED ("points at m5 itself") |
| `[quote of m2 lines 2-2: "the code review MAJOR finding"; exact text via proxy_retrieve <id>]` | 104 | 36 | MAJOR only (correct, no tool needed) |
| `[quote <id>; call proxy_retrieve for the text]` | 58 | 20 | UNRESOLVED (no tool to call) |

The first row/col attempt used a row I had miscounted. That mistake is the finding in small: a position in "the context" has no agreed origin, since the request is JSON with no rows, and both writer and reader have to agree on a rendering. One call per format on one synthetic case shows the direction, not a rate.

**Evidence:** `prompt-test-results.txt` and the `u_*.txt` / `ctx.txt` inputs in the scratchpad, run 2026-11-04 on the owner's subscription (project memory "claude -p readers"). No paid API was called.

### F7 — The baseline understood the verbatim echo, so the clarity benefit is unshown [INFERRED]

From F6: the model picked out the MAJOR from the pasted paragraph without help. The plan's second motive, that an echo "can blur what the user is asking", did not show up in the one case tried. It may show up with long echoes holding several items, which nobody has tested.

### F8 — A Claude Code `UserPromptSubmit` hook cannot rewrite the prompt; it can only block it or add context [READ]

The hooks reference states that `UserPromptSubmit` "can't replace the prompt; it only injects `additionalContext` alongside it". The only `decision` value is `"block"`, with a `reason`, and exit code 2 also blocks the prompt so it "never reaches Claude". A hook can therefore detect an echo and stop the send with a message ("this repeats m2; resend with a reference"), but it cannot shorten what is sent, and `additionalContext` makes the request longer. The proxy sees every client (Claude Code, Codex, any SDK caller) and can rewrite. The hook only sees Claude Code and its one advantage is that it can ask the user first.

**Evidence:** https://code.claude.com/docs/en/hooks, sections "Decision control" and "Exit code 2 behavior", fetched 2026-11-04.

### F9 — CCR already has a deterministic id, marker and retrieval path that an echo reference could reuse [READ]

A CCR id is the first 16 hex characters of the SHA-256 of the original text (`src/AiProxy.Core/Content/Ccr/CcrText.cs:11`). The marker is fixed text with no timestamp or counter, and preview bytes are a pure function of the original plus a pinned version (ADR 0005, §Preview, lines 30-39). The `proxy_retrieve` tool reads `ccr_original` by that id (`src/AiProxy.Core/Content/Ccr/CcrRetrieval.cs:13`), and originals expire 14 days after first keep (ADR 0008, Decision 4). A region that already holds a `[proxy preview` marker is never previewed again (ADR 0005, line 137). Today the region finder only selects tool output (`ToolOutputRegionFinder.cs:192-201`), so user text is out of reach of every reducer.

**Evidence:** files and lines as cited, in this worktree at origin/main 0.22.1.

### F10 — The rewrite of user turn k keeps later cached prefixes intact only if it is a pure function of messages 0..k [INFERRED]

From the cache record F1 (the cache key hashes everything from `tools` to the breakpoint) and F12 (a stateless rewrite must be prefix-stable), plus ADR 0005 line 14 (Claude Code threads send each block once). What has to hold:

1. The detector searches only text before turn k in the same request, never later turns, so adding turns k+1..n cannot change the output for turn k.
2. It matches against the original bytes of earlier turns, not against their reduced or previewed form, or against the reduced form consistently. Mixing the two flips a match when a reducer version changes.
3. The output depends on no clock, counter, store state or token budget, and the normaliser and threshold sit behind a pinned version whose change ships as a cache event.
4. For a thread client the rewrite happens once, on the send that first carries turn k, which is after the last breakpoint, so nothing cached changes.
5. If the source turn disappears (compaction, front truncation), the prefix has already changed from that point, so the cache is gone anyway and the new decision costs nothing extra.

The risk side is F11 of the cache record: one break re-writes the whole prefix behind it at 1.25x, while the echo saves a median of about 81 tokens (F1). A single unstable decision on a 100,000-token prefix wipes out the savings of many echoes.

### F11 — Safety needs four rules, and the measured data says which matter [INFERRED]

From F5 and F6:

- Replace only the matched span. Every character the user typed outside it stays, in order.
- Match assistant text only. Tool-text matches are mostly code and logs the user pasted on purpose (F5), and "is this pasted editor code an echo of the `Read` result?" has no safe answer from bytes alone.
- Keep a short head of the quote inside the reference (about 60 characters) so a reference the model cannot resolve still says what it pointed at. F6 shows a bare pointer fails closed into "UNRESOLVED".
- Skip any span that already contains a `[proxy preview` marker or a previous echo reference, the same rule CCR uses.

### F12 — Whether edited or partial quotes need shingle matching is unknown [UNVERIFIED]

The scan found exact spans only. It did not measure how many quotes the user trimmed or edited in the middle, which would split one quote into several shorter spans and push some below the threshold. A shingle or longest-common-subsequence pass over the 171 long turns would settle it.

### F13 — Other clients and other users may echo far more [UNVERIFIED]

Every number above comes from one person's Claude Code sessions in one month. Chat-style clients, where copy and paste of answers is the main way to refer back, were not available to measure.

## Recommendation

Park it, and do not build the rewrite now. The measured saving is about 0.06% of prompt tokens in the conversations where echoes happen (F2), less across all traffic, and mostly at cache-read prices. Against that sits a new rewrite of user text, the one region the proxy has never touched, with a cache-break failure mode that costs far more per mistake than a whole thread of echoes saves (F10). The clarity benefit did not show in the one test that could have shown it (F7).

If the owner wants it anyway, the shape the evidence supports is: proxy-side (the hook cannot rewrite, F8), off by default, assistant-source matches only, at least 200 markdown-normalised characters, and a reference of the form `[quote of assistant message N, lines a-b: "<first ~60 chars>…", id <16-hex>]` with the original stored in `ccr_original` so `proxy_retrieve` can return it after compaction. That combines the two formats that resolved in F6 and costs roughly 35 to 45 estimated tokens, so it only pays on spans above about 130 characters.

## Owner decisions needed

Ruling (2026-11-05): build. The owner answered decision 1 only. Decisions 2 to 6 take the shape recommended above as defaults: proxy rewrite, off by default, assistant text only, at least 200 normalised characters, and the message-index plus quote-head plus id reference. The build plan can revisit any of them with evidence.

1. Build or park, given a measured upper bound of 0.06% of prompt tokens.
2. If built: the reference format (message index plus line plus quote head plus id, or something else).
3. Proxy rewrite or a Claude Code hook that blocks and asks the user to resend (the hook cannot rewrite).
4. Default on or off.
5. Minimum span: 200 normalised characters recommended; 80 lets commands and paths through.
6. Sources: assistant text only, or tool text too.

## Still open

- Edited and partial quotes (F12): run a shingle pass over the 171 long turns.
- A larger prompt test: several formats times several echo shapes (multi-item lists, code blocks, long echoes), more than one call each, to put a rate on F6 and test F7.
- Whether markdown-normalised matching ever matches across a code block where the stripped characters were meaningful (`*` in a glob, `_` in an identifier). The `md` normaliser removes them on both sides, so a match is still a real copy, but the reference must point at the original bytes.
- How Codex and other clients render history, and whether their users quote more.
- Why `sqlite3 -readonly` cannot open `capture.db` while `immutable=1` can. Likely the read-only open needs the `-shm` or journal file beside it; not checked.
