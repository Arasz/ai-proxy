# environment variables

The proxy is configured through the environment, and the credentials live there and nowhere
else. Nothing in this list is written into a tracked file, and nothing in it is logged.

## Provider upstreams

The upstream a provider's traffic goes to. A dialect with no upstream is refused rather than
guessed at, so a missing variable shows up as a refusal in the log, not as a request sent to
the wrong host.

| Variable | What it is |
|---|---|
| `PROXY_ANTHROPIC_UPSTREAM` | the Anthropic upstream, for example `https://api.anthropic.com` |
| `PROXY_OPENAI_UPSTREAM` | the OpenAI upstream, for example `https://api.openai.com` |
| `PROXY_GEMINI_UPSTREAM` | the Gemini upstream, for example `https://generativelanguage.googleapis.com` |

The upstream is a base address. The caller's path and query string travel with the request,
so `/v1/messages` reaches the Anthropic upstream as `/v1/messages`.

## The proxy's own credentials

A request that names its provider by its body or its headers does not get the caller's
credential. It gets the proxy's own, and if the proxy holds none for that dialect the
request is refused with `routing_credential_ambiguous`.

| Variable | What it is |
|---|---|
| `PROXY_ANTHROPIC_KEY` | the proxy's own Anthropic credential, sent as `x-api-key` |
| `PROXY_OPENAI_KEY` | the proxy's own OpenAI credential, sent as `Authorization: Bearer` |
| `PROXY_GEMINI_KEY` | the proxy's own Gemini credential, sent as `x-goog-api-key` |

A request that names its provider by its path keeps the caller's own credential, so these
variables matter only for the generic-path case.

## Pass-through rules

Traffic that is not provider traffic goes to the upstream the operator named for its path
prefix, unchanged. The value is a comma-separated list of `prefix=upstream` pairs.

| Variable | What it is |
|---|---|
| `PROXY_PASSTHROUGH` | for example `/mcp=http://127.0.0.1:7801,/other=http://127.0.0.1:7802` |

A prefix matches whole path segments, so `/mcp` covers `/mcp` and `/mcp/messages` but not
`/mcpx`. A prefix that does not start with `/`, or a pair without an `=`, stops the proxy at
startup rather than at the first request.

## The content compression stage

The stage reduces the tool output in a request before the provider sees it, and keeps what
it dropped. It is off until the operator turns it on, because the stage changes what the
provider is sent.

| Variable | What it is |
|---|---|
| `PROXY_COMPRESSION` | `on` turns the stage on. Anything else, including nothing, leaves it off. |
| `PROXY_COMPRESSION_DB` | where the store keeps what the proxy compressed. The default is `~/.ai-proxy/content.db`. |
| `PROXY_COMPRESSION_MINIMUM` | the smallest part that is compressed, for every kind. Without it, each kind has its own: 200 characters for a log, 100 for JSON, 512 for prose, and a directory listing is never compressed. |
| `PROXY_COMPRESSION_MODEL` | the path to the extractive model's artifact. The model is not in this release, and the rule-based path runs until it is. |

The original of every request the stage changes is written to the database before the
request is forwarded, and the response carries the id it is kept under in the
`x-ai-proxy-content-id` header. The original is served back from `/content/{id}`.

The database is the proxy's own, and it holds the text an agent sent. It is not a secret
store and it is not a cache of credentials, but it does hold whatever the tools returned,
so it belongs on the machine the proxy runs on.

## Reading a request

| Setting | What it is |
|---|---|
| `ASPNETCORE_URLS` | where the proxy listens. It binds loopback by default, and `SECURITY.md` says why it is not a knob to open casually. |
| | The proxy reads a request body of at most 8 MiB to look at it, and refuses a larger one rather than forwarding a body it never read. The limit is a constant in this phase, not a variable. |
