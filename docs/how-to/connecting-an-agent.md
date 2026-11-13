# Connecting an agent

Many agents need explicit proxy and certificate settings, even when the system proxy is set.
`ai-proxy harness` sets them for one child process and leaves your shell alone.

Start ai-proxy once first ([running the proxy](running-the-proxy.md)). It writes its public
inspection certificate to `~/.ai-proxy/root-ca.pem`. The private certificate and key stay in
`root-ca.pfx`; clients only ever need the public PEM file.

## Claude Code

Keep the proxy running in one terminal. In another, start Claude from the project directory:

```bash
ai-proxy harness claude
ai-proxy harness claude -- --model sonnet
```

The launcher checks that the proxy is reachable, then sets `HTTP_PROXY`, `HTTPS_PROXY`,
`NO_PROXY` and `NODE_EXTRA_CA_CERTS` for the child. It adds `X-Ai-Proxy-Project` with the
current directory to `ANTHROPIC_CUSTOM_HEADERS`, keeping any custom headers you already have.
`NO_PROXY` keeps your existing entries and gains loopback and the cloud metadata address
(`169.254.169.254`). Claude keeps its terminal, its arguments and its exit status.

The project header attributes gain to this directory, and ai-proxy removes it before
forwarding. Keep each provider's upstream base URL as it is: port 8080 is a forward proxy, not
an API base URL.

## Codex

```bash
ai-proxy harness codex -- exec "summarize this repo"
```

For Codex the launcher sets the same proxy variables and points `CODEX_CA_CERTIFICATE` at the
root CA file. It replaces a value you set yourself, and it never sets `SSL_CERT_FILE`. A CA bundle
you keep in `CODEX_CA_CERTIFICATE` is therefore replaced too, so hosts that bypass the proxy
through `NO_PROXY` fall back to Codex's native roots.

When Codex runs the interactive session or a model command such as `exec`, `review`, `resume`
or `fork`, the launcher puts `--no-daemon` before your arguments, so an already running Codex
daemon cannot bypass the proxy. Commands that work with the daemon, such as `agents`, `queue`
and `app-server`, are left alone, and so is a command line that already starts with
`--no-daemon` or `--remote`.

The launcher puts the project in the proxy URL as an ai-proxy credential, so `gain` attributes
Codex traffic to the current directory, built-in `openai` provider included. A custom model
provider can send the `X-Ai-Proxy-Project` header instead (the `env_http_headers` recipe in the
[gain guide](reading-gain-and-retrieving-originals.md#split-the-numbers-by-project)); when the
header is present, it wins. Codex signed in with ChatGPT is recorded too: the proxy decrypts
`chatgpt.com` only for tunnels that carry the credential, and never changes that traffic.

On Windows, an npm install of Codex provides `codex.cmd`. The launcher starts the process without
a shell and does not resolve `.cmd` files, so pass the full path to `codex.cmd`.

## Another proxy or CA file

The defaults are `http://127.0.0.1:8080` (or the port in `PROXY__PORT`) and
`~/.ai-proxy/root-ca.pem`. To use something else:

```bash
ai-proxy harness --proxy http://127.0.0.1:9080 --ca-file /path/to/root-ca.pem claude --model sonnet
```

The CA file you pass replaces `NODE_EXTRA_CA_CERTS` for Claude. If you need more private CAs,
pass one PEM bundle that holds them and the proxy's public CA.

`ai-proxy harness --help` lists the launcher options; `ai-proxy harness claude -- --help` shows
Claude's own help.

## The `aph` shortcut

`ai-proxy shortcut` adds an `aph` command beside `ai-proxy`, so `aph claude` runs
`ai-proxy harness claude`. It never replaces a file it did not create, and
`ai-proxy shortcut --remove` deletes it.

## Check the setup

If an agent does not seem to go through the proxy, ask:

```bash
ai-proxy doctor
```

Doctor changes nothing. It asks the running proxy who it is, opens a TLS handshake through it to
see that provider traffic is really decrypted, reads the published root certificate, and looks
at the proxy variables in your shell. Exit code 0 means every check passed, 1 means something is
broken, and 2 means only warnings or unknowns. Add `--format json` for scripts. See the
[doctor reference](../reference/doctor.md).
