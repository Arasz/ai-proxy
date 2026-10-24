# Research: how each provider keys its prompt cache, and what the compressor may rewrite

**Date:** 2026-10-24
**Question:** For each host in `ProviderHostCatalog` (Anthropic, OpenAI with its regional hosts, Gemini, OpenRouter), how is the prompt cache keyed, priced and invalidated, and from that, which parts of a request may the compressor rewrite, when, and may the proxy place or keep cache markers?

Research half of plan step `cache-strategies` (plan `aip-whole-request-compression-and-retrieval`). No code changes. Every provider page below was fetched on 2026-10-24 with the tinyfish `fetch_content` tool, once each, and none was denied. "Access date" in every evidence line therefore means 2026-10-24. Model names and prices are quoted as the pages print them today; they move often, so treat the prices as a snapshot. The measured side is `docs/work/2026-10-24-thread-cache-billing.md` (called the billing record below).

## Findings

### F1 — Anthropic keys the cache on a cumulative prefix hash written only at a breakpoint [READ]

A breakpoint (`cache_control` on a block, or one top-level `cache_control` that lands on the last cacheable block) writes exactly one entry: a hash of everything from the start of `tools`, then `system`, then `messages`, up to and including that block. A read looks for an entry at the breakpoint and, failing that, walks backward at most 20 block positions (a run of consecutive `tool_use` blocks counts as one position, as does a run of `tool_result` blocks) looking for an entry some earlier request wrote. Up to 4 explicit breakpoints; the top-level form uses one of the 4 slots. If the last block carries an explicit `cache_control` with a different TTL than the top-level one, the API returns 400; a fifth breakpoint also returns 400. Hits need "100 percent identical prompt segments, including all text and images up to and including the block marked". Caches are isolated per organization and, on the Claude API, per workspace.

What invalidates it: any change at or before the breakpoint changes the hash. Tool definitions invalidate everything. Web search or citations toggles, speed setting, thinking parameters and `output_config.effort` invalidate system and/or messages depending on the model. `tool_choice` and images invalidate messages only. Key order inside `tool_use` input matters (the page warns that Go and Swift randomize it).

**Evidence:** https://platform.claude.com/docs/en/build-with-claude/prompt-caching, sections "How automatic prefix checking works", "Automatic caching / Edge cases", "What invalidates the cache", "Cache storage and sharing", "Troubleshooting". Accessed 2026-10-24.

### F2 — Anthropic minimum length, lifetime and usage fields [READ]

Minimum cacheable prompt: 512 tokens for Fable 5.1, Mythos 5.1, Opus 5.5, Opus 5, Sonnet 5.5, Fable 5, Mythos 5 and Haiku 5.5; 1,024 for Opus 4.8, Sonnet 5 and Sonnet 4.x; 2,048 for Opus 4.7; 4,096 for Opus 4.5/4.6 and Haiku 4.5. A shorter prompt is processed uncached with no error. Lifetime: 5 minutes by default, refreshed at no charge on every hit, or 1 hour with `"ttl": "1h"` (longer TTL entries must come before shorter ones in one request). The lifetime counts from the start of the request that wrote or read the entry. An entry becomes readable only once the first response starts, so parallel requests do not share a fresh write.

Hits are reported in `usage.cache_read_input_tokens`, writes in `usage.cache_creation_input_tokens` (and in the `cache_creation` object as `ephemeral_5m_input_tokens` and `ephemeral_1h_input_tokens`), and `input_tokens` counts only the tokens after the last breakpoint. The page also names a cache diagnostics feature that compares consecutive requests and reports where they diverged; its fields were not read.

**Evidence:** same URL, sections "Cache limitations", "How prompt caching works", "Tracking cache performance", "1-hour cache duration". Accessed 2026-10-24.

### F3 — Anthropic prices: write 1.25x (5 min) or 2x (1 h), read 0.1x, and 0.05x on the 5.5 models [READ]

From the pricing table on the same page, per million tokens. Sonnet 5.5: input $2, 5-minute write $2.50 (1.25x), 1-hour write $4 (2x), read $0.10 (0.05x). Opus 5.5: $4, $5, $8, read $0.20 (0.05x). Opus 5: $5, $6.25, $10, read $0.50 (0.1x). Haiku 5.5 at or under 100k-token prompts: $0.10, $0.125, $0.20, read $0.01 (0.1x). Fable 5.1 and Mythos 5.1 read at 0.025x. Sonnet 5 reads at $0.20 on a $2 input (0.1x). The page states the rule outright: reads and refreshes cost 0.1x of base input, 0.05x on Opus 5.5 and Sonnet 5.5, 0.025x on Fable 5.1 and Mythos 5.1. Breakpoints themselves cost nothing.

