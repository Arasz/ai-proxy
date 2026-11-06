# 0013 - Pin harness-only hosts to HTTP/1.1

Status: Accepted (owner request 2026-11-06)

## Context

ADR 0012 decrypts `chatgpt.com` on credentialed tunnels. A live test of 0.27.1 showed Codex
requests failing with `stream disconnected before completion: error sending request`, and the
proxy logging `HTTP/2 protocol error: frame of type Data exceeded the maximum accepted frame size`.

Measured with Codex 0.167.0 (`RUST_LOG=h2=debug`) and Titanium.Web.Proxy 7.0.26:

- `chatgpt.com` advertises `SETTINGS_MAX_FRAME_SIZE` 16777215.
- Titanium relays that setting to the client unchanged.
- Titanium's frame loop rejects any frame longer than 16384 bytes, a fixed limit. It has no option
  to change it.
- Codex may then send a longer DATA frame. Titanium answers `GOAWAY FRAME_SIZE_ERROR` and closes
  the connection. Codex received 30 of these in one failed turn.

The failure first appeared for a model upstream rejects, and it hid upstream's message. Short test
turns with supported models passed, including an 80 KB prompt and a tool-using turn. A real
`gpt-6-luna` session on the unpatched proxy then failed with the same Codex error while writing a
file, so this is not limited to rejected models. What makes a request send a long frame is not known.

## Decision

On a decrypted tunnel to a harness-only host, the proxy sets `UpstreamHttpProtocol` to `Http11` in
`BeforeTunnelConnectRequest`. The client and the origin then negotiate HTTP/1.1 through the proxy,
so no HTTP/2 SETTINGS frame is relayed. Other hosts keep HTTP/2.

## Consequences

Positive: the frame error is gone, and upstream's own errors reach the client. With the pin, the
same rejected request ended with `The 'gpt-6.1-terra' model is not supported when using Codex with
a ChatGPT account`, and the proxy logged no frame error. Supported turns still passed. SSE streams
over HTTP/1.1 as before.

Negative: a harness session has no HTTP/2 multiplexing to `chatgpt.com`, so parallel requests use
more connections.

Neutral: the proxy still answers a WebSocket upgrade with 426 (ADR 0012).

Not verified: other decrypted hosts could have the same mismatch when the origin advertises a
larger frame size and the client uses it. `api.openai.com` with an API-key Codex was not tested.

## Alternatives

- Pin every decrypted host: rejected, because Claude and other clients work on HTTP/2 today and no
  failure was measured there.
- Turn off HTTP/2 for the whole proxy with `EnableHttp2`: rejected for the same reason.
- Rewrite SETTINGS in the relay: Titanium has no hook for it.
- Fix the limit in Titanium: the right long-term fix, but it depends on a new package release.
  Remove the pin when a release honours the frame size it relays.
