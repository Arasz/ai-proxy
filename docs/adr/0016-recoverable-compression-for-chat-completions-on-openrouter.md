# 0016 - Recoverable compression for Chat Completions on OpenRouter

Status: Accepted (owner rulings 2026-11-20, plan review)

Amends: [ADR 0005](0005-tool-result-previews-with-in-request-retrieval.md), the rule that an
OpenRouter request whose upstream is a router or an unknown model gets the fallback and no CCR
(0005, line 97).

## Context

The pi agent sends Chat Completions requests to `openrouter.ai`. In the gain ledger they hide in
the "codex" row: 221 rows, 56.7M original tokens, all DeepSeek bodies with the tools `read`,
`bash`, `edit` and `message-bus`
([token return record](../work/2026-11-20-token-return-vs-rtk.md) F2). Those bodies carry 39.7%
of their size in tool output, 38.4% in blocks of 300 tokens or more, yet the proxy saved 60K of
56.7M (0.1%), because CCR runs only on streamed Anthropic `/v1/messages` (F4). `CcrPlan` forwards
any other dialect or path without CCR (`src/AiProxy/Ccr/CcrPlan.cs:72`).

Most of the parts already exist. The region finder reads `role: "tool"` message content and
Responses `function_call_output` (`src/AiProxy.Core/Content/ToolOutputRegionFinder.cs:199-200`).
The retrieval loop's state machine (Forwarding, HoldingTools, Retrieving, Done, Failed) lives in
`src/AiProxy.Core/Content/Ccr/Loop/RetrievalLoop.cs:56-63`. What is Anthropic-only is the tool
append (`CcrRequest.AppendTool`, `CcrRequest.cs:78-79`), the SSE round that reads
`content_block` events (`Loop/RetrievalRound.cs`), the follow-up body, and the closing events the
loop writes itself (`RetrievalLoop.cs:137-141` emits `message_stop`). `Proxy:Ccr:UnknownProviders`
is bound but has no effect (`src/AiProxy/Configuration/CcrSettings.cs:13`).

Chat Completions has no thread mode. Every request carries the whole conversation, so the
stateless rules of ADR 0005 apply as they are.

## Decision

### Gate

1. CCR runs on a request when all of these hold: the host is `openrouter.ai`, the method is POST,
   the path ends in `/chat/completions`, `stream` is true, the request defines tools, and the model
   id starts with a known prefix. A known prefix is one whose upstream provider the proxy can name;
   the list lives in one place in Core and starts as `anthropic/`, `openai/`, `google/` and
   `deepseek/`. DeepSeek is on it because it is the only OpenRouter model in the stored bodies
   (221 of 221 rows are `deepseek/deepseek-v4.1-flash-1017`), and its cache matches on the request
   prefix, which an appended tool keeps stable. A router model (`openrouter/auto`) or an unknown
   prefix keeps the ADR 0005 fallback.
2. The mode is always `CcrMode.Stateless`. Originals are read from the request; nothing is kept in
   the store first.
3. `Proxy:Ccr:Enabled` switches this on with the Anthropic path. `Proxy:Ccr:UnknownProviders`
   keeps its meaning and is still without effect.
4. Other OpenAI-dialect hosts (`api.openai.com` and its regional hosts, `eu.openrouter.ai`,
   `chatgpt.com`) keep forwarding without CCR. Each needs its own decision.

### Tool and cache placement

5. `proxy_retrieve` is appended last in `tools` as a Chat Completions function, on every request
   that passes the gate, whether or not it holds a preview, so `tools` never changes within a
   conversation (0005, "Cache placement"). OpenRouter does not hash tools for sticky routing, so the
   append does not move the request to another provider (0005, line 97).

### Streaming rules

6. `tool_calls` deltas are held until the choice's `finish_reason` arrives. Text and reasoning
   deltas pass through as they arrive.
7. Held deltas are joined by their `index` into whole calls before the proxy decides whether the
   message retrieves.
8. Client calls released to the client are renumbered from 0, so the client never sees a gap where
   a `proxy_retrieve` call was.
9. The client gets exactly one `data: [DONE]`, after the last upstream response.
10. Upstream requests carry `stream_options.include_usage: true`. Usage from every round is merged
    into the last chunk. When the client did not ask for usage, the usage chunk is stripped.
11. The follow-up holds one `role: "tool"` message for every `tool_call_id` of the assistant
    message: the retrieval answers, and for a client call made in the same message the ADR 0005
    "not run, issue it again" result (owner ruling O7).
12. Reasoning the upstream returned in the assistant message is echoed back in the follow-up
    unchanged, as thinking blocks are on the Anthropic path.
13. While a follow-up runs, the proxy sends `:` comment lines as keep-alives. An upstream error
    during a follow-up becomes an in-stream error chunk, then `[DONE]`.

### One state machine

14. The Chat Completions loop reuses `RetrievalLoop` and its declared transitions. The parts that
    differ by dialect (reading the stream, holding calls, building the follow-up body, the closing
    events) go behind one round interface that the loop drives. The Anthropic round is the first
    implementation and does not change behaviour. There is no second state machine.

### Forwarded without CCR

15. A request goes upstream unchanged by CCR (reducers still run) when:
    - `n` is greater than 1, because held calls from several choices cannot be merged into one
      follow-up;
    - `tool_choice` is `none`, or names a function other than `proxy_retrieve`, because the model
      could not call it;
    - it is not streamed, has no tools, or fails the host or prefix gate.

## Consequences

Positive: pi traffic on OpenRouter gets previews, and the 38.4% of its body in blocks of 300 tokens
or more becomes reachable. The loop keeps one state machine and one set of transition tests for
both dialects.

Negative: the refactor behind the round interface touches the working Anthropic relay and must
leave it unchanged. Tool-call deltas reach the client only at `finish_reason`, which delays their
display. A request switched on mid-conversation pays one cache write for the changed `tools`.
Mixed tool calls and last-chunk usage on OpenRouter have no fixture yet and need captured streams.

Neutral: with the ADR 0014 amendment of 2026-11-20, these rows show under `openrouter`, not
`codex`. The size and skip numbers of ADR 0015 apply here unchanged.

## Alternatives considered

- Use `Proxy:Ccr:UnknownProviders` for every OpenRouter model: rejected. It would also preview for
  router models whose upstream cache rules are unknown, which ADR 0005 chose to avoid.
- A second state machine for Chat Completions: rejected. Two machines drift, and the transitions
  are the same.
- Non-streamed requests too: deferred. The pi traffic streams, and the Anthropic path is also
  streamed only.
- Wait for Codex compression first: rejected. Codex needs a reversal of ADR 0012 rule 5; this does
  not.

## Sources

- [What raises ai-proxy's token return](../work/2026-11-20-token-return-vs-rtk.md), F2 and F4
- Code: `src/AiProxy/Ccr/CcrPlan.cs`, `src/AiProxy/Configuration/CcrSettings.cs`,
  `src/AiProxy.Core/Content/ToolOutputRegionFinder.cs`, `src/AiProxy.Core/Content/Ccr/CcrRequest.cs`,
  `src/AiProxy.Core/Content/Ccr/Loop/RetrievalLoop.cs`, `src/AiProxy.Core/Providers/ProviderHostCatalog.cs`
