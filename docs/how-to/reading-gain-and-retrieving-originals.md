# Reading gain and retrieving originals

The `ai-proxy` tool has two client commands besides the proxy itself. `ai-proxy gain` shows how
much the proxy took out of the requests it forwarded. `ai-proxy mcp` is an MCP server whose one
tool, `retrieve_original`, reads back the text behind a `[proxy preview ...]` or `[proxy view ...]`
line. Both talk to the running proxy over its local HTTP endpoint and never open its database.

## The endpoint

The proxy listens for these clients on `http://127.0.0.1:17432`. Set `ASPNETCORE_URLS` (or `--urls`)
to move it; both commands then need `--endpoint` with the same URL. By default the listener is loopback only,
and it answers only requests addressed to `localhost`, `127.0.0.1` or `[::1]`, so a web page cannot reach it
by rebinding a domain name. Set `AllowedHosts` if you move it to another address on purpose.

## See what the proxy saved

```bash
ai-proxy gain                 # summary
ai-proxy gain --daily         # UTC days; --weekly starts on Monday, --monthly, --all
ai-proxy gain --history       # the most recent requests (--limit, 1-1000)
ai-proxy gain --recalls       # what retrievals cost back
ai-proxy gain --format json   # or csv; same numbers as the text view
ai-proxy gain --watch         # live view, Ctrl+C exits
```

Token counts are estimates at 2.89 characters per token. Saved and Net cover the first request only;
the Follow-up column and the billed balance below add what the proxy's own follow-up requests cost.
Character and byte counts are measured. The summary, bucket and attribution tables show the gross
figure and what it cost:

| Column | Meaning |
| --- | --- |
| Saved | Original minus forwarded, without the injected `proxy_retrieve` definition. |
| Retrieved | What `proxy_retrieve` and `retrieve_original` calls read back (listed under `--recalls`). |
| Tool | The `proxy_retrieve` definition the proxy added to requests that carry tools. |
| Follow-up | The body of every follow-up request the proxy sent upstream to answer a `proxy_retrieve` call inside your request. |
| Net | Saved minus retrieved minus tool. Negative when the cost is larger than the saving. |
| Saved % and Net % | Saved and net as a share of the original tokens, to one decimal. |

The summary headers drop the word "tokens"; the bucket and attribution tables still spell it out.
Table numbers under 10,000 are exact, with thousands separators. Larger ones show one decimal of K,
M or B (`523.8K`, `261.2M`), so the summary fits an 80-column terminal and no number splits across
lines. `--format csv` and `--format json` keep exact raw values. The JSON totals add `estimatedToolTokens`, `estimatedNetTokens`, `savedPercent` and
`netPercent`; the CSV adds `estimated_tool_tokens` and `estimated_net_tokens` after `error`.
Version 0.24.0 adds `followUps`, `followUpBytes`, `estimatedFollowUpTokens`, `followUpMeasuredRequests`
and `estimatedBilledBalanceTokens` to the JSON totals (schema version unchanged), and appends
`follow_ups`, `follow_up_bytes`, `estimated_follow_up_tokens` and `estimated_billed_balance_tokens`
to the CSV after `bytes`.

Under the table, `Billed balance (all requests)` is original minus forwarded minus follow-up tokens.
It answers a different question than Net. Net asks what the first request saved once retrievals are
paid for; the balance asks what every request sent upstream cost against sending the originals
untouched. Retrieved is not subtracted from the balance because the retrieved text already travels
inside the follow-up bodies, so subtracting it would count it twice. A stateless request resends
its whole forwarded body with the assistant turn and the tool results on each follow-up, so those
bytes count again, as they are billed. A thread request sends only the results, so its follow-up
is small. The line ends with `follow-ups measured on N of M requests`: requests recorded before
version 0.24.0 carry no follow-up figures and are counted in M but not in N, and a request whose follow-up write failed (logged only) still
counts in N. The balance is an estimate from the sizes sent, not a provider-billed figure, which
is why the Method line still says net billed savings are unavailable; the Follow-up bytes are the exact length of the body sent.

Follow-ups appear in the summary and in the `--by-method`, `--by-rule`, `--by-layer` and `--by-pipeline`
attribution views, under the path of the request that caused them, and follow the project, date
and provider filters. The `--daily`, `--weekly` and `--monthly` buckets list requests only, so
their follow-up figures are zero. A follow-up is stamped with the time its request started, and it
is recorded only after the provider accepts it; a refused or failed follow-up records nothing.
Capture does not need to be on.

