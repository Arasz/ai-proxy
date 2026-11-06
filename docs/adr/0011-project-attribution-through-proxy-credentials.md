# 0011 - Project attribution through proxy credentials

Status: Accepted

## Context

`gain` splits traffic by project using the `X-Ai-Proxy-Project` request header, which the proxy
removes before forwarding. Claude Code can add it through `ANTHROPIC_CUSTOM_HEADERS`. Codex CLI
can add headers only to a custom model provider (`env_http_headers`); its built-in `openai`
provider cannot, so that traffic is unattributed.

Codex 0.166.0 and Claude Code 2.1.318 both send `Proxy-Authorization: Basic` on every CONNECT
when the proxy URL in `HTTPS_PROXY` carries userinfo, and percent-decode it to UTF-8 (measured
2026-11-05 on a local listener). Titanium.Web.Proxy gives each decrypted request the CONNECT
request that opened its tunnel (`HttpWebClient.ConnectRequest`), and requires proxy
authentication only when an authentication callback is set.

## Decision

`ai-proxy harness` sets `HTTP_PROXY`, `HTTPS_PROXY`, `http_proxy` and `https_proxy` to
`http://ai-proxy:<percent-encoded project path>@<host>:<port>` for every harness.

The proxy takes a request's project from, in order: the `X-Ai-Proxy-Project` header; a
`Proxy-Authorization` on the request; a `Proxy-Authorization` on the tunnel's CONNECT. A
credential counts only when it is Basic, valid UTF-8, has user `ai-proxy` and a non-empty
password without CR or LF. Anything else is ignored, never rejected.

The proxy removes `Proxy-Authorization` from every request it forwards. It sets no Titanium
authentication callback, so proxy authentication stays optional for every client.

## Consequences

Positive: Codex built-in provider traffic, and traffic from any harness whose HTTP stack honours
proxy userinfo, is attributed without client configuration. No registry, port or state is added.
A `Proxy-Authorization` sent through the proxy over plain HTTP no longer reaches the origin.

Negative: the project path is visible in the child's environment and, on loopback, in the Basic
credential. It already was, in `AI_PROXY_CODEX_PROJECT`. A local process can claim any project,
as it already can with the header. Traffic the proxy does not record stays outside `gain`:
WebSocket turns, and hosts it does not decrypt, such as `chatgpt.com` used by ChatGPT sign-in.

Neutral: the header stays the explicit route and wins over the credential. Clients that ignore
proxy userinfo behave as before.

## Alternatives considered

- A short id registered through the proxy API: needs registry state, a lifetime and an endpoint
  for a value that is not secret.
- A listener port per harness: on-demand listeners, lifetime tracking and port races.
- A base-URL path prefix the proxy strips: changes what the client sends, fails visibly when the
  proxy is off, and still adds no header.
- Client process lookup through Titanium's `ProcessId`: needs PID registration and a process-tree
  walk per connection; the process directory is not the project under `codex --cd`.
- Titanium's `ProxyBasicAuthenticateFunc`: makes authentication mandatory, so every client
  without credentials gets a 407.