**Evidence:** same URL, section "Pricing" and footnotes 1 and 2, and "Understanding cache breakpoint costs". Accessed 2026-10-24.

### F4 — OpenAI keys the cache on the rendered prefix, with implicit or explicit breakpoints on GPT-5.6 and later [READ]

Reuse needs the whole rendered prefix to match, and the rendered context includes hidden OpenAI system content, tool definitions, developer messages and history. Settings that alter the rendering also break it: `model`, `tools` (names, descriptions, schemas, order), `parallel_tool_calls`, `text.format`, `reasoning.effort`, `text.verbosity`, and `context_management` (compaction). On GPT-5.6 and later, `prompt_cache_options.mode` is `implicit` (a breakpoint at the end of the latest eligible message, plus up to 20 earlier message endings as lookup candidates) or `explicit` (only blocks marked `prompt_cache_breakpoint`; with none marked, the request does no caching and no write charge). Up to 4 cache writes per request. Lookup boundaries are the first 2 and latest 50 explicit breakpoints plus, in implicit mode, the implicit one.

Two gotchas matter to a rewriter. Extending an existing message (Content A becomes A + B) moves the old endpoint inside a message, so the old prefix is no longer a lookup boundary; the page says to append a new message instead. And switching a conversation from implicit to explicit mode between requests drops the earlier implicit entry. Routing: a hash of the initial tokens after the hidden content (tools included), plus `prompt_cache_key`. On models before GPT-5.6 a stable `prompt_cache_key` is advised; on 5.6 and later it is optional and only separates accounting. Caches are not shared across organizations or across regional processing boundaries; cached state lives on single machines and traffic above about 15 requests a minute per prefix can overflow to a cold machine.

**Evidence:** https://developers.openai.com/api/docs/guides/prompt-caching (redirect target of platform.openai.com/docs/guides/prompt-caching), sections "What is the prompt cache", "How caching works", "How prefix matching works", "Cache location", "Gotchas". Accessed 2026-10-24.

### F5 — OpenAI minimum, lifetime, usage fields and prices [READ]

Minimum: 1,024 visible input tokens on GPT-5.6 and later; "varies by request settings" before that (tools, images, schemas and reasoning effort all move it, so no single number exists for older models). Lifetime on 5.6 and later: `prompt_cache_options.ttl` of `30m` (default, minimum lifetime) or `12h` in the Responses API, overridable per breakpoint; a read refreshes at least 30 minutes. Older models use `prompt_cache_retention` (`in_memory` about 5 to 10 minutes idle, up to an hour; `24h` extended). Usage: `usage.input_tokens_details.cached_tokens` (reads), `cache_write_tokens` and, with two lifetimes, `cache_write_tokens_details.ttl_30m_tokens` and `ttl_12h_tokens`. Chat Completions uses `prompt_tokens_details` with the same names.

Prices from the pricing page, per million tokens. gpt-6.1-sol: input $2.00, cached input $0.10 (0.05x), 30-minute write $2.50 (1.25x), 12-hour write $5.00 (2.5x). gpt-6-astra: $10.00, $1.00 (0.1x), $12.50, $25.00. gpt-6-luna: $0.10, $0.01 (0.1x), $0.125, $0.25. The caching page says 1.25x and 2.5x writes, 0.1x reads on most GPT-5.6-and-later models and 0.05x on GPT-6.1 Sol. Models before 5.6 have "no additional cache-write charge" and a model-dependent cached-input rate; the rate was not read for any of them. Regional (data residency) endpoints cost 10% more for models released on or after 2026-03-05.

**Evidence:** https://developers.openai.com/api/docs/guides/prompt-caching ("Cache lifetime", "Summary of model differences", "How to optimize prompt caching"); https://developers.openai.com/api/docs/pricing ("Flagship models", regional note). Accessed 2026-10-24.

### F6 — Gemini caches implicitly by prefix, and explicit `cachedContents` objects are a separate path [READ]

