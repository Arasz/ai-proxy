# doctor

`ai-proxy doctor` checks the setup without changing anything. It never writes to the data
directory, never trusts a certificate and sends no HTTP request to a provider.

## Checks

| Check | What it tests | Fail | Warn | Unknown |
|---|---|---|---|---|
| `api` | `GET {endpoint}/health` answers as `ai-proxy`. | Nothing answers, or another server does. If the proxy port is also closed, one failure says the proxy is not running. | The running version differs from the command's. | An older proxy answers without `product`. |
| `inspection` | `CONNECT {host}:443` through the proxy, then a TLS handshake only. The leaf must chain to `<data-directory>/root-ca.pem`. | The host is not decrypted, or the certificate does not chain to that root. | | The proxy answered the tunnel request with a status other than 200, the tunnel or handshake timed out or was reset (including when the proxy cannot reach an upstream), or the published root could not be read. Also reported (as "skipped") when the proxy is not running. |
| `root` | Reads `<data-directory>/root-ca.pem` (at most 64 KiB). | Missing, holds a private key, not limited to the provider hosts, or outside its validity period. | Expires within 30 days, or the operating system does not trust it (advisory; `NODE_EXTRA_CA_CERTS` clients are unaffected). | |
| `environment` | `HTTPS_PROXY` and `HTTP_PROXY`, either case, against the proxy address. | A variable names another endpoint. | | |

An unset variable passes: clients that use the system proxy are covered. Output shows only
`scheme://host:port` of a proxy variable, never credentials, path or query. The root path is
assumed from `--data-directory`; doctor cannot ask a running proxy where its directory is.

## Options

| Option | Default | Meaning |
|---|---|---|
| `--endpoint <URL>` | `http://127.0.0.1:17432` | The proxy's local API. |
| `--proxy <URL>` | `http://127.0.0.1:{port}`, where the port comes from the `Proxy__Port` environment variable, or 8080 | The proxy listener. Overrides the default. |
| `--data-directory <PATH>` | `~/.ai-proxy` | Where `root-ca.pem` lives. |
| `--host <HOST>` | `api.anthropic.com` | A provider host the proxy inspects. |
| `--timeout <SECONDS>` | `5` | How long each network check may take, from 1 to 300. Raise it on a slow or busy machine: the first inspected handshake makes the proxy create a certificate. |
| `-f, --format <text\|json>` | `text` | Table or JSON. |

## Exit codes

| Code | Meaning |
|---|---|
| 0 | Every check passed. |
| 1 | At least one check failed. Command-line usage errors also return 1. |
| 2 | No failure, but at least one warning or unknown. |
| 130 | Cancelled with Ctrl+C. |

The JSON report has `status`, `exitCode` and `checks`, each check with `name`, `status`
(`pass`, `warn`, `fail`, `unknown`), `detail` and `fix`.
