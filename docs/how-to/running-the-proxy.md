# Running the proxy

Install the tool, start it, and keep it running while you use your agents. This page also
says what the proxy can see, and how to replace or stop a running instance.

## Install

Install the [.NET 11 SDK](https://dotnet.microsoft.com/download/dotnet/11.0), then the tool:

```bash
dotnet tool install --global ai-proxy
```

To update, run `dotnet tool update --global ai-proxy`.

## Start it

```bash
ai-proxy
```

From a checkout, run `dotnet run --project src/AiProxy` instead.

Keep the process running. On macOS the first start registers the system proxy and may ask once
to trust the HTTPS inspection certificate. Ctrl+C stops it and restores the previous proxy
settings.

The forward proxy listens on `127.0.0.1:8080` (set `PROXY__PORT` to change it). It binds to
loopback only, so the local network cannot reach it. A separate local API listens on
`127.0.0.1:17432`; check it from another terminal:

```bash
curl http://127.0.0.1:17432/health
```

## What the proxy sees

ai-proxy routes the standard API hosts of four providers and passes every other destination
through. There are no upstream or pass-through rules to configure.

| Provider      | API host                                                                                                                  |
|---------------|---------------------------------------------------------------------------------------------------------------------------|
| OpenAI        | `api.openai.com`, plus the regional `us.`, `eu.`, `gb.`, `ca.`, `au.`, `jp.`, `kr.`, `sg.`, `in.` and `ae.api.openai.com` |
| Anthropic     | `api.anthropic.com`                                                                                                       |
| Google Gemini | `generativelanguage.googleapis.com`                                                                                       |
| OpenRouter    | `openrouter.ai`, `eu.openrouter.ai`                                                                                       |
| ChatGPT (Codex sign-in) | `chatgpt.com`, decrypted only for tunnels carrying the ai-proxy credential, never changed                  |

HTTPS to those hosts is decrypted, so the proxy can inspect and compress request bodies. The
first run creates a per-user root certificate and asks the operating system to trust it.
`chatgpt.com` is decrypted only when the tunnel carries the `ai-proxy harness` credential, so a
browser's ChatGPT traffic stays opaque; its requests are recorded, never compressed.

Every other host stays in an opaque CONNECT tunnel. MCP servers and other tools keep working,
and the proxy never sees their HTTPS contents. A client that ignores the system proxy, or does
not trust the user's root certificates, connects directly or rejects the inspected request.

The proxy forwards the caller's own credentials unchanged. It has no provider API keys of its
own.

Compression and recoverable previews are on by default (`PROXY__COMPRESSION__ENABLED=false`
turns them off); see the [compression guide](turning-on-the-compression-stage.md).
`PROXY__CAPTURE=true` keeps a local copy of provider request bodies. Bodies over 8 MiB, or sent
without a `Content-Length`, are not kept; see
[request capture](../reference/environment-variables.md#request-capture).

## Restart, status and stop

<!-- trust:trustchecked evidence=src/AiProxy/Hosting/Proxy/ProxyInstanceLifetime.cs:40#RestartFlag -->
To replace the running instance gracefully, keep the same data directory and run:

```bash
ai-proxy --restart
```

The command waits for the owned process to exit, then runs the replacement in the foreground.
It preserves the actual proxy and local API ports unless configuration explicitly overrides them.
With no owner, it starts normally. Competing starts fail while a restart or stop is in progress.
To see or end the running instance without starting another, use `ai-proxy status` (exit 3 when nothing runs) and `ai-proxy stop`; see [lifecycle](../reference/lifecycle.md).
A plain start while another instance runs exits with `ai-proxy is already running (pid N, port P). Use `ai-proxy --restart` to replace it.`
An unknown command or a bare flag exits with code 2 and the usage text; `ai-proxy --version` prints the product version.
Configuration options take `--key=value` or `--key value`; an unknown `Proxy:*` key stops startup and names the key.
If an older installation holds the system proxy marker, stop it manually before restarting. The error includes its PID when the operating system identifies one process with the marker open.
See [runtime isolation and restart limits](../reference/environment-variables.md#runtime-isolation-and-restart).
<!-- /trust:trustchecked -->

## After a crash

When ai-proxy stops, it restores the operating system's previous proxy settings. After a crash,
a guard process or the next start does it. Both restore only while ai-proxy still owns the
settings, so a proxy you changed since is left alone when the ownership check can see the
change. See [crash recovery](../reference/lifecycle.md#crash-recovery).