A request the proxy inspected but left unchanged still counts, with zero saved (requests it does not
inspect, such as a body over 8 MB or a chunked body, are not counted). A request whose only change is
the `proxy_retrieve` definition is recorded under the rule `ccr.tool`; its tool tokens appear in the
Tool column and its net is negative. Only model calls count in the totals
and percentages: `POST` to `/v1/messages` (Anthropic), the chat completions, completions, responses and
messages endpoints (OpenAI-style hosts, OpenRouter) and `generateContent` / `streamGenerateContent` (Gemini).
Other calls to a provider host, such as Claude Code telemetry, are recorded as **other traffic** and shown
apart: a line `Other traffic: N requests, B bytes` under the table, an `other` row in CSV (the `bytes`
column) and `otherTraffic` (`requests`, `bytes`) at the end of the JSON report. They follow the
same project, date and provider filters and `--reset`. Rows recorded before this change cannot be sorted again
because the path was not kept; telemetry recorded then stays in the totals as `unchanged`, and only new
traffic is separated.

`ai-proxy gain --reset` deletes the statistics that match the other filters, after a confirmation
(`--yes` skips it). Kept originals and captures are not touched.

## Split the numbers by project

The proxy takes a request's project from the `X-Ai-Proxy-Project` header when there is one, and
otherwise from the proxy credential the client sends. `ai-proxy harness` sets that credential for
you: it launches the agent with `HTTP_PROXY`, `HTTPS_PROXY` and their lowercase forms pointing at
`http://ai-proxy:<percent-encoded project path>@<proxy address>` (by default 127.0.0.1:8080), so any agent whose HTTP stack
honours proxy userinfo is attributed to the directory you launched it from. That includes Codex's
built-in provider (measured with Codex 0.166.0) and Claude Code (measured with 2.1.318).

```bash
ai-proxy harness codex -- exec "your prompt"
```

Without the launcher, set the variable yourself with the path percent-encoded (the example uses the default proxy address):

```bash
HTTPS_PROXY="http://ai-proxy:%2FUsers%2Fme%2Fapp@127.0.0.1:8080" codex
```

The user name must be `ai-proxy` and the password is the project path. The proxy never requires
the credential: a client without it is served as before, and a credential with another user name,
a malformed value or an empty password is ignored. The proxy removes `Proxy-Authorization` from
everything it forwards.

The header stays the explicit route and wins over the credential. For Claude Code, start it with

```bash
ANTHROPIC_CUSTOM_HEADERS="X-Ai-Proxy-Project: $PWD" claude
```

For a Codex custom model provider, add `env_http_headers` to the selected provider's existing table
in `~/.codex/config.toml`. Replace `work` below with that provider's ID and keep its connection and
authentication settings:

```toml
[model_providers.work]
env_http_headers = { "X-Ai-Proxy-Project" = "AI_PROXY_CODEX_PROJECT" }
```

`ai-proxy harness codex` exports `AI_PROXY_CODEX_PROJECT` with the current directory; without the
launcher, set it yourself: `AI_PROXY_CODEX_PROJECT="$PWD" codex`. A built-in provider ID (`openai`,
`ollama`, `lmstudio`) cannot be redefined as a custom provider, which is why the credential route
above matters for it.

Codex signed in with ChatGPT talks to `chatgpt.com`. The proxy records it only under
`ai-proxy harness`: it decrypts that host only for a tunnel carrying the harness credential, and
answers Codex's WebSocket upgrade with 426 so the turn goes over HTTPS. Codex run outside the
harness, and WebSocket turns to any host, are not recorded.

Run `ai-proxy gain --project` in the same directory to filter to it (`--project-path <PATH>` names
another). Requests with neither the header nor the credential have no project and show up only in unfiltered views. When the
project has no requests, `gain` prints a hint naming the header and `ai-proxy harness`, which launches
an agent with the credential set.
Statistics start with version 0.15.0; older requests are not backfilled.

## Let an agent read originals back

Register the server with the agent, for example in Claude Code:

```bash
claude mcp add ai-proxy -- ai-proxy mcp
```

The agent then has `retrieve_original(id, mode, ...)`, with the same `whole`, `lines` and `pattern`
modes as `proxy_retrieve`. Most of the time the agent does not need it: the proxy answers
`proxy_retrieve` inside the request. The MCP tool is for reading an id again in a later turn.

It finds an original only when the proxy kept it in its database, which happens for Anthropic
thread requests. A stateless request carries its originals only for that one request, so its ids
come back as `not_kept` afterwards.
