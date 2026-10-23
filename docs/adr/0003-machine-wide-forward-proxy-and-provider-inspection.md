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
continues through the proxy without HTTPS body inspection. A forced process termination may
prevent system proxy restoration, so normal shutdown is required.

## Dependency provenance

The proxy engine is the NuGet package `Titanium.Web.Proxy` 7.0.26 (published 2026-10-19,
NuGet owner `titanium007`, package id prefix not verified). The upstream GitHub project
(justcoding121/titanium-web-proxy) was archived at 3.x; the 7.x line has no project URL,
no repository URL, and no license metadata (no `license`, `licenseUrl` or license file in the
nuspec or catalog). The nuspec names only git commit `630f01804493a293fade9a48e8ee40387dbd7414`
with no repository to resolve it against. It depends on `Titanium.Web.Proxy.Abstractions`
7.0.26 and `BouncyCastle.Cryptography` 2.6.2.

Treat it as unverified third-party code that handles the root CA private key, decrypted
provider traffic and the system proxy settings. Its behavior was checked by decompiling the
assembly rather than assumed from upstream documentation, and that check drove these choices:
`ProxyServer` is constructed with root trust off (its default adds the root to the user's
certificate store on every start), it is started without its own system-proxy changes, and
the system proxy is set through `TrySetAsSystemProxy` with the registered endpoint, because
`SetAsSystemProxy` only logs a warning on failure. Its license status is unknown; confirm it
before redistributing ai-proxy in a form that bundles the package.
