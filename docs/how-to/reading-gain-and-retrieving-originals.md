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

Token counts are estimates at 2.89 characters per token, and they cover the first request only.
Character and byte counts are measured. The summary, bucket and attribution tables show the gross
figure and what it cost:

| Column | Meaning |
| --- | --- |
| Saved tokens | Original minus forwarded, without the injected `proxy_retrieve` definition. |
| Retrieved tokens | What `proxy_retrieve` and `retrieve_original` calls read back (listed under `--recalls`). |
| Tool tokens | The `proxy_retrieve` definition the proxy added to requests that carry tools. |
| Net tokens | Saved minus retrieved minus tool. Negative when the cost is larger than the saving. |
| Saved % and Net % | Saved and net as a share of the original tokens, to one decimal. |

Numbers in the tables carry thousands separators; `--format csv` and `--format json` keep raw
values. The JSON totals add `estimatedToolTokens`, `estimatedNetTokens`, `savedPercent` and
`netPercent`; the CSV adds `estimated_tool_tokens` and `estimated_net_tokens` as its last two columns.

A request the proxy inspected but left unchanged still counts, with zero saved (requests it does not
inspect, such as a body over 8 MB or a chunked body, are not counted). A request whose only change is
the `proxy_retrieve` definition is recorded under the rule `ccr.tool`; its tool tokens appear in the
Tool column and its net is negative. Non-message traffic to a provider host, such as telemetry
batches, is also counted as unchanged and dilutes the percentages.

`ai-proxy gain --reset` deletes the statistics that match the other filters, after a confirmation
(`--yes` skips it). Kept originals and captures are not touched.

## Split the numbers by project

The proxy knows a request's project only when the client sends the `X-Ai-Proxy-Project` header;
the proxy removes it before the request goes upstream. For Claude Code, start it with

```bash
ANTHROPIC_CUSTOM_HEADERS="X-Ai-Proxy-Project: $PWD" claude
```

For Codex, add `env_http_headers` to the selected custom model provider's existing table in
`~/.codex/config.toml`. Replace `work` below with that provider's ID and keep its connection and
authentication settings:

```toml
[model_providers.work]
env_http_headers = { "X-Ai-Proxy-Project" = "AI_PROXY_CODEX_PROJECT" }
```

Then launch Codex from the project directory:

```bash
AI_PROXY_CODEX_PROJECT="$PWD" codex
```

The provider must be selected by `model_provider`. If you use a built-in provider, first define
and select a [custom model provider](https://learn.chatgpt.com/docs/config-file/config-advanced#custom-model-providers);
the built-in IDs `openai`, `ollama` and `lmstudio` are reserved. Keep the provider's upstream base
URL: ai-proxy's port 8080 is a forward proxy, so Codex traffic still needs to follow the proxy
settings and trust its inspection certificate.

The header setting was verified with Codex CLI 0.164.0, a loopback HTTP server and a fake API key.
Live provider authentication and ChatGPT sign-in were outside that check.

Run `ai-proxy gain --project` in the same directory to filter to it (`--project-path <PATH>` names
another). Requests without the header have no project and show up only in unfiltered views. When the
project has no requests, `gain` prints a hint naming the header and `ai-proxy harness`, which launches
an agent with it set.
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
