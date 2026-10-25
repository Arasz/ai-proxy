# 0005: Tool result previews with retrieval answered inside the request

Date: 2026-10-25
Status: Draft

## Context

A large tool result is the part of a request that grows without bound. Recoverable compression (CCR) sends the model a short preview of that result plus a tool it can call to read the rest. The proxy answers that call itself, inside the same client request, so the client never sees it.

rtk-proxy's CCR is the only working example on this machine, and it loses. It previews any result over about 310 tokens with a fixed head and tail ([rtk record](../work/2026-10-23-rtk-proxy-ccr-threshold-and-chaining.md) F1). Scored with retrieval allowed, the reader kept every answer but fetched on 44 to 100% of calls for six of seven kinds, and every fetch returned the whole original. Net, 40,204 original tokens became 43,938 sent, 9% more than sending nothing compressed ([scoring record](../work/2026-10-24-reducer-scoring-results.md), amendment "rtk CCR with retrieval allowed"). The 147-token tool definition was charged at full price on every request there. On a provider cache it is a one-time write and then a cache read.

The owner ruled on 2026-10-24 ([feedback](../work/2026-10-24-retrieval-activation-gates-feedback.md)). Previews should rarely need a fetch, and retrieval returns only what is asked for: a line range, a pattern match, or an explicitly requested whole result (D1). CCR is built for tool results first (D2). The size minimum applies to a whole region, not to each fragment (D6). The whole request is in scope later, so nothing here may assume tool results are the only region ever compressed.

Three facts about caching bound the design. Claude Code threads send each block once and send tool definitions only on `create` ([capture first look](../work/2026-10-23-capture-first-look.md) F1). Thread history is billed as cache reads, with cache_read(n) = cache_read(n-1) + cache_creation(n-1) on 509 of 665 turns ([thread billing](../work/2026-10-24-thread-cache-billing.md) F1). Stateless clients re-send everything, so any rewrite must be a pure function of the region's own bytes (same record, F4). The [cache strategies record](../work/2026-10-24-provider-cache-strategies.md) (as corrected by PR #62) adds that a change to `tools` invalidates the whole Anthropic cache (F1), and that a break on a cached prefix of T tokens costs an extra 1.15T to 1.2T once (F11).

The [activation record](../work/2026-10-24-csharp-reducer-activation.md) requires originals to be readable before a hint is published, and the original request forwarded without a hint when storage or lookup fails. The C# reducer consumes the tool contract decided here.

## Decision

### Where it runs

CCR runs after the kind reducers, on each tool result region the `ToolOutputRegionFinder` returns. A region is previewed only when its reduced text is still at least 3,000 estimated tokens (8,670 characters at the measured 2.89 characters per token). The size is judged on the whole region, never on a decomposed part. A region below that size gets today's reducer output and no hint. Both numbers are assumptions until the activation gate below confirms them.

The first release covers Anthropic Messages on `api.anthropic.com`, thread and stateless. Placement for the other providers is decided below, but nothing is injected or previewed there until each has its own loop and passes the same gate. The universal fallback never gets CCR, because its rule 5 forbids touching `tools` (cache strategies F13).

### Preview format

The preview is a subset of the original's lines, kept verbatim, in original order, within a budget of 1,500 estimated tokens. Selection is structure-aware rather than a fixed head and tail. Lines that match the failure, exit-code and state-transition patterns of `MustKeepOverlay` are kept first, up to 40% of the budget. The rest goes to the head and the tail, two parts head to one part tail. Each elided run becomes one line naming the original line range. A trailing `MustKeepOverlay` block lists identifiers from the hidden lines, capped at 5% as today, so the model knows what to search for. A single line longer than the budget is cut at a character offset and the cut is named. The split ratios are assumptions the gate tunes.

The marker is fixed text with no timestamp, counter, request id or store state:

