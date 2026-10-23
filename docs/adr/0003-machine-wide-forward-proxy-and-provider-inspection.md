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
