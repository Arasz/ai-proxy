# CCR runtime: implementation plan

Date: 2026-10-25. Task `aip-ccr-runtime-retrieval-loop-default`. Implements the `ccr-implement` step of
[ADR 0005](../adr/0005-tool-result-previews-with-in-request-retrieval.md) and turns CCR on by default.

## Evidence this plan rests on

- Titanium 7.0.26 exposes `SessionEventArgs.RespondStreaming(Response, Func<Stream, CancellationToken, Task>, bool)`
  (reflected from the built assembly, 2026-10-25). The proxy can answer a request with a body it writes itself,
  so the ADR's "first spike" is replaced by the integration tests below.
- `~/.ai-proxy/capture.db`, `/v1/messages` (read 2026-10-25): 1,729 thread `continue` + 95 thread `create`
  requests, all streamed; 271 stateless streamed requests that carry tools and tool results (Claude Code
  subagents, 9-13 tools, up to 1.6 MB). No non-streamed request carries tools. Both the thread and the
  stateless path are needed; a non-streamed path is not.
- The running proxy has only `PROXY__PORT` and `PROXY__CAPTURE` set, so compression is off today.

## Scope

In: Anthropic `/v1/messages`, streamed, thread and stateless. Out: non-streamed requests (no CCR, no
injection), other providers, `UnknownProviders` (setting is bound, no effect yet), the per-kind activation
gate (every kind previews; the owner turns CCR on to gather the data the gate needs).

## Shape

Core (pure, BCL only):

1. `CcrRequest` — reads `stream`, `thread.type`, `thread.previous_message_id`, presence of `tools` from the
   body; appends the `proxy_retrieve` definition as the last tool by a byte splice (no re-serialisation).
2. `ContentCompressionPipeline` CCR stage — `CompressionRequest.Ccr` is `Off | Thread | Stateless`. After a
   region's reducers, when the reduced text is >= 3,000 estimated tokens, build the preview from the
   region's original. Thread: keep the original through `IOriginalStore` first (1 s); on failure the region
   keeps its reducer output and no hint. Stateless: no store write. `CompressionResult.Originals` returns
   id -> text for the request.
3. `IOriginalStore` port: `KeepAsync`, `FindAsync`, `FindThreadAsync(messageId) -> successor?`,
   `LinkThreadAsync(first, last)`.
4. Stream loop parts: `SseEvent` + reader; `RoundCollector` (one upstream response: passes text/thinking
   events through with renumbered indexes, holds `tool_use` blocks, rebuilds the assistant content);
   `RetrievalLoop` state machine (Forwarding, HoldingTools, Retrieving, Done, Failed); follow-up builders
   (thread: `continue` from the round's message id with tool results only; stateless: forwarded body +
   assistant message as received + tool results). Client calls in a retrieving message get `is_error`
   (O7 A). Three answered rounds, a fourth gets `limit_reached`, a fifth call is an `error` event.

Storage: `ccr_original(id, text, kind, line_count, created_at)` and `ccr_thread(message_id, successor_id,
created_at)` in the content database, `INSERT OR IGNORE` / upsert, 14-day eviction by the worker.

Host:

5. `ProviderRequestInspector` decides eligibility (CCR on, Anthropic, streamed, `create`+tools / known CCR
   thread `continue` / stateless+tools), runs the pipeline with the mode, injects the tool (create,
   stateless), rewrites `previous_message_id` (and `diagnostics.previous_message_id`) to the ledger's
   successor, then hands the exchange to `RetrievalExchange`.
6. `RetrievalExchange` sends the request upstream itself (`HttpClient`, no proxy, caller headers minus
   hop-by-hop and `Accept-Encoding`), and answers the client with `RespondStreaming`: non-200 or non-SSE
   bodies are copied through; SSE runs the loop, sends `ping` while a follow-up is pending, writes the ledger
   pair before `message_stop`, tees client bytes into the capture buffer, and logs each retrieval.
7. Configuration: `Proxy:Ccr:Enabled` (default **true**), `Proxy:Ccr:UnknownProviders` (false);
   `Proxy:Compression:Enabled` default **true** (owner goal 2026-10-25: CCR with minimizers on by default).

## Steps and gates

| Step | Acceptance | Gate |
|---|---|---|
| S1 Core request + injection | tool appended last, all other bytes identical; no tools -> unchanged | unit tests |
| S2 Pipeline CCR stage + port | large region previewed only when mode on; thread keep failure -> no hint; originals returned | pipeline unit tests |
| S3 SQLite originals + ledger | keep/find round trip, ledger link/find, eviction | storage tests |
| S4 Stream loop (Core) | no-retrieval passthrough identical events; retrieval merges rounds, drops `proxy_retrieve` and deferred client calls, sums output tokens; limit after 3 rounds | unit tests on recorded SSE |
| S5 Host exchange | through the real proxy against a fake SSE upstream: stateless round trip, thread `create` then `continue` rewritten to the last id, non-eligible request untouched, upstream error passed through | integration tests |
| S6 Defaults + docs | CCR and compression on by default; VERSION 0.10.0, changelog, ADR status note | build, review |

S1-S4 are independent; S5 depends on all; S6 last.
