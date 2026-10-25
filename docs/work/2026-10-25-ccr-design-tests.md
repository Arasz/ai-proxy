# Test list: tool result previews with retrieval answered inside the request

Date: 2026-10-25
Status: design, for [ADR 0005](../adr/0005-tool-result-previews-with-in-request-retrieval.md) (Draft). Plan step `ccr-design`, acceptance item `tests`.

Each row is one test. "Mutation" is the defect put in front of the test to watch it go red before the real code makes it green (`prove-the-check-fails`). Layer is where the test lives: Core unit (pure, no HTTP or SQLite), Storage (real SQLite file in a temp directory), or Host (the real proxy against a fake upstream that records each body and replays scripted SSE or JSON responses). No row calls a real provider. The last section lists the checks that need a live session and cannot be unit tests.

## Preview

| # | Layer | Behaviour | Failure mode it targets | Mutation that proves it |
|---|---|---|---|---|
| P1 | Core | The same original gives byte-identical preview text across two builder instances and two calls | Preview depends on hidden state, so stateless re-sends miss the cache | Append a call counter to the marker |
| P2 | Core | The preview contains no clock value: built under two `FakeTimeProvider` times, bytes are equal | A timestamp leaks into the marker | Put `time.GetUtcNow()` into the header |
| P3 | Core | Built under `de-DE` and `en-US` current culture, bytes are equal | Culture-dependent number formatting (`3.000` against `3,000`) | Format line counts with the current culture |
| P4 | Core | The id is the first 16 hex characters of SHA-256 of the original UTF-8 text, checked against a known vector | Id derived from request id, position or reducer version | Hash the original plus its region index |
| P5 | Core | Two originals that differ by one byte get different ids | Id derived from a prefix or the length | Hash only the first 4 KB |
| P6 | Core | A region of 2,999 estimated tokens is not previewed; 3,000 is | Off-by-one, or the threshold judged per decomposed part instead of the whole region (D6) | Compare against the largest part's size instead of the region's |
| P7 | Core | A region made of many small parts that together exceed the minimum is previewed | Per-fragment minimum skips a large region (untouched-payloads finding) | Apply `MinimumFor` per part |
| P8 | Core | Every preview line except markers and the overlay block appears verbatim in the original, in the same order | Preview rewrites content, so a fact in it is wrong | Trim trailing whitespace on kept lines |
| P9 | Core | A build log whose only error line sits in the middle keeps that line in the preview with its original line number | Fixed head and tail drops the signal line (rtk's build and test losses) | Disable signal-line selection |
| P10 | Core | Preview size is at most 1,500 estimated tokens including markers and overlay | Budget ignores marker or overlay cost | Exclude the overlay from the budget sum |
| P11 | Core | Each elided run is named by its exact original line range, and the named ranges plus shown lines cover 1..N with no gap or overlap | Wrong line numbers send the model to the wrong slice | Number shown lines from 0 |
| P12 | Core | A single line longer than the budget is cut at a character offset and the cut is named | One-line JSON produces an over-budget preview or an empty one | Skip lines longer than the budget |
| P13 | Core | A region that already contains `[proxy preview` is returned unchanged and not previewed | Double preview or expansion of an echoed preview | Remove the marker check |
| P14 | Core | Prefix stability: for a transcript of k tool results, the rewritten bytes of the first j results equal the first j results of the rewrite of all k, for every j | A rewrite depends on regions after it (cache strategies F12) | Choose the budget from the number of regions in the request |

## Retrieval tool

| # | Layer | Behaviour | Failure mode it targets | Mutation that proves it |
|---|---|---|---|---|
| R1 | Core | The injected definition serializes to the exact bytes of a checked-in fixture | Definition drifts between builds and changes `tools`, breaking every cache | Reorder schema properties |
| R2 | Core | `lines` 10-12 returns exactly those three original lines with number and tab prefixes | Off-by-one slice | Make `end` exclusive |
| R3 | Core | `lines` with `start` > total, `end` < `start`, or `start` = 0 returns `bad_range` with the total line count | Silent empty result the model reads as "nothing there" | Clamp instead of failing |
| R4 | Core | `pattern` returns matching lines with `context` lines, at most 50 matches, and says when it stopped | Unbounded result defeats the saving | Remove the match cap |
| R5 | Core | `pattern` on a line over 240 characters returns a 240-character window around the match | One-line JSON match returns the whole document | Return the whole line |
| R6 | Core | An invalid regex returns `bad_pattern`; a catastrophic pattern finishes within the 250 ms timeout | ReDoS on the request path | Drop `NonBacktracking` and the timeout |
| R7 | Core | Zero matches is a normal result, not `is_error` | Model treats a negative answer as a failure and retries | Return `bad_pattern` on no match |
| R8 | Core | after its header line, `whole` returns the original byte for byte, with CRLF and a trailing newline kept and no line-number prefixes | Edits fail on altered whitespace (activation record) | Normalize line endings |
| R9 | Core | A `lines` result over 3,000 estimated tokens is cut and names the next start line | Large slice silently truncated | Cut without the continuation line |
| R10 | Core | Missing `id`, unknown `mode`, extra property, or a malformed id returns `bad_input` | Proxy throws and the client request fails | Let the parser throw |
| R11 | Core | An id with no original returns `not_kept` with the "re-run the tool" text | Proxy throws or answers from another original | Fall back to the most recent original |

## Loop state machine (Core)

| # | Layer | Behaviour | Failure mode it targets | Mutation that proves it |
|---|---|---|---|---|
| L1 | Core | Only the declared transitions are accepted; an undeclared one (Done to Retrieving) throws and records the trigger | State changes outside the machine | Add a public setter on the state |
| L2 | Core | Three rounds are answered, the fourth call gets `limit_reached`, a fifth fails the request: at most five upstream calls | Unbounded loop burns tokens | Raise the cap check to `<=` |
| L3 | Core | A message with two retrieval calls is answered in one round with two tool results | Second call left without a result, upstream 400 | Answer only the first call |
| L4 | Core | A message mixing retrieval with client calls produces an `is_error` "not run, issue again" result for each client call and passes none of them to the client | Client executes a call the server thread will also get a result for, or upstream 400 on a missing tool result | Forward client calls to the client |

## Host: in-request loop through the real proxy

| # | Layer | Behaviour | Failure mode it targets | Mutation that proves it |
|---|---|---|---|---|
| H1 | Host | Non-streaming stateless: upstream answers with a retrieval call, then text; the client gets one message with the text, the last id, the last `stop_reason`, and the fake upstream received the second request with the assistant message and tool result appended | Client sees the internal tool call | Return the first upstream response |
| H2 | Host | The follow-up carries the client's own auth headers and no proxy header | Follow-up unauthenticated, or credential sent somewhere else | Build the follow-up from default headers |
| H3 | Host | The assistant message in a stateless follow-up equals the first response content byte for byte, thinking blocks and signatures included | Upstream rejects a follow-up with altered thinking | Drop thinking blocks from the follow-up |
| H4 | Host | Streaming: text deltas before the retrieval call reach the client before the follow-up starts (first byte measured against the fake upstream's delay) | Whole response buffered, streaming lost | Buffer until `message_stop` |
| H5 | Host | Streaming with retrieval: the client stream has one `message_start`, contiguous block indexes, no `proxy_retrieve` block, one `message_delta` with summed output tokens and one `message_stop` | Two messages spliced badly, client parser breaks | Forward the follow-up's `message_start` |
| H6 | Host | Streaming: a client `tool_use` block before a retrieval call in the same message never reaches the client | Client runs a deferred call | Release tool blocks as they arrive |
| H7 | Host | Streaming without retrieval: the client stream equals the upstream stream byte for byte | Hold-back reorders or rewrites normal responses | Renumber indexes when nothing was dropped |
| H8 | Host | `ping` events reach the client while the follow-up runs longer than the fake's 5 s delay | Client times out during the hidden round | Send nothing between rounds |
| H9 | Host | An upstream 529 during a follow-up becomes an `error` event (streaming) or the upstream status (non-streaming) | Client hangs or gets a truncated success | Swallow the follow-up error |
| H10 | Host | Thread: a `continue` follow-up names the first response id as `previous_message_id` and carries only the tool results | Follow-up re-sends history or branches from the wrong message | Use the client's `previous_message_id` |
| H11 | Host | Thread streaming: after a retrieval turn, the client's next `continue` naming the first id reaches upstream naming the last id | Next turn continues from before the hidden turn and loses the model's answer | Skip the alias lookup |
| H12 | Host | Thread streaming: with the ledger write failing, the client gets an `error` event, not `message_stop` | Client continues from an id that drops the answer | Ignore the ledger write result |
| H13 | Host | Thread: a `continue` whose `previous_message_id` is not in the ledger gets no previews and no hint | Preview in a thread with no retrieval tool | Treat unknown threads as known |
| H14 | Host | Two files read in one request get two ids, and retrieving each returns its own exact bytes, including same basenames and reversed result order | Hint resolves to the wrong file (activation record) | Key originals by basename |
| H15 | Host | A model call to `proxy_retrieve` with no preview in context gets `not_kept`, and the client request still completes | Spurious call breaks the turn | Throw on an unknown id |

## Cache placement and determinism (Host, bytes at the fake upstream)

| # | Layer | Behaviour | Failure mode it targets | Mutation that proves it |
|---|---|---|---|---|
| C1 | Host | `proxy_retrieve` is the last element of `tools`, has no `cache_control`, and every other tool and `cache_control` field is byte-identical | Injection moves a client breakpoint or adds a fifth | Insert the tool first |
| C2 | Host | A tools-bearing request with no preview still gets the tool; a request without `tools` gets none | `tools` changes the first time a preview appears, breaking the prefix | Inject only when a preview exists |
| C3 | Host | Stateless replay: turn n then turn n+1 (history grown) forward byte-identical bytes for every region already sent, before and after a proxy restart | Re-preview differs and turns reads into writes | Seed the overlay order from a random |
| C4 | Host | Stateless replay with the store failing on turn n+1: forwarded bytes for previewed regions equal turn n, and retrieval is answered from the request | Store state changes bytes | Skip the preview when the store write fails on a stateless request |
| C5 | Host | `system`, thinking blocks, `tool_use` input key order and block and message counts of the client part of the request are byte-identical | Rewrite reaches outside the tool result text | Re-serialize the whole body |
| C6 | Host | Requests to an OpenAI, Gemini, OpenRouter or unlisted host are forwarded with no tool and no preview in this release | CCR applied where no loop exists | Select the Anthropic strategy for every dialect |
| C7 | Host | A `cachedContent` Gemini request is never touched (kept for when Gemini is enabled) | Change to immutable cached tools | Inject into every Gemini request |

## Storage and failure fallback

| # | Layer | Behaviour | Failure mode it targets | Mutation that proves it |
|---|---|---|---|---|
| S1 | Storage | Keeping the same original twice leaves one row and both calls succeed | Duplicate key error on a repeated file read | Plain `INSERT` |
| S2 | Storage | A kept original is readable by a second connection as soon as keep returns, with 70 rows written before it | Hint published before the original is readable; the 64-row queue scan | Route the write through the queue |
| S3 | Storage | Rows older than 14 days are removed by the sweep, newer ones stay, and a read does not extend the date | Originals never evicted, or evicted early | Compare against the last read time |
| S4 | Host | Thread request with the keep failing (injected), the queue failing, or lookup unavailable: the forwarded tool result is the reducer's output with no `[proxy preview` hint | Hint without a readable original (activation record) | Publish the preview before checking the keep result |
| S5 | Host | After eviction, a thread retrieval returns `not_kept` and the client request completes | Proxy errors when an old thread asks | Throw on a missing row |
| S6 | Storage | Two writers keeping the same id at once both succeed and one row remains | Concurrent handoff failure (activation record) | Remove `OR IGNORE` |
| S7 | Core | `AiProxy.Core` has no reference to SQLite, ASP.NET or Titanium (architecture test over the assembly references) | Layering broken by the new port | Reference `Microsoft.Data.Sqlite` from Core |

## Needs a live session (activation checks, not unit tests)

- A Claude Code thread through the loop: after a retrieval turn the next `continue` succeeds and the model refers to the text it wrote after retrieving. Confirms the `previous_message_id` assumption.
- A stateless client with extended thinking re-sends a merged assistant message after a retrieval turn and the provider accepts it.
- `usage` in the captured responses: the cache read on the turn after a retrieval turn follows cache_read(n) = cache_read(n-1) + cache_creation(n-1), with the tool definition present from `create`.
- The retrieval-aware re-score in ADR 0005's activation gate: every answer kept with retrieval, at most 25% retrieval rate, at most 80% of original tokens net, per kind.
