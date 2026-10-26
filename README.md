<p align="center">
  <img src="https://raw.githubusercontent.com/Arasz/ai-proxy/main/content/icon.png" width="128" alt="ai-proxy">
</p>

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

Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), then install the tool:

```bash
dotnet tool install --global ai-proxy
```

To update an existing installation, run `dotnet tool update --global ai-proxy`.

## Start the proxy

```bash
ai-proxy
```

Or run from a checkout:

```bash
dotnet run --project src/AiProxy
```

Keep this process running while using your agents. On macOS, startup registers the system
proxy and may ask once for permission to trust the HTTPS inspection certificate. Stop it with
Ctrl+C to restore the previous proxy settings.

The forward proxy listens on `127.0.0.1:8080`. Its separate local API listens on
`127.0.0.1:17432`; check it from another terminal:

```bash
curl http://127.0.0.1:17432/health
```

## Connect an agent

Clients can require explicit proxy and certificate settings even when macOS's system proxy
is configured. Start ai-proxy once before launching them: it writes its public inspection
certificate to `~/.ai-proxy/root-ca.pem`. The private certificate and key remain in
`root-ca.pfx`; use the public PEM file for client trust.

For Claude Code, launch it from the project directory with:

```bash
HTTP_PROXY=http://127.0.0.1:8080 \
HTTPS_PROXY=http://127.0.0.1:8080 \
NO_PROXY=localhost,127.0.0.1,::1 \
NODE_EXTRA_CA_CERTS="$HOME/.ai-proxy/root-ca.pem" \
ANTHROPIC_CUSTOM_HEADERS="X-Ai-Proxy-Project: $PWD" \
claude
```

The proxy settings route traffic through ai-proxy, and `NODE_EXTRA_CA_CERTS` lets the Node
client trust its inspection certificate. The optional project header attributes gain to this
directory; ai-proxy removes that header before forwarding. See the
[gain guide](docs/how-to/reading-gain-and-retrieving-originals.md#split-the-numbers-by-project)
for the verified Codex project-header configuration. Keep each provider's upstream base URL;
port 8080 is a forward proxy, not an API base URL.

## Read gain and retrieve originals

```bash
ai-proxy gain
ai-proxy gain --project
ai-proxy gain --watch
```

These commands query the running proxy's local API. To expose kept originals to Claude Code
over MCP, register the same installed tool:

```bash
claude mcp add ai-proxy -- ai-proxy mcp
```

The [gain and retrieval guide](docs/how-to/reading-gain-and-retrieving-originals.md) describes
filters, reset, and retrieval limits.

## Build and test

```bash
dotnet build
dotnet test
```

## Security

The proxy can read provider request bodies and credentials while handling inspected traffic.
Only run it on a machine you control. See [SECURITY.md](SECURITY.md) for the trust boundary.
