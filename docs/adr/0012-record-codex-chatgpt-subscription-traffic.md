# 0012 - Record Codex ChatGPT-subscription traffic, harness only

Status: Accepted (owner rulings 2026-11-06)

## Context

Codex CLI signed in with a ChatGPT account sends model turns to
`https://chatgpt.com/backend-api/codex/responses`, not to `api.openai.com`. The proxy does not
decrypt `chatgpt.com`, so these turns are missing from `gain`.

Codex 0.166.0 (source tag `rust-v0.166.0`) behaves as follows:

- It opens a WebSocket on that path by default. Configuration cannot turn this off for the
  built-in `openai` provider. When the upgrade gets HTTP 426, Codex uses HTTPS with server-sent
  events for the rest of the session (`core/src/client.rs`, `force_http_fallback`).
- It compresses HTTP request bodies with zstd by default (`Content-Encoding: zstd`).
- It uses the OpenAI Responses format. Usage is in the `response.completed` event, under
  `response.usage.{input_tokens, output_tokens, input_tokens_details.cached_tokens}`.
- It pins no certificate. It trusts `CODEX_CA_CERTIFICATE`, which `ai-proxy harness` sets.
- Under `ai-proxy harness`, it sends the ai-proxy credential on every CONNECT (ADR 0011).

`chatgpt.com` also serves the ChatGPT website to browsers that use the system proxy.

## Decision

1. `chatgpt.com` joins the provider catalog as a harness-only host with the OpenAI dialect. The
   root's name constraint permits `chatgpt.com`, so the root rotates once (ADR 0009 migration).
2. The proxy decrypts a CONNECT to a harness-only host only when the CONNECT carries a valid
   ai-proxy credential (ADR 0011). Every other tunnel to that host stays opaque.
3. On a decrypted harness-only tunnel, a WebSocket upgrade to `/backend-api/codex/responses` gets
   HTTP 426 from the proxy and never reaches upstream.
4. The proxy decodes zstd request bodies with a managed zstd package for capture and `gain`. It
   forwards the original bytes.
5. Harness-only hosts are record-only. The proxy never changes their requests or responses.
6. The usage reader reads the Responses `response.completed` event in SSE and JSON bodies.

## Consequences

Positive: ChatGPT-subscription Codex turns appear in `gain` with token usage. Browser traffic to
`chatgpt.com` is not decrypted. Streamed Responses usage from `api.openai.com` is now read too.

Negative:

- The root rotates once. That costs one trust prompt and a restart of every harness session.
- The root key can sign any `*.chatgpt.com` name.
- Harness Codex loses the latency benefit of its WebSocket transport.
- The proxy's upstream TLS differs from Codex's own. If `chatgpt.com` challenges it, harness Codex
  fails. The fallback is to run Codex outside the harness. Only a live run can show this.
- A new package dependency (zstd).

Neutral: a CONNECT without the credential behaves as before. Compression of `chatgpt.com`
requests is out of scope and needs its own decision.

## Alternatives considered

- Decrypt `chatgpt.com` for every client: exposes browser ChatGPT sessions to the proxy.
- Record WebSocket frames: attaching Titanium's frame handlers strips
  `Sec-WebSocket-Extensions`, so the handshake changes anyway. WebSocket turns also send only the
  new input, so their sizes cannot be compared with HTTP bodies.
- Have the harness pass `-c features.enable_request_compression=false`: changes the client's
  behaviour, and Codex run outside the harness would still send zstd.
- A custom Codex model provider: loses the built-in provider's OpenAI-specific behaviour.
- Document the gap only: subscription usage stays invisible.
