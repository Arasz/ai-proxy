# Environment variables

Settings live in the `Proxy` configuration section, so each is also readable from the host's usual
configuration sources. In the environment, a double underscore stands for the section separator:
`PROXY__COMPRESSION__ENABLED` is the setting `Proxy:Compression:Enabled`. A switch takes `true` or
`false`; `on` is no longer accepted.

An unknown `Proxy:*` key stops startup and the error names it, so a misspelled `PROXY__PROT` or
`--Proxy:Prot=8090` can no longer pass silently. A stray `PROXY__*` environment variable that names
no setting now stops the proxy too.

Provider routing needs no environment configuration. ai-proxy recognizes these destination
hosts and sends all other HTTP traffic through unchanged. HTTPS for other hosts remains an
opaque tunnel.

| Provider | Host | Documented in |
|---|---|---|
| OpenAI | `api.openai.com`, plus the regional `us.`, `eu.`, `gb.`, `ca.`, `au.`, `jp.`, `kr.`, `sg.`, `in.` and `ae.api.openai.com` | [data controls](https://developers.openai.com/api/docs/guides/your-data) |
| Anthropic | `api.anthropic.com` | [data residency](https://platform.claude.com/docs/en/manage-claude/data-residency) |
| Google Gemini | `generativelanguage.googleapis.com` | [API reference](https://ai.google.dev/api) |
| OpenRouter | `openrouter.ai`, `eu.openrouter.ai` | [quickstart](https://openrouter.ai/docs/quickstart) |

Hosts match exactly; there is no subdomain wildcard. A wildcard on `openrouter.ai` would also
decrypt `mcp.openrouter.ai`, which is an MCP server and not the chat API. Anthropic picks a
region with the `inference_geo` request field, so it has no regional host. The list was checked
against each provider's documentation on 2026-10-24.

ai-proxy sets the current user's system HTTP and HTTPS proxy to `127.0.0.1:8080` (or the port in `PROXY__PORT`) while it
runs, then restores the prior settings at shutdown. It creates a per-user root certificate
for HTTPS inspection and asks the operating system to trust it. Clients that do not honor
system proxy settings or user-root trust are outside this routing path.

## Compression

Compression stays off unless enabled, because it changes provider request bodies.

| Variable | What it is |
|---|---|
| `PROXY__COMPRESSION__ENABLED` | `true` turns request compression on. Anything else, or unset, leaves it off. |
| `PROXY__COMPRESSION__DATABASE` | Where original request bodies are kept. The default is `~/.ai-proxy/content.db`. |
| `PROXY__COMPRESSION__MINIMUM` | A minimum size applied to every content kind. Without it, logs use 200 characters, JSON uses 100, prose uses 512, and directory listings are left alone. |
| `PROXY__COMPRESSION__MODEL` | Reserved for the extractive model artifact, which is not included in this version. |

The database contains the text tools returned to the agent. Keep it on the machine running
ai-proxy and protect it like other local agent data.

## Request capture

Capture stays off unless enabled. It exists to measure, over real traffic, how a provider request
is made up (system prompt, tool definitions, turns, tool results, thinking blocks).

| Variable | What it is |
|---|---|
| `PROXY__CAPTURE` | `true` keeps every provider request body the proxy reads, exactly as the client sent it. Anything else, or unset, keeps nothing. |

Each request becomes one row of `capture_request` in `~/.ai-proxy/capture.db`: a request id, the
time, the provider, the host, the path without its query string, and the body as bytes. The body
is forwarded unchanged. With `PROXY__COMPRESSION__ENABLED=true` as well, the row holds the body from before
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
`truncated` or the usage read so far. The client connection is then closed, so the client sees
the failure at once, with capture off as well.

The row is written once the client has the whole response, in Titanium's `AfterResponse` step.
On a keep-alive connection that step can delay the next request on the same connection by one
SQLite insert. A failed response write is logged as a warning and the response is relayed
regardless. A `capture.db` from an earlier version keeps its rows and gains the new table on the
next capture.

## Local health endpoint

`ASPNETCORE_URLS` controls the ASP.NET health, content, gain and retrieval endpoints (default
`http://127.0.0.1:17432`, loopback only). `ai-proxy gain` and `ai-proxy mcp` use the same default;
give them `--endpoint` when you move it. See
[reading gain and retrieving originals](../how-to/reading-gain-and-retrieving-originals.md).

## Proxy port

| Variable | What it is |
|---|---|
| `PROXY__PORT` | The loopback port the proxy listener binds, and the port written into the system proxy setting. The default is `8080`. |

<!-- trust:trustchecked evidence=src/AiProxy/Configuration/ProxySettings.cs:12 -->
A whole number from 0 to 65535 is valid. Zero asks the operating system to choose a free port;
the listener address and system proxy marker report that actual bound port. An empty value
counts as unset and keeps `8080`; an invalid value stops startup with an error naming `Proxy:Port`.
<!-- /trust:trustchecked -->

## Runtime isolation and restart

<!-- trust:trustchecked evidence=src/AiProxy/ProxyServiceExtensions.cs:31 -->
| Variable | What it is |
|---|---|
| `PROXY__DATADIRECTORY` | Directory for the root certificate, gain ledger, capture database, and instance ownership files. Defaults to `~/.ai-proxy`. The compression database has its own setting. |
| `PROXY__CONFIGURESYSTEMPROXY` | `false` leaves the operating system proxy settings alone. Defaults to `true`. |
| `PROXY__TRUSTROOTCERTIFICATE` | `false` creates the inspection certificate without adding it to operating system trust. Defaults to `true`. |

Unset values preserve runtime defaults, including options injected by an embedding host.
For an isolated run, use a separate data directory, disable both operating system integrations,
and set the compression database separately or disable compression:

```bash
PROXY__DATADIRECTORY=/tmp/ai-proxy-example \
PROXY__CONFIGURESYSTEMPROXY=false \
PROXY__TRUSTROOTCERTIFICATE=false \
PROXY__COMPRESSION__ENABLED=false \
PROXY__PORT=0 ASPNETCORE_URLS=http://127.0.0.1:0 ai-proxy
```
<!-- /trust:trustchecked -->

<!-- trust:trustchecked evidence=src/AiProxy/Hosting/ProxyInstance.cs:137 -->
`ai-proxy --restart` selects the same data directory as normal startup. It requests a graceful
stop through a same-user control pipe, waits for that exact process to exit, acquires ownership,
and starts the replacement. The replacement remains in the foreground. It inherits the actual
proxy port and API URLs when their configuration keys are absent. Explicit environment, file,
or command-line configuration wins; for example, `ai-proxy --restart --Proxy:Port=9081` changes
the proxy port. Other settings still come from the new command's configuration.

Each data directory has one persistent `process.lock` held for the whole process lifetime and a
short `startup.lock` held during start or restart. Leave both files in place. A concurrent start
fails immediately; it does not wait and then stop the replacement. Paths are normalized to
absolute paths, but symlink aliases are not a supported way to select the same instance.

<!-- /trust:trustchecked -->

<!-- trust:trustchecked evidence=src/AiProxy/Hosting/ProcessIdentity.cs:8 -->
The owner also reports an exact process start token. Linux uses the kernel start ticks from
`/proc/PID/stat`, because independently running .NET processes can convert boot time to slightly
different UTC timestamps. macOS and Windows use the native process start time. The nonce and
PID remain part of the identity check.
<!-- /trust:trustchecked -->

<!-- trust:trustchecked evidence=src/AiProxy/Hosting/ProxyInstance.cs:137 -->
The command validates proxy settings and API URLs before requesting shutdown. An explicitly
requested port held by an unrelated listener produces an error before the owner is stopped.
Port availability cannot prove ownership, and another process can still take a port between
preflight and startup. There is no process scan, signal, or kill fallback.

Version 0.16.1 and earlier have no control pipe or process lease. A held system proxy marker
produces a manual-stop error before certificate preparation. The error includes the PID when
the operating system identifies one process with the marker open; otherwise it leaves the PID
unspecified. Unix uses `lsof`, and Windows uses Restart Manager. If an older instance disabled
system proxy integration, only occupied known requested ports can reveal a conflict. An older
instance on unknown nondefault ports cannot be discovered safely; stop it manually.

Root help, including `ai-proxy --restart --help`, runs before configuration validation, ownership
files, certificate preparation, or listeners. Use it even when the server configuration is invalid.
<!-- /trust:trustchecked -->