Implicit caching is on by default for Gemini 2.5 and newer. The page's advice is to put large common content at the start and send similar prefixes close in time, so the key is a prefix match. Minimum input: 4,096 tokens for Gemini 4 Argon, 3.8 Flash, 3.6 Flash and 3.1 Pro Preview; 2,048 for 2.5 Flash and 2.5 Pro. Hits are reported in `usage.total_cached_tokens` on the page (Interactions API wording). On `generateContent`, a forum report quotes `usageMetadata.cachedContentTokenCount`; that is one user's post, not the docs.

Explicit caching creates a `CachedContent` resource (`POST /v1beta/cachedContents`) holding `contents`, `tools`, `systemInstruction` and `toolConfig`, all marked immutable, bound to one model, with an `expireTime` or `ttl` and a patch method that updates only the expiration. Later `generateContent` calls pass `cachedContent: "cachedContents/<id>"` and only the new contents. Gemini 4 Argon supports implicit caching only; the pricing page says so and a forum post shows `cachedContents` returning 400 "Explicit caching is not supported". The same post reports no implicit hits on Argon in its test, which contradicts the page's claim that Argon is cached implicitly (see F10).

**Evidence:** https://ai.google.dev/gemini-api/docs/caching ("Implicit caching" table, last updated 2026-10-23); https://ai.google.dev/api/caching (CachedContent resource, last updated 2026-10-13); https://ai.google.dev/gemini-api/docs/pricing (Gemini 4 Argon, "Implicit caching only"); https://discuss.ai.google.dev/t/gemini-4-argon-returns-no-context-cache-hits-explicit-caching-returns-400-gemini-3-7-flash-caches-the-same-prefix/189019. Accessed 2026-10-24.

### F7 — Gemini prices: implicit reads at 0.05x to 0.1x with no write fee, explicit adds hourly storage [READ]

Context caching price against input price, per million tokens, as the pricing page prints them. Gemini 4 Argon: $0.10 against $2.00 (0.05x) through 2026-11-30, then $0.20 against $4.00 (0.05x); implicit only, so no storage fee. Gemini 3.8 Flash: $0.075 against $0.75 (0.1x) through 2026-12-31, then $0.15 against $1.50 (0.1x), plus storage of $0.50 then $1.00 per million tokens per hour. Gemini 2.5 Flash: $0.03 against $0.30 (0.1x) plus $1.00 per million tokens per hour storage. The page lists no write surcharge for implicit caching. The default TTL of an explicit `CachedContent` and the implicit entry lifetime are not stated on any page fetched.

**Evidence:** https://ai.google.dev/gemini-api/docs/pricing, rows "Context caching price" under Gemini 4 Argon, 3.8 Flash and 2.5 Flash, "Standard" tier. Accessed 2026-10-24. Ratios are my division of two printed numbers.

### F8 — OpenRouter forwards each upstream's rules and adds sticky routing keyed by `session_id`, `prompt_cache_key` or the opening messages [READ]

Caching behavior depends on the model's upstream. Anthropic models accept `cache_control` (top-level or per block, TTL `5m` or `1h`), OpenAI GPT-5.6 and later accept `prompt_cache_breakpoint` and `prompt_cache_options`, and the two marker styles are translated into each other when a request is routed across (TTLs are dropped in translation). Gemini needs `cache_control` breakpoints and OpenRouter uses only the last one; the Gemini `systemInstruction` is treated as immutable, so uncached dynamic text after a cached block in the first system message is not preserved. Some upstreams (Grok, Moonshot, DeepSeek, Z.AI, Groq) cache automatically.

Sticky routing sends later requests for the same model to the same provider endpoint so the cache stays warm. The routing key is `session_id` (body field or `x-session-id` header), else `prompt_cache_key`, else a hash of the first system or developer message and the first non-system message. Sessions expire after 10 minutes idle. Usage is `usage.prompt_tokens_details.cached_tokens` and `cache_write_tokens`; Anthropic reads are billed at 0.1x, 5-minute writes 1.25x, 1-hour writes 2x; OpenAI GPT-5.6 and later writes 1.25x (30m) and 2.5x (12h); Gemini reads 0.25x with a 5-minute write TTL and 4,096 or 1,024 token minimums depending on model.

**Evidence:** https://openrouter.ai/docs/guides/best-practices/prompt-caching, sections "Provider Sticky Routing", "OpenAI", "Anthropic Claude", "Google Gemini"; https://openrouter.ai/docs/api-reference/overview (usage object). Accessed 2026-10-24.

### F9 — Regional OpenAI hosts add only a cache boundary and a 10% uplift; `eu.openrouter.ai` was not checked [READ]

