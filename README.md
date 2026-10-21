<p align="center">
  <strong>ai-proxy</strong>
  <br><br>
  One local endpoint for your AI agent's provider traffic: the proxy decides which
  provider a request belongs to, and carries other local traffic through untouched.
</p>

<p align="center">
  <a href="LICENSE"><img alt="License: MIT" src="https://img.shields.io/badge/license-MIT-blue"></a>
  <a href="https://github.com/Arasz/ai-proxy"><img alt="GitHub" src="https://img.shields.io/badge/github-Arasz/ai-proxy-8b949e?logo=github"></a>
</p>

---

## What it does

An agent configured to talk to `https://api.anthropic.com` and one configured to talk to
`https://api.openai.com` both point at the same local URL. The proxy looks at the request,
decides which provider it belongs to, and forwards it there with the caller's own
credential. A request for anything else — an MCP server such as
[ai-raccoon](https://github.com/Arasz/ai-raccoon) — is relayed to the upstream the operator
named for that path prefix, unchanged.

The design comes from a research record written before any of this code existed:
[detecting the provider and forwarding to the right upstream](docs/work/2026-10-21-detecting-the-provider-and-forwarding-to-the-right-upstream.md).
Every decision below traces to a finding in it.

## Status: not implemented yet

This repository is the scaffold. The provider detector exists and is tested; the forwarding,
the routing and the pass-through do not. There is no release, and nothing to install.

What is here:

| Piece | State |
|---|---|
| The provider dialect detector | Implemented, 15 tests, in `src/AiProxy.Core/Providers/` |
| The web host | A `/health` endpoint and nothing else |
| Forwarding, routing, pass-through | Not implemented — the v1 scope, recorded in [the v1 plan](docs/work/2026-10-21-ai-proxy-v1-implementation-plan.md) |
| Compression or cache alignment | Not investigated. Whether this project should do that at all is an open question in the research record (F10) |

## Install

Not installable yet. When it is:

```bash
dotnet tool install -g ai-proxy
```

## Configuration

Not settled yet. The research record fixes the shape of two things — the provider dialects
the detector knows (`anthropic`, `openai`, `gemini`) and the credential rule below — and
nothing else.

## The credential rule

This proxy forwards the caller's own provider credential upstream, so where that credential
ends up is the whole security question.

A request whose path names a provider keeps the caller's credential, because the caller
addressed that provider's endpoint and the credential is theirs. A request whose provider was
detected from the *body* carries a credential of unknown provenance — it may belong to the
other provider — so the proxy sends its own credential for that dialect, or refuses the
request outright. It never forwards a caller's credential to a provider it guessed.

The reason is measured, not assumed. The sibling Rust implementation was run against fake
upstreams with an OpenAI credential on an Anthropic path, and the credential arrived at the
Anthropic host. That is finding F3 in the record, and the rule that follows is F4.

## How the provider is detected

Three signals, in order of authority:

1. **The path.** `/v1/messages` is Anthropic, `/v1/chat/completions` and `/v1/responses` are OpenAI, `/v1beta/models/…:generateContent` is Gemini.
2. **The body shape.** `max_tokens` and `tools[].cache_control` mean Anthropic, `max_completion_tokens` and `response_format` mean OpenAI, `contents` means Gemini.
3. **The headers.** `anthropic-version` means Anthropic, `x-goog-api-key` means Gemini.

A path and a body that name different providers resolve to *nothing*. The request is not
routed to either, because a request whose provider was guessed must not carry a caller's
credential to a host the proxy chose.

## Building

```bash
dotnet build
dotnet test
```

The local quality gate runs before a push:

```bash
lefthook install   # once
.lefthook/pre-push/verify.sh all
```

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md). The failing test comes first, and the gates are real.

## Licence

[MIT](LICENSE). Copyright (c) 2026 Rafał Araszkiewicz.
