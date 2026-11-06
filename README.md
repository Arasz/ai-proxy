![ai-proxy](https://raw.githubusercontent.com/Arasz/ai-proxy/main/content/icon.png)

# ai-proxy

[![build](https://github.com/Arasz/ai-proxy/actions/workflows/build.yml/badge.svg)](https://github.com/Arasz/ai-proxy/actions/workflows/build.yml)
[![publish](https://github.com/Arasz/ai-proxy/actions/workflows/publish.yml/badge.svg)](https://github.com/Arasz/ai-proxy/actions/workflows/publish.yml)
[![NuGet](https://img.shields.io/nuget/v/ai-proxy.svg)](https://www.nuget.org/packages/ai-proxy)
[M8ven Score](https://m8ven.ai/mcp/arasz-ai-proxy-1h8736?s=readme)

ai-proxy installs itself as the current user's system HTTP and HTTPS proxy while it runs. It
routes the standard API hosts for OpenAI, Anthropic, Google Gemini, and OpenRouter, and passes
every other destination through. There are no upstream or pass-through rules to configure.

| Provider      | API host                                                                                                                  |
|---------------|---------------------------------------------------------------------------------------------------------------------------|
| OpenAI        | `api.openai.com`, plus the regional `us.`, `eu.`, `gb.`, `ca.`, `au.`, `jp.`, `kr.`, `sg.`, `in.` and `ae.api.openai.com` |
| Anthropic     | `api.anthropic.com`                                                                                                       |
| Google Gemini | `generativelanguage.googleapis.com`                                                                                       |
| OpenRouter    | `openrouter.ai`, `eu.openrouter.ai`                                                                                       |
| ChatGPT (Codex sign-in) | `chatgpt.com`, decrypted only for tunnels carrying the ai-proxy credential, never changed                  |

HTTPS requests to those hosts are decrypted so ai-proxy can inspect and optionally compress
request bodies. The first run creates and asks the operating system to trust a per-user root
certificate. `chatgpt.com` is decrypted only when the tunnel carries the `ai-proxy harness` credential, so a browser's ChatGPT traffic stays opaque; its requests are recorded, never compressed. HTTPS for every other host stays in an opaque CONNECT tunnel. This lets MCP
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

<!-- trust:trustchecked evidence=src/AiProxy/Hosting/Proxy/ProxyInstanceLifetime.cs:40#RestartFlag -->
To replace the running instance gracefully, keep the same data directory and run:

```bash
ai-proxy --restart
```

The command waits for the owned process to exit, then runs the replacement in the foreground.
It preserves the actual proxy and local API ports unless configuration explicitly overrides them.
With no owner, it starts normally. Competing starts fail while a restart or stop is in progress.
To see or end the running instance without starting another, use `ai-proxy status` (exit 3 when nothing runs) and `ai-proxy stop`; see [lifecycle](docs/reference/lifecycle.md).
A plain start while another instance runs exits with `ai-proxy is already running (pid N, port P). Use `ai-proxy --restart` to replace it.`
An unknown command or a bare flag exits with code 2 and the usage text; `ai-proxy --version` prints the product version.
Configuration options take `--key=value` or `--key value`; an unknown `Proxy:*` key stops startup and names the key.
If an older installation holds the system proxy marker, stop it manually before restarting. The error includes its PID when the operating system identifies one process with the marker open.
See [runtime isolation and restart limits](docs/reference/environment-variables.md#runtime-isolation-and-restart).
<!-- /trust:trustchecked -->

## Connect an agent

Clients can require explicit proxy and certificate settings even when macOS's system proxy
is configured. Start ai-proxy once before launching them: it writes its public inspection
certificate to `~/.ai-proxy/root-ca.pem`. The private certificate and key remain in
`root-ca.pfx`; use the public PEM file for client trust.

Keep the proxy running in one terminal. In another, use `harness` to start a command-line
agent from the project directory. For Claude Code:

```bash
ai-proxy harness claude
ai-proxy harness claude -- --model sonnet
```

The launcher checks that the proxy is reachable, sets `HTTP_PROXY`, `HTTPS_PROXY`,
`NO_PROXY` and `NODE_EXTRA_CA_CERTS` for the child, and adds `X-Ai-Proxy-Project` with the
current directory to `ANTHROPIC_CUSTOM_HEADERS`. It preserves other custom headers and
existing proxy bypass entries. Claude keeps its terminal input and output, arguments, and
exit status. The parent shell's environment stays unchanged.

The defaults are `http://127.0.0.1:8080` (or the port in `PROXY__PORT`) and
`~/.ai-proxy/root-ca.pem`. To use a different running proxy or CA file:

```bash
ai-proxy harness --proxy http://127.0.0.1:9080 --ca-file /path/to/root-ca.pem claude --model sonnet
```

The selected CA file replaces `NODE_EXTRA_CA_CERTS` for Claude. If you need additional
private CAs, pass a PEM bundle containing those certificates and the proxy's public CA.
`ai-proxy shortcut` adds an `aph` command beside `ai-proxy`, so `aph claude` runs
`ai-proxy harness claude`. It never replaces a file it did not create, and `ai-proxy shortcut --remove`
deletes it.

Use `ai-proxy harness --help` for launcher options; use `ai-proxy harness claude -- --help` for Claude's help.

The proxy settings route traffic through ai-proxy, and `NODE_EXTRA_CA_CERTS` lets the Node
client trust its inspection certificate. The project header attributes gain to this
directory; ai-proxy removes that header before forwarding. See the
[gain guide](docs/how-to/reading-gain-and-retrieving-originals.md#split-the-numbers-by-project)
for the verified Codex project-header configuration. Keep each provider's upstream base URL;
port 8080 is a forward proxy, not an API base URL.

### Codex

```bash
ai-proxy harness codex -- exec "summarize this repo"
```
For Codex the launcher sets the same proxy variables and points `CODEX_CA_CERTIFICATE` at the
root CA file. It replaces a value you set yourself, and it never sets `SSL_CERT_FILE`. A CA bundle
you keep in `CODEX_CA_CERTIFICATE` is therefore replaced too, so hosts that bypass the proxy
through `NO_PROXY` fall back to Codex's native roots. When Codex runs the interactive session or a
model command such as `exec`, `review`, `resume` or `fork`, the launcher puts `--no-daemon` before
your arguments, so an already running Codex daemon cannot bypass the proxy. Commands that work
with the daemon, such as `agents`, `queue` and `app-server`, are left alone, and so is a command
line that already starts with `--no-daemon` or `--remote`.

On Windows, an npm install of Codex provides `codex.cmd`. The launcher starts the process without
a shell and does not resolve `.cmd` files, so pass the full path to `codex.cmd`.

The launcher also puts the project in the proxy URL as an ai-proxy credential, so `gain`
attributes Codex traffic to the current directory, built-in `openai` provider included. A custom
model provider can send the `X-Ai-Proxy-Project` header instead (the `env_http_headers` recipe in
the [gain guide](docs/how-to/reading-gain-and-retrieving-originals.md#split-the-numbers-by-project));
when the header is present, it wins. Codex signed in with ChatGPT is recorded too: the proxy
decrypts `chatgpt.com` only for tunnels that carry the credential, and never changes that traffic.

## Check the setup

If an agent does not seem to go through the proxy, ask it:

```bash
ai-proxy doctor
```

Doctor changes nothing. It asks the running proxy who it is, opens a TLS handshake through it
to see that provider traffic is really decrypted, reads the published root certificate, and
looks at the proxy variables in your shell. Exit code 0 means every check passed, 1 means
something is broken and 2 means only warnings or unknowns. Add `--format json` for scripts.
See the [reference](docs/reference/doctor.md).

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