All ten `*.api.openai.com` regional hosts share the OpenAI request shape and caching rules. The one difference the docs state is that caches do not cross regional processing boundaries and regional endpoints carry a 10% price uplift (F4, F5). `generativelanguage.googleapis.com` is the only Gemini host. For `eu.openrouter.ai` no page was fetched, so whether its sticky routing state or cache is shared with `openrouter.ai` is UNVERIFIED. The proxy needs no per-region strategy: the OpenAI strategy applies, and a region never shares a cache entry with another, so decisions must not assume one.

**Evidence:** `src/AiProxy.Core/Providers/ProviderHostCatalog.cs:11-27` (host list); OpenAI page "Cache location" and pricing page regional note, accessed 2026-10-24.

### F10 — Where the docs and our measurements agree, and the one place they conflict [INFERRED]

Agreement. The billing record's F1 identity (cache read at turn n equals cache read plus cache creation at turn n-1, to the token, on 509 of 665 turns) is exactly the multi-turn table on the Anthropic page: request 2 reads "System through User(2)" and writes "Asst(2) + User(3)". Its "uncached input is 2 to 4 tokens at the median" matches the page's `input_tokens` definition (tokens after the last breakpoint). Its F4, stateless re-sends reading a cached prefix and writing the new tail, matches the same mechanism, and its warning that a non-identical rewrite turns reads into writes is the page's "common mistake" example.

Conflict. The billing record's F3 found that block 0 of the system prompt, a billing header with a per-request suffix and no `cache_control`, changes on almost every turn, yet the cache still reads the whole history. F1 above says the hash is cumulative over every block up to the breakpoint, so a changed block 0 should miss every time. Both are read or measured, and they cannot both describe the same bytes. The likeliest reconciliation is that the server drops or normalizes that header before hashing; I did not find that stated in any fetched page. Until someone confirms it, the proxy must treat "a changing block 0 is harmless" as true for this one header on Claude Code traffic and nowhere else, and must never rely on it for content it rewrites.

Second conflict, between sources. OpenRouter's page says Anthropic reads cost 0.1x and OpenAI reads cost 0.25x or 0.5x; the vendors' own pages say 0.05x for Opus 5.5 and Sonnet 5.5 and 0.1x or 0.05x for the GPT-6 family. OpenRouter's numbers describe older models or are stale. The vendor page wins for any model the vendor lists. Third, on Gemini the OpenRouter page gives minimums of 1,024 (2.5 Flash) and 4,096 (2.5 Pro), Google's page gives 2,048 for both. Google wins. Fourth, the Argon page says implicit caching works and a forum report measured zero hits on it; the page is the claim, the forum post is one user's test, and neither is ours.

### F11 — The saving from a first-send rewrite is 1.25 tokens of write plus 0.05 to 0.1 tokens per later turn; a rewrite that breaks the prefix costs 1.15 to 1.2 tokens per cached token behind it [INFERRED]

From F3 and the billing record's F1 counts. Over the 478 thread-mode turns, 89,167,411 tokens were read and 1,796,957 written. At a 0.1 read weight and a 1.25 five-minute write weight that is 8.92 million read-equivalents against 2.25 million write-equivalents, reads about 4.0 times the writes. At 0.05 it is 4.46 million against 2.25 million, about 2.0 times. (Claude Code's actual TTL and the model mix in the capture were not checked, so the weights are the 5-minute and the two stated read rates, not a fit.) That settles the open item in the billing record's F2: first-send compression still compounds, but with a read price of 0.05x the compounding is half what a 0.1x read would give, so the case rests more on the write turn and on the context window than on repeated reads.

The risk side. If a rewrite changes a block at position k and T cached tokens sit behind it in a prefix that was being read, the next send re-writes them: cost 1.25T instead of 0.1T (or 0.05T), an extra 1.15T to 1.2T, paid once per break. A rewrite that removes r tokens recovers only about 0.1r to 1.25r per turn, so a single break on a long cached prefix wipes out many turns of savings. That is why the rules below prefer no rewrite over an uncertain one.

### F12 — Strategy per provider: what the compressor may rewrite, where, when, and who owns the markers [INFERRED]

This is a design rule set. It reasons from F1 to F9 and the billing record, and nothing here has been tried against a provider.

