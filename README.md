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

## Status: the proxy works, the compression does not

The provider is detected, the request is forwarded to the right upstream, and other traffic
is carried through unchanged. What is here:

| Piece | State |
|---|---|
| The provider dialect detector | Implemented, as a composite of the path, body and header signals, in `src/AiProxy.Core/Providers/` |
| The router and the credential rule | Implemented, in `src/AiProxy.Core/Routing/` |
| The host, the forwarder and the pass-through | Implemented, in `src/AiProxy/` |
| The compression pipeline | A seam with a pass-through implementation. Whether this project should compress at all is an open question in the research record (F10) |

The design and the reasoning are in the
[v1 phase 1 plan](docs/work/2026-10-21-v1-phase1-structure-and-proxy-skeleton-plan.md) and
the [v1 plan](docs/work/2026-10-21-ai-proxy-v1-implementation-plan.md).

## Install

Not installable yet. When it is:

```bash
dotnet tool install -g ai-proxy
```

## Configuration

The environment holds the configuration, and the credentials live there and nowhere else.

| Variable | What it is |
|---|---|
| `PROXY_ANTHROPIC_UPSTREAM` | the Anthropic upstream, for example `https://api.anthropic.com` |
| `PROXY_OPENAI_UPSTREAM` | the OpenAI upstream |
| `PROXY_GEMINI_UPSTREAM` | the Gemini upstream |
| `PROXY_ANTHROPIC_KEY` | the proxy's own Anthropic credential |
| `PROXY_OPENAI_KEY` | the proxy's own OpenAI credential |
| `PROXY_GEMINI_KEY` | the proxy's own Gemini credential |
| `PROXY_PASSTHROUGH` | comma-separated `prefix=upstream` pairs, for example `/mcp=http://127.0.0.1:7801` |

The [environment variable reference](docs/reference/environment-variables.md) has the
detail, including what happens when a variable is missing.

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

A path the operator named for another server, such as `/mcp`, is carried to that server
before the body is even read. The operator's prefix is a stronger statement than a body
shape, and the traffic is not the proxy's to interpret.

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