```text
[proxy preview 3f2a9c0d81b7e645: 120 of 3000 lines shown. Call proxy_retrieve with this id for a line range, a pattern, or the whole text.]
...
[lines 41-2980 not shown]
...
```

The id is the first 16 hex characters of the SHA-256 of the region's original UTF-8 text. The preview bytes are a pure function of the original and a pinned preview version, with invariant-culture number formatting. The same original gives the same id and the same bytes in every request and after every restart. Changing the preview version is a cache event and ships as one.

This answers the 9% loss in three ways. Fewer regions are previewed, and only large ones, so each preview saves at least 2,000 tokens before any fetch. Signal lines survive, which is where rtk lost build and test answers. And a fetch returns a slice, not the whole original.

### Retrieval tool

One tool, appended to the request's tools:

```json
{
  "name": "proxy_retrieve",
  "description": "Reads part of a tool result the proxy shortened. Use the id from a [proxy preview ...] line. Ask for lines or a pattern; ask for the whole text only when you need all of it.",
  "input_schema": {
    "type": "object",
    "properties": {
      "id": { "type": "string", "pattern": "^[0-9a-f]{16}$" },
      "mode": { "type": "string", "enum": ["lines", "pattern", "whole"] },
      "start": { "type": "integer", "minimum": 1 },
      "end": { "type": "integer", "minimum": 1 },
      "pattern": { "type": "string", "description": ".NET regular expression, case-sensitive" },
      "context": { "type": "integer", "minimum": 0, "maximum": 10 }
    },
    "required": ["id", "mode"],
    "additionalProperties": false
  }
}
```

`lines` returns original lines `start` to `end`, each prefixed with its line number and a tab. `pattern` returns every matching line with `context` lines around it, at most 50 matches. For a line over 240 characters it returns a 240-character window around the match. The pattern runs with `RegexOptions.NonBacktracking` and a 250 ms timeout. `whole` returns the header line, a line feed, then the original text exactly, with no line-number prefixes, so an edit can use it; the host passes the result into the `tool_result` unchanged. A `lines` or `pattern` result is capped at 3,000 estimated tokens and says where it stopped. The first line of every result names the id, the mode and the original's total line count.

Errors are tool results with `is_error: true` and one code: `not_kept` (unknown id, evicted, or never stored; the model is told to re-run the tool that produced it), `bad_range` (with the total line count), `bad_pattern`, `bad_input` (schema violation) or `limit_reached`. Zero matches is a normal result, not an error.

### The in-request loop

The host owns the loop. Core owns the pure parts: building the preview, parsing a retrieval call, answering it from an original, and the loop's state machine (Forwarding, HoldingTools, Retrieving, Done, Failed), whose declared transitions are the only way it moves.

When an upstream response calls `proxy_retrieve`, the host answers every such call in that message in one round and sends a follow-up upstream with the caller's own headers. A stateless follow-up is the forwarded request plus the assistant message exactly as received (thinking blocks and signatures included) plus a user message with the tool results. A thread follow-up is a `continue` from the response's message id carrying only the tool results. The original is read from the current request when it is there, which it always is for a stateless client, and from the store otherwise.

If the same message also calls client tools, those are not passed to the client. Each gets a tool result `is_error: true` saying it was not run and should be issued again after the retrieved text is read. The model re-issues them in the next response, which the client receives. This needs no state across client requests.

The loop answers at most three rounds. A fourth call gets `limit_reached`. If the model still calls after that, the client gets an error. That bounds a client request at five upstream calls.

Non-streaming: the client gets one message. Its content is the concatenated content of every upstream response with the `proxy_retrieve` blocks and their deferred client calls removed. Its id and `stop_reason` are the last response's. Its input and cache usage fields are the last call's and its output tokens are the sum (assumption; all calls are recorded in the capture).

