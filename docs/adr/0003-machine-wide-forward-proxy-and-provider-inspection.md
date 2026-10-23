# ADR 0003: use a machine-wide forward proxy and inspect provider HTTPS

**Status:** Accepted
**Date:** 2026-10-23

## Context

The reverse-proxy design required operators to configure an upstream per provider and a
path-prefix rule for every other service. That prevented clients from using their normal
provider URLs and made unrelated MCP traffic depend on hand-maintained routing rules.
HTTPS CONNECT also hides the request host and body unless the proxy terminates TLS.

## Decision

ai-proxy runs an explicit loopback HTTP(S) forward proxy and registers it as the current user's
system HTTP and HTTPS proxy while running. It restores the previous proxy settings on normal
shutdown. It identifies provider traffic by the standard OpenAI, Anthropic, Gemini, and
OpenRouter API hosts. It decrypts HTTPS only for those hosts using a per-user root certificate.
Other HTTP destinations pass through unchanged, and other HTTPS destinations remain opaque
CONNECT tunnels.

There are no configurable provider upstreams or pass-through rules. The caller's provider
credentials stay in its request and are forwarded to the addressed host. Compression remains
opt-in.

## Consequences

Provider clients must honor the system proxy and trust the per-user inspection root. Certificate
pinning or clients that ignore system proxy settings can bypass inspection or fail. MCP traffic
continues through the proxy without HTTPS body inspection. A forced kill no longer leaves the system
proxy pointing at a dead port. ai-proxy holds `~/.ai-proxy/system-proxy.lock` while the proxy is
set and starts a guard process (the same executable, `--guard-system-proxy`) that clears the
system proxy when its pipe to the main process closes without a `restored` message. If the
guard did not run either (reboot, power loss), the next start finds the non-empty marker and
clears the proxy before setting it. Both paths clear and do not restore: a proxy the user had
configured before the killed run is lost, and only a normal shutdown brings it back. A second
instance fails to start while the marker is held.

## Dependency provenance

The proxy engine is the NuGet package `Titanium.Web.Proxy` 7.0.26 (published 2026-10-19,
NuGet owner `titanium007`, package id prefix not verified). The nuspec carries no project
URL, repository URL or license metadata, but its git commit
`630f01804493a293fade9a48e8ee40387dbd7414` is the 2026-10-19 merge of the `beta` branch in
the MIT-licensed upstream project
[justcoding121/titanium-web-proxy](https://github.com/justcoding121/titanium-web-proxy),
whose `v7.0.26` release was published the same day. It depends on
`Titanium.Web.Proxy.Abstractions` 7.0.26 and `BouncyCastle.Cryptography` 2.6.2.

It is third-party code that handles the root CA private key, decrypted provider traffic and
the system proxy settings. Its behavior was checked by decompiling the
assembly rather than assumed from upstream documentation, and that check drove these choices:
`ProxyServer` is constructed with root trust off (its default adds the root to the user's
certificate store on every start), it is started without its own system-proxy changes, and
the system proxy is set through `TrySetAsSystemProxy` with the registered endpoint, because
`SetAsSystemProxy` only logs a warning on failure. A bundled redistribution carries the MIT
notice of the upstream project.
