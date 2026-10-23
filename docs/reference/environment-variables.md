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

ai-proxy sets the current user's system HTTP and HTTPS proxy to `127.0.0.1:8080` while it
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

## Local health endpoint

`ASPNETCORE_URLS` controls the ASP.NET health and content endpoints (default `http://localhost:5000`).
The proxy listener itself always binds to loopback port `8080` so its system proxy address stays fixed.