Streaming: `message_start`, thinking and text blocks pass through as they arrive, with block indexes renumbered. `tool_use` blocks are held until the message ends, so a client call is never shown before the proxy knows whether the message also retrieves. Without a retrieval call, the held blocks, `message_delta` and `message_stop` are released unchanged. With one, the host sends `ping` events while the follow-up runs, drops the follow-up's `message_start`, appends its blocks, and closes with one `message_delta` and `message_stop`. An upstream error during a follow-up becomes an `error` event, and the client retries the turn as it would any upstream error.

Thread streaming needs one more step. The client has already seen the first message id, but the thread now continues from the last one. The host records the pair in the thread ledger before it releases `message_stop`. When that client's next `continue` names the first id, the host rewrites `previous_message_id` to the last. If the ledger write fails, the client gets an `error` event instead of a message it could not continue from. The semantics of `previous_message_id` across a hidden turn are an assumption. A first captured Claude Code session through the loop must show a continuation that works before the thread path is enabled.

### Cache placement

The tool definition goes last in `tools`, with no cache marker. The proxy adds it to every request that already defines tools, whether or not that request holds a preview, so `tools` never changes within a conversation. The tool costs 147 estimated tokens. A new conversation pays about 184 token-equivalents to write it once (1.25x) and about 7 per later turn to read it at Opus 5.5's 0.05x, or 15 at 0.1x (cache strategies F3). Over a 167-turn thread that is about 1,400 token-equivalents, which one 3,000-token preview repays on its first send. Injecting only when a preview first appears would instead change `tools` mid-conversation and rewrite the whole cached prefix once: 1.15T to 1.2T, or roughly 94,000 token-equivalents on the 81,572-token prefix the billing record shows.

For a thread client the tool can only go on `create`, because continuations carry no tools. The proxy previews in a `continue` only when the thread ledger shows the thread was created with the tool. A thread the ledger does not know, including one started before CCR was enabled, gets no previews. Previews in a thread go only into new blocks, which are always after the last breakpoint, and are sent once.

For a stateless client every tool result is re-previewed on every send, identically, so the transcript stays prefix-stable (cache strategies F12). The hidden retrieval turn is not spliced back into later requests. The client re-sends the merged assistant message without it, the prefix still matches up to the preview, and only the tail is written, as it would be anyway. The retrieved slice is gone from history and the model can fetch it again.