Common ground. A "region" is one unit the compressor rewrites on its own: a tool result, a pasted file, a message text block. A region's output is a pure function of its own bytes plus a pinned reducer version. Nothing in the output may depend on the request around it, the time, a counter, a random id, the number of tokens elsewhere, or the state of the retrieval store. Retrieval handles are a hash of the region's original bytes. If the store is unavailable on a later send, the proxy must still emit the same bytes as before (it keeps the decision made on first send, or it fails the request path open by forwarding nothing it has not already forwarded the same way). Changing the reducer version is a cache event and ships as one deliberately. The proxy does not add, remove, move or retime a cache marker; it keeps the client's markers on the same blocks with the same TTL. It also never changes the number or boundaries of blocks or messages.

| Host | May rewrite | Never rewrite | When | Markers |
|---|---|---|---|---|
| `api.anthropic.com` | `tool_result` content and large user text blocks in `messages`, inside the block's own text | `tools`, `system` (billing header included), thinking blocks, `tool_use` input (key order), `cache_control` fields, block or message count | Thread client that sends only new blocks: first send only, and only blocks after the last breakpoint. Stateless client that re-sends history: every send, same bytes each time (prefix-stable) | Preserve exactly. Never add: a different TTL on the last block returns 400 and a fifth breakpoint returns 400 |
| `*.api.openai.com` (all ten regions) | Tool outputs and large input text inside one content block or message | `tools`, `text.format`, `reasoning`, `prompt_cache_key`, `prompt_cache_options`, `prompt_cache_breakpoint`, message order, the initial run of developer messages, message boundaries (do not merge or split) | Same two client shapes as Anthropic. A client using server-held state (previous-response style) sends only new input, so first send only | Preserve. Never add: on GPT-5.6 and later a marker adds a 1.25x write charge the client did not choose |
| `generativelanguage.googleapis.com` | `contents` text parts, per part, deterministic | `systemInstruction`, `tools`, `toolConfig`, the `cachedContent` field, all `cachedContents` endpoints (create, patch, get, delete) | Implicit: as the stateless shape, same bytes each send. Explicit `cachedContent` in use: only the new `contents` after it, first send | Nothing to place or keep in the body for implicit caching. Leave explicit cache objects alone, including their create bodies, because a smaller body could fall under the 2,048 or 4,096 minimum |
| `openrouter.ai`, `eu.openrouter.ai` | Pick the upstream's rules from the `model` prefix (`anthropic/`, `openai/`, `google/`), then apply them | The first system or developer message and the first non-system message (they form the sticky-routing hash), `session_id`, `x-session-id`, `prompt_cache_key`, both marker styles, `provider` ordering | As the matching upstream. An unknown or router model (auto router, `~...-latest`) uses the universal fallback | Preserve both marker styles. Never add. For Gemini upstreams a marker in a non-final position is ignored anyway |

Where relative to the breakpoint. Content before or at the last breakpoint is the cached prefix: it is only ever re-emitted unchanged. Content after the last breakpoint is the tail, which is new on every send. For a thread client the tail is the only place to rewrite, and it gets rewritten once, before it is ever written to a cache, so the cached form is the compressed form from then on. For a stateless client the "prefix" is the client's own re-sent history; the rewrite is allowed there only because it is prefix-stable, meaning the output for the first k regions of a transcript is a byte prefix of the output for all of it.

A compression that takes a prompt below the model's minimum cacheable length (F2, F5, F6) forfeits caching for the request. For prompts that short the loss is small by definition, but the compressor should skip a rewrite whose only effect is to cross that line.

### F13 — The universal fallback for an unrecognised provider [INFERRED]

When the host is not in the catalog, or the host maps to a dialect whose rules this record did not verify (an Anthropic-shaped request on a Bedrock or Vertex host, an OpenAI-shaped request on an unknown gateway, an OpenRouter router model), use these rules, which hold under plain prefix caching and cost nothing under any stricter scheme:

1. Assume the provider caches on an exact byte prefix of the rendered request.
2. Rewrite only whole regions, each as a pure function of its own bytes and a pinned reducer version.
3. Never reorder, insert before, or delete any content that an earlier request in the same conversation already carried. Append-only effects only: a region that was forwarded one way is forwarded the same way again.
4. Never emit anything that varies per request: no timestamp, counter, random id, request-scoped hash, or store state.
5. Do not touch tools, system or developer text, any field whose name mentions cache, session or key, or any header.
6. Selection: the host picks the strategy by exact catalog match; the detected dialect picks the region grammar (which fields hold messages and tool results); when host and dialect disagree, or either is unknown, the fallback applies.

