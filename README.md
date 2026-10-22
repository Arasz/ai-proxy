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

## Status: the proxy works, and so does the compression

The provider is detected, the request is forwarded to the right upstream, and other traffic
is carried through unchanged. The tool output in a request is reduced before the provider
sees it, and the original is kept. What is here:

| Piece | State |
|---|---|
| The provider dialect detector | Implemented, as a composite of the path, body and header signals, in `src/AiProxy.Core/Providers/` |
| The router and the credential rule | Implemented, in `src/AiProxy.Core/Routing/` |
| The host, the forwarder and the pass-through | Implemented, in `src/AiProxy/` |
| The compression stage | Implemented, in `src/AiProxy.Core/Content/`, with the store in `src/AiProxy.Storage/`. Off until the operator sets `PROXY_COMPRESSION=on` |
| The model-based path | Not here. The measurements put the model on prose and the rules on logs and JSON, and the model is a 274 MB artifact, so the rule-based prose path runs until it is delivered |

The design and the reasoning are in the
[v1 phase 1 plan](docs/work/2026-10-21-v1-phase1-structure-and-proxy-skeleton-plan.md), the
[v1 plan](docs/work/2026-10-21-ai-proxy-v1-implementation-plan.md) and the
[compression design](docs/work/2026-10-22-content-compression-pipeline-architecture.md).

## Install

Not installable yet. When it is:

```bash
dotnet tool install -g ai-proxy
```

## Configuration

The environment holds the upstream and pass-through configuration. Provider credentials
come from the caller's request and are forwarded as supplied.

| Variable | What it is |
|---|---|
| `PROXY_ANTHROPIC_UPSTREAM` | the Anthropic upstream, for example `https://api.anthropic.com` |
| `PROXY_OPENAI_UPSTREAM` | the OpenAI upstream |
| `PROXY_GEMINI_UPSTREAM` | the Gemini upstream |
| `PROXY_PASSTHROUGH` | comma-separated `prefix=upstream` pairs, for example `/mcp=http://127.0.0.1:7801` |
| `PROXY_COMPRESSION` | `on` turns the compression stage on. It is off by default |
| `PROXY_COMPRESSION_DB` | where the store keeps what the proxy compressed. The default is `~/.ai-proxy/content.db` |

The [environment variable reference](docs/reference/environment-variables.md) has the
detail, including what happens when a variable is missing.

## The compression stage

The tool output an agent sends back to the provider is the part of the context that grows
without bound. The stage reduces it before the provider sees it, and keeps what it dropped.

A log keeps the lines that carry a signal, plus two anchors at each end. A JSON document is
compacted, and every value in it survives. A directory listing passes through untouched,
because nothing compresses a listing without losing file names. A mixed payload, an `ls`
result and a grep result and a JSON blob in one tool output, is split by structure and each
part is reduced by the rule that fits it.

Nothing is rewritten. Every reducer keeps a subset of the text it was given, so a fact that
survives is intact, and a fact that does not survive is in the store. The bytes outside the
tool output are what the client sent, byte for byte.

Every request the proxy changes is written to a SQLite database, with the original, the
compressed text, the ratio and the path of the reducer that ran. The record is committed
before the request is forwarded, the response carries the id in the
`x-ai-proxy-content-id` header, and the original is served back from `/content/{id}`.

The [how-to](docs/how-to/turning-on-the-compression-stage.md) says how to turn it on, how to
see that it ran, and how to get the original back. The rules come from measurements, and the
[research record](docs/work/2026-10-22-content-decomposition-and-format-detection.md) says
which. The storage and the queue are in
[ADR 0002](docs/adr/0002-the-store-is-an-adapter-and-sqlite-is-the-queue.md).

## Credentials

The proxy does not hold provider credentials. It forwards credential headers from the
incoming request unchanged, including when the provider is identified from the body or
headers. Requests where the path and body identify different providers are refused, so a
credential is never sent to a provider that conflicts with the addressed path.

## How the provider is detected

Three signals, in order of authority:

1. **The path.** `/v1/messages` is Anthropic, `/v1/chat/completions` and `/v1/responses` are OpenAI, `/v1beta/models/…:generateContent` is Gemini.
2. **The body shape.** `max_tokens` and `tools[].cache_control` mean Anthropic, `max_completion_tokens` and `response_format` mean OpenAI, `contents` means Gemini.
3. **The headers.** `anthropic-version` means Anthropic, `x-goog-api-key` means Gemini.

A path and a body that name different providers resolve to *nothing*. The request is not
routed to either, because the signals disagree about the destination.

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