The other providers, for when their loops exist. OpenAI appends to `tools` and adds no `prompt_cache_breakpoint`, paying a one-time 1.25x write on GPT-5.6 and later and none before it. Gemini appends a function declaration at the end of `tools` (assumption: the API accepts it next to the client's tools), with no write fee, and requests that use `cachedContent` get no previews, because their tools are immutable in the cache object. OpenRouter follows the upstream named by the model prefix; tools are not in its sticky-routing hash, so injection does not move routing. Router and unknown models get the fallback, which means no CCR.

Enabling or disabling CCR changes `tools` and is a cache event for every conversation in flight, like a preview version change.

### Storage and eviction

Originals live in a content-addressed table in the content database, keyed by the preview id: id, text, kind, line count, created time. A second table, the thread ledger, holds the response message ids of threads created with the tool, plus the first-to-last id pairs. Core declares the port (`IOriginalStore`: keep, find, plus the ledger reads and writes) and `AiProxy.Storage` implements it. Core has no SQLite.

A thread original is written directly in the request path, `INSERT OR IGNORE` with the 1-second request timeout, not through the queue. That one commit costs what a queue publish costs and is readable at once, with no 64-row queue scan. The preview is published only after that write returns. If the write or the ledger lookup fails, the region is forwarded as its reducer left it, with no hint.

A stateless request never depends on the store. Its originals are in the request, so retrieval is answered from memory and the preview is the same whatever the store's state. The host still writes them best-effort for diagnostics. This is how both rules hold together: no hint without a readable original, and no byte that depends on store state.

Originals and ledger rows are deleted 14 days after they are first kept. A read does not extend that (assumption). After eviction a retrieval returns `not_kept`. The existing `/content/{requestId}` endpoint and `content_request` record are left as they are.

### Thread continuation and re-sent previews

The client never sees a preview. The proxy rewrites requests, not what the client holds. A thread client never re-sends a block, so a preview it caused stays server-side as it was first sent. A stateless client re-sends the original, and the proxy derives the same preview bytes from it. A region that already contains a `[proxy preview` marker, from an echo or another proxy, is forwarded unchanged and never previewed again or expanded.

### Activation gate

CCR stays off until a retrieval-aware re-score passes. The reader is `claude-opus-5-5` through `claude -p` (owner D3), with `proxy_retrieve` semantics, on the 13 scored payloads plus at least two over 3,000 tokens per kind. A kind is enabled only when it keeps every answer with retrieval allowed, retrieves on at most 25% of reader calls, and sends at most 80% of its original tokens net. Net means preview plus fetched slices plus the definition charged at full price per request, as the harness does today. A kind that fails stays off in `CompressionOptions`.

## Consequences

Every tools-bearing Anthropic request carries 147 more tokens, mostly as cache reads. A conversation in flight when CCR is switched on pays one full prefix rewrite. Tool calls in a streamed response reach the client only when the message ends, which delays their display but not their execution. Mixed messages cost the model's re-emitted tool input in output tokens. A retrieval round adds the 1.5 to 3.8 s the scoring run measured per extra call.

The proxy takes over the response for every loop-eligible request, which Titanium does not do today. Whether Titanium can write a host-produced stream is unverified and is the first implementation spike. Thread support adds a small ledger write on every thread turn. And the design rests on two assumptions it cannot test without a live session: how `previous_message_id` behaves across a hidden turn, and whether Anthropic accepts a stateless re-send whose assistant message merges thinking blocks from two upstream responses.

This ADR amends two rules of the cache strategies record for this one tool. The proxy appends one tool definition, the same bytes from the first request of a conversation, and it adds hidden upstream turns that the client never re-sends. Both changes stay inside the record's own cost reasoning (F11). Every other rule there holds.

Later regions (system text, user text) can reuse the preview, the tool and the store unchanged, because none of them assumes the region is a tool result beyond the finder that selects it.

The simpler shape is one tool, one content-addressed table and a thread ledger, one loop in the host for one provider, and three retrieval modes, with no splicing of hidden turns, no per-conversation tool state for stateless clients, and no other provider until this one passes its gate.

## Sources

- [rtk-proxy CCR threshold and chaining](../work/2026-10-23-rtk-proxy-ccr-threshold-and-chaining.md), F1 to F4 and F8
- [Reducer scoring results](../work/2026-10-24-reducer-scoring-results.md), F6 and the amendments "sizes in tokens" and "rtk CCR with retrieval allowed"
- [Provider cache strategies](../work/2026-10-24-provider-cache-strategies.md), F1 to F3, F11 to F13, as corrected by PR #62
- [Thread cache billing](../work/2026-10-24-thread-cache-billing.md), F1, F2 and F4
- [Capture first look](../work/2026-10-23-capture-first-look.md), F1 and F4
- [C# reducer activation](../work/2026-10-24-csharp-reducer-activation.md)
- [Retrieval activation owner rulings](../work/2026-10-24-retrieval-activation-gates-feedback.md), D1 to D6
- [Content compression pipeline](../work/2026-10-22-content-compression-pipeline-architecture.md); ADRs [0001](0001-core-holds-the-rules-and-the-host-holds-the-http.md) and [0002](0002-the-store-is-an-adapter-and-sqlite-is-the-queue.md)
- Code: `src/AiProxy.Core/Content/ToolOutputRegionFinder.cs`, `MustKeepOverlay.cs`, `CompressionOptions.cs`, `ContentCompressionPipeline.cs`; `src/AiProxy.Storage/SqliteContentStore.cs`; `src/AiProxy/Hosting/ProviderRequestInspector.cs`