### F14 — Things this record did not check [UNVERIFIED]

The default lifetime of an explicit Gemini `CachedContent` and of an implicit Gemini entry, because no fetched page states either. Whether the server normalizes the Claude Code billing header before hashing (F10). Whether Claude Code requests carry a top-level `cache_control` or only block-level ones (the capture was not re-read for this). The cached-input rate of any OpenAI model before GPT-5.6. Whether `eu.openrouter.ai` shares sticky-routing state with `openrouter.ai`. The Anthropic cache diagnostics fields. Whether OpenAI Responses calls that rely on server-held conversation state bill the earlier turns as cache reads the way Claude Code threads do (the billing record covers Claude Code only).

## Tests that would prove a cache-breaking rewrite is refused

Names only, one list per strategy. Each must be seen failing first against a deliberately broken rewriter, per the `prove-the-check-fails` invariant, and then passing.

Common to all strategies:
- prefix-stable: for a fixture transcript, the rewrite of the first k regions is a byte prefix of the rewrite of all regions, for every k.
- same bytes twice: rewriting the same request twice, and again after a restart of the proxy, gives identical output.
- non-deterministic control: a rewriter that embeds a counter or timestamp in its retrieval hint is caught by the two tests above.
- store down: with the retrieval store unavailable on the second send, the forwarded bytes for regions already forwarded equal the first send.
- reducer version bump is the only thing that changes already-forwarded regions, and it is reported as a cache event.

Anthropic:
- `cache_control` fields (block-level and top-level, including `ttl`) are byte-identical in position and value after the rewrite.
- a request with four breakpoints never gains a fifth, and a top-level marker with a different TTL than the last block is never produced.
- `tools`, `system` (billing header included), thinking blocks and `tool_use` input key order are byte-identical.
- block count and run boundaries of `tool_use` and `tool_result` are unchanged (the 20-position lookback).
- thread `continue` request: every block at or before the last breakpoint is unchanged; only later blocks may differ.
- tail rewritten once: the same tail on the next send, now inside the prefix, comes out byte-identical.

OpenAI (all regional hosts):
- `prompt_cache_key`, `prompt_cache_options`, `prompt_cache_breakpoint`, `tools`, `text.format`, `reasoning`, `parallel_tool_calls` and `context_management` are untouched.
- message count, order and the initial run of developer messages are unchanged, and no message is extended or split.
- no marker is added to a request that had none (the write-charge guard).
- each regional host gets the OpenAI strategy and no state is shared between hosts.

Gemini:
- `cachedContent` and `systemInstruction`/`tools`/`toolConfig` are byte-identical.
- `cachedContents` create, patch, get and delete requests pass through unmodified.
- implicit path: `contents` prefix-stability over a growing conversation.
- a rewrite that would take the prompt under the model minimum is refused.

OpenRouter:
- the first system or developer message and the first non-system message are byte-identical (sticky-routing hash).
- `session_id`, `x-session-id` and `prompt_cache_key` are untouched, and none is added.
- both marker styles survive unchanged; `model` prefix selects the matching upstream strategy; an unknown model prefix selects the fallback.

Fallback:
- unknown host: tools, system and any cache-named, session-named or key-named field are byte-identical.
- the same property tests as the common list, run on an OpenAI-shaped and an Anthropic-shaped request sent to an unlisted host.
- host and dialect disagreement selects the fallback and not either provider's strategy.

## Grade mix

Fourteen findings: 9 READ (F1 to F9), 4 INFERRED (F10 to F13), 1 UNVERIFIED (F14). None MEASURED here; the measured side is the billing record. The strategies are design rules and no provider has been billed against them yet.

## Still open

- A billed active arm: does a prefix-stable rewrite keep a stateless Claude call's cache reads, and does a counter-embedding control show the miss? This is the billing record's open item and needs an owner budget.
- Why a changing block 0 does not break Anthropic's cumulative hash (F10). One billed replay with block 0 held constant against one with it changed would settle it.
- Gemini entry lifetimes and the default `CachedContent` TTL (F14), and whether implicit caching works on Argon at all (F6).
- OpenAI Responses calls with server-held state: are earlier turns billed as reads? Not covered by Claude Code captures.
- Whether the compressor can learn "thread client versus stateless client" from the request alone for non-Claude clients; today only Claude Code's `thread` field is known.
