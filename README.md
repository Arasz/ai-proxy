# ai-proxy

[![build](https://github.com/Arasz/ai-proxy/actions/workflows/build.yml/badge.svg)](https://github.com/Arasz/ai-proxy/actions/workflows/build.yml)
[![publish](https://github.com/Arasz/ai-proxy/actions/workflows/publish.yml/badge.svg)](https://github.com/Arasz/ai-proxy/actions/workflows/publish.yml)
[![NuGet](https://img.shields.io/nuget/v/ai-proxy/.svg)](https://www.nuget.org/packages/ai-proxy)
[![M8ven Score](https://m8ven.ai/badge/mcp/arasz-ai-proxy-1h8736)](https://m8ven.ai/mcp/arasz-ai-proxy-1h8736?s=readme)

ai-proxy installs itself as the current user's system HTTP and HTTPS proxy while it runs. It
routes the standard API hosts for OpenAI, Anthropic, Google Gemini, and OpenRouter, and passes
every other destination through. There are no upstream or pass-through rules to configure.

| Provider | API host |
|---|---|
| OpenAI | `api.openai.com`, plus the regional `us.`, `eu.`, `gb.`, `ca.`, `au.`, `jp.`, `kr.`, `sg.`, `in.` and `ae.api.openai.com` |
| Anthropic | `api.anthropic.com` |
| Google Gemini | `generativelanguage.googleapis.com` |
| OpenRouter | `openrouter.ai`, `eu.openrouter.ai` |

HTTPS requests to those hosts are decrypted so ai-proxy can inspect and optionally compress
request bodies. The first run creates and asks the operating system to trust a per-user root
certificate. HTTPS for every other host stays in an opaque CONNECT tunnel. This lets MCP
servers and other tools continue to work without exposing their HTTPS contents to the proxy.
Clients that ignore the operating system proxy, or do not trust the user's root certificates,
will connect directly or reject inspected provider HTTPS requests.

When ai-proxy stops cleanly, it restores the operating system's previous proxy settings. The
listener binds to loopback at `127.0.0.1:8080` (set `PROXY__PORT` to change the port); it is not exposed to the local network.

The proxy forwards the caller's credentials unchanged. It has no provider-owned API keys.
Compression and recoverable previews are on by default (`PROXY__COMPRESSION__ENABLED=false` turns them off); see the
[compression guide](docs/how-to/turning-on-the-compression-stage.md). `PROXY__CAPTURE=true` keeps a
local copy of every provider request body to measure what requests are made of; see
[environment variables](docs/reference/environment-variables.md#request-capture).

`ai-proxy gain` shows what the proxy saved, and `ai-proxy mcp` lets an agent read originals back;
see [reading gain and retrieving originals](docs/how-to/reading-gain-and-retrieving-originals.md).

## Install

The tool package is not published yet. To run from a checkout:

```bash
dotnet run --project src/AiProxy
```

This registers the system proxy and may ask once for permission to trust the HTTPS inspection
certificate. Stop the process with Ctrl+C to restore the previous system proxy settings.

## Build and test

```bash
dotnet build
dotnet test
```

## Security

The proxy can read provider request bodies and credentials while handling inspected traffic.
Only run it on a machine you control. See [SECURITY.md](SECURITY.md) for the trust boundary.
