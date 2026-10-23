# Research: which agent clients reach a provider, and through which local proxy

**Date:** 2026-10-23
**Question:** Which AI agent clients on the owner's Mac reach an LLM provider, by what route, and which local proxy (rtk-proxy on 127.0.0.1:8080, or ai-proxy, which is not running) sees their traffic today?

The owner decided that ai-proxy replaces rtk-proxy fully. This record lists what has to keep working at cutover. Everything below was read-only: no proxy was started or stopped and no system setting changed. No credential value was printed or recorded. This is the published, trimmed version; the usage profile, unrelated projects, signed-in accounts and installed software were left out on purpose.

## Findings

### F1 — Only rtk-proxy is running, and the macOS system proxy is empty [MEASURED]

rtk-proxy listens on 127.0.0.1:8080; no ai-proxy process exists. `scutil --proxy` shows no HTTP, HTTPS or SOCKS proxy, only an exceptions list. So nothing reaches rtk-proxy through the system proxy today: every client that reaches it does so by an explicit base URL.

**Evidence:** `scutil --proxy`; `lsof -nP -iTCP -sTCP:LISTEN` (one listener on :8080, command `rtk-proxy`); a process listing filtered for `ai-proxy` returned nothing. Run 2026-10-23.

### F2 — Claude Code reaches rtk-proxy by a base URL in its own settings [MEASURED]

The user-level Claude Code settings file has an `env` block with `ANTHROPIC_BASE_URL=http://127.0.0.1:8080` and no proxy or CA variable. rtk-proxy's usage store attributes almost all of its traffic to client `claude-code`, every request on `/v1/messages`.

**Evidence:** a script printing only the env keys of the Claude Code settings files; rtk-proxy's usage store opened read-only (`mode=ro`), `select client, count(*) from usage_records group by client`. Run 2026-10-23.

### F3 — Claude Code applies that base URL itself, not through the process environment [MEASURED]

The environment of the running `claude` processes contains none of `ANTHROPIC_BASE_URL`, `HTTPS_PROXY`, `ALL_PROXY`, `NODE_EXTRA_CA_CERTS` or `SSL_CERT_FILE`, yet their traffic reaches rtk-proxy. Claude Code reads the setting from F2 and passes it to the shells it spawns. A shell `export` is not what routes it, and clearing one would change nothing.

**Evidence:** `ps eww` on the running `claude` processes, filtered for those variable names, values masked before printing. Run 2026-10-23.

### F4 — All recorded traffic is Anthropic; rtk-proxy does not detect a dialect [MEASURED]

Every row in the usage store has `provider = anthropic`. A handful of `/v1/chat/completions` test requests are labelled `anthropic` too: rtk-proxy labels everything with its one upstream. No existing client depends on ai-proxy's multi-provider detection, so that behaviour is new and carries no cutover risk.

**Evidence:** `select provider, count(*) from usage_records group by 1`; `select path, count(*) ... group by 1`. Run 2026-10-23, read-only connection.

### F5 — rtk-proxy's own compaction calls are a client of their own [READ]

A small number of rows carry client `ureq` (a Rust HTTP library) with a Haiku model and the `haiku-compaction` pipeline. These are rtk-proxy calling its configured cheap-model upstream (`[upstream.haiku]`, `provider = "anthropic"`, with its own API-key helper). Nothing on the client side does this. ai-proxy needs an equivalent, a credentialed upstream call from inside the proxy, or that compaction stops at cutover. This sits against ai-proxy's rule of holding no provider credentials of its own.

**Evidence:** usage store query `select model, pipeline, count(*) from usage_records where client='ureq' group by 1,2`; `~/.config/rtk/pipeline.toml` `[upstream.haiku]` and `[task.compact]`. That `ureq` is rtk-proxy itself is INFERRED from the pipeline name and the library; no packet capture was taken.

### F6 — Codex does not go through rtk-proxy [MEASURED]

Codex's config sets no base URL, provider table or proxy, and it signs in with a ChatGPT account. The usage store has no Codex rows. Codex talks to OpenAI directly today; ai-proxy would only see it if Codex follows the system proxy and trusts the inspection root, which was not tested.

**Evidence:** `~/.codex/config.toml` searched for `base_url`, `model_providers` and `proxy` (none); the auth file's mode key only; `select distinct client from usage_records`. Run 2026-10-23.

### F7 — How each client behaves as a system-proxy client is untested [UNVERIFIED]

Per client, two things decide whether ai-proxy sees it as a forward proxy: whether it follows the macOS system proxy, and whether it trusts the user-installed root. Neither was tested for any client, because that means starting ai-proxy or changing the system proxy.

One thing follows without testing. Claude Code sets its base URL itself (F2, F3), so it sends plain HTTP to whatever answers on 127.0.0.1:8080 and never takes the system-proxy path. At cutover, then, either ai-proxy answers on that address and accepts requests addressed to it directly, not only forward-proxy requests, or the `env` block in the Claude Code settings changes. That is INFERRED from F2 and F3. Whether ai-proxy accepts direct requests on its listen port was not read from the source here.

## Cutover table

| Client | How it picks its endpoint | Proxy that sees it today | Reaches ai-proxy as system forward proxy |
|---|---|---|---|
| Claude Code CLI | `ANTHROPIC_BASE_URL` in its settings `env` block | rtk-proxy | No, it bypasses the system proxy; needs a direct listener on the same URL or a settings change |
| rtk-proxy compaction (`ureq`) | `[upstream.haiku]` in rtk's pipeline config | It is rtk-proxy | Not a client; ai-proxy needs its own equivalent |
| Codex | ChatGPT sign-in, no base URL or proxy | None | UNVERIFIED |
| Other agents and IDE assistants on the machine | Their own settings; none routes to rtk-proxy today | None | UNVERIFIED |

## Still open

- Whether ai-proxy accepts plain requests addressed to its own loopback port as well as forward-proxy requests. All Claude Code traffic depends on that unless the settings change. Settle by reading the listener code and sending one request to a running ai-proxy.
- Per client, whether it follows the macOS system proxy and trusts a user root. Settle by running each once against a test listener with the system proxy pointed at it, with the owner present for the keychain prompt.
- Whether `ureq` is rtk-proxy's compaction call. A capture of the outbound call would settle it.
- Whether any MCP server makes a provider call of its own that should pass through ai-proxy.
- IDE AI assistant settings that could not be read in this session.
