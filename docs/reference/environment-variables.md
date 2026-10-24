# Environment variables

Provider routing needs no environment configuration. ai-proxy recognizes these destination
hosts and sends all other HTTP traffic through unchanged. HTTPS for other hosts remains an
opaque tunnel.

| Provider | Host |
|---|---|
| OpenAI | `api.openai.com` |
| Anthropic | `api.anthropic.com` |
| Google Gemini | `generativelanguage.googleapis.com` |
| OpenRouter | `openrouter.ai` |

ai-proxy sets the current user's system HTTP and HTTPS proxy to `127.0.0.1:8080` (or the port in `PROXY_PORT`) while it
runs, then restores the prior settings at shutdown. It creates a per-user root certificate
for HTTPS inspection and asks the operating system to trust it. Clients that do not honor
system proxy settings or user-root trust are outside this routing path.

## Compression

Compression stays off unless enabled, because it changes provider request bodies.

| Variable | What it is |
|---|---|
| `PROXY_COMPRESSION` | `on` turns request compression on. |
| `PROXY_COMPRESSION_DB` | Where original request bodies are kept. The default is `~/.ai-proxy/content.db`. |
| `PROXY_COMPRESSION_MINIMUM` | A minimum size applied to every content kind. Without it, logs use 200 characters, JSON uses 100, prose uses 512, and directory listings are left alone. |
| `PROXY_COMPRESSION_MODEL` | Reserved for the extractive model artifact, which is not included in this version. |

The database contains the text tools returned to the agent. Keep it on the machine running
ai-proxy and protect it like other local agent data.

## Request capture

Capture stays off unless enabled. It exists to measure, over real traffic, how a provider request
is made up (system prompt, tool definitions, turns, tool results, thinking blocks).

| Variable | What it is |
|---|---|
| `PROXY_CAPTURE` | `on` or `true` keeps every provider request body the proxy reads, exactly as the client sent it. Anything else, or unset, keeps nothing. |

Each request becomes one row of `capture_request` in `~/.ai-proxy/capture.db`: a request id, the
time, the provider, the host, the path without its query string, and the body as bytes. The body
is forwarded unchanged. With `PROXY_COMPRESSION=on` as well, the row holds the body from before
compression. Bodies over 8 MiB, or sent without a `Content-Length`, are not read and not kept.

Headers and the query string are not stored, so `Authorization`, `x-api-key`, cookies and a
`?key=` parameter stay out of the file. The body itself is stored in full: prompts, source code,
file contents and tool output the agent sent, and any secret that sits inside them. On Linux and
macOS the file is `0600` and its directory `0700`. Nothing removes old rows; delete the file when the
measurement is over. A failed write is logged as a warning and the request goes out regardless.

Each captured request also gets one row of `capture_response`, under the same `request_id`, once
its response has passed through, whatever its status code. The row holds numbers only: the
completion time, the status code, the request body size as sent and as forwarded, the response
body size on the wire, the four token counts the provider reported (`input_tokens`,
`output_tokens`, `cache_creation_input_tokens`, `cache_read_input_tokens`), and `stages`, a JSON
object of how many microseconds each step took. The stages are the proxy's own (`body-read`,
`capture-write`, `compression` and its `compression.*` steps, `set-body`, `usage-read`, and
`inspector`, the whole request handling, which includes the steps before it) and Titanium's
(`titanium.client-request-read`, `titanium.connection-wait`, `titanium.request-send`,
`titanium.ttfb`, `titanium.response-delivery`, `titanium.total`).

Usage is read for Anthropic only: from a JSON message's `usage`, or from an event stream's
`message_start` and final `message_delta`. OpenAI's `prompt_tokens` and `completion_tokens` are not
mapped, because OpenAI counts cached tokens inside its prompt count and Anthropic does not, so an
OpenAI response gets `no-usage`.

The response body is not stored, and neither are response headers. The proxy copies the body as
it streams past to the client, never holding it back, reads the usage once the response is
complete, then lets the copy go. A plain event stream keeps only its first and last 64 KiB, where
the usage is, so a stream of any length is read; any other body, and an event stream that is
gzip, deflate or br encoded, is kept whole up to 4 MiB and decoded up to 16 MiB. When the counts
cannot be read they are empty and `usage_error` says why:

| `usage_error` | Meaning |
|---|---|
| `over-cap` | The body was over 4 MiB as sent, or over 16 MiB decoded. |
| `encoding:<name>` | The body used a content encoding the proxy does not decode. |
| `decode-failed` | The body did not decode, or a gzip, zlib-deflate or br body was cut short. A raw-deflate body carries no trailer, so one cut short is read as far as it goes and usually comes out `malformed`. |
| `malformed` | The body was not JSON, or a stream broke before any usage was read. |
| `no-usage` | The body was readable and carried no Anthropic usage, such as an error response. |
| `truncated` | A stream ended inside an event. The counts read before it are kept, so the output count may be the early one from `message_start`. |

An upstream that drops a stream midway still gets a row, with the bytes that passed and
`truncated` or the usage read so far. The client is left waiting until its own timeout in that
case, with capture off as well; that is Titanium's handling of the dropped connection.

The row is written once the client has the whole response, in Titanium's `AfterResponse` step.
On a keep-alive connection that step can delay the next request on the same connection by one
SQLite insert. A failed response write is logged as a warning and the response is relayed
regardless. A `capture.db` from an earlier version keeps its rows and gains the new table on the
next capture.

## Local health endpoint

`ASPNETCORE_URLS` controls the ASP.NET health and content endpoints (default `http://localhost:5000`).

## Proxy port

| Variable | What it is |
|---|---|
| `PROXY_PORT` | The loopback port the proxy listener binds, and the port written into the system proxy setting. The default is `8080`. |

A value that is not a whole number from 1 to 65535, including an empty value, stops startup with
an error naming `PROXY_PORT`; it never falls back to `8080`. A port another program already holds
also stops startup, before the system proxy setting is changed. Set `PROXY_PORT` to run beside
another proxy that holds `8080`.
