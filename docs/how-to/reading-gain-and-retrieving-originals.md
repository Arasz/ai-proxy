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
What a retrieval later reads back is listed under `--recalls` and is not netted out of the savings.
Character and byte counts are measured. A request the proxy inspected but left unchanged still counts,
with zero saved (requests it does not inspect, such as a body over 8 MB or a chunked body, are not counted); one that grew (the `proxy_retrieve` tool definition adds a little) shows a negative number.

`ai-proxy gain --reset` deletes the statistics that match the other filters, after a confirmation
(`--yes` skips it). Kept originals and captures are not touched.

## Split the numbers by project

The proxy knows a request's project only when the client sends the `X-Ai-Proxy-Project` header;
the proxy removes it before the request goes upstream. For Claude Code, start it with

```bash
ANTHROPIC_CUSTOM_HEADERS="X-Ai-Proxy-Project: $PWD" claude
```

and then `ai-proxy gain --project` in the same directory filters to it (`--project-path <PATH>` names
another). Requests without the header have no project and show up only in unfiltered views.
Statistics start with this release; older requests are not backfilled.

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
