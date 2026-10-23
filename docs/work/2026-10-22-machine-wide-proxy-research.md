# Research: a machine-wide proxy without per-provider configuration

**Date:** 2026-10-22
**Question:** What proxy contract can route known provider URLs, inspect provider HTTPS, and pass every other destination through without route configuration?

## Findings

### F1 — Kestrel accepts both absolute-form HTTP requests and CONNECT authority-form requests [READ]

Kestrel can receive ordinary HTTP proxy requests such as `GET http://host/path` and `CONNECT host:443`. This makes it viable to keep the existing ASP.NET Core host for HTTP requests, although the CONNECT path still needs a tunnel implementation.

**Evidence:** [Kestrel request-target form validation](https://github.com/dotnet/AspNetCore.Docs/blob/main/aspnetcore/fundamentals/servers/kestrel/security-considerations.md#request-target-form-validation), section “Request-target form validation”.

### F2 — HTTPS CONNECT is opaque unless the proxy terminates TLS [READ]

A CONNECT proxy can carry the TLS connection without reading request paths, headers, or bodies. Reading or changing provider requests requires TLS termination and a certificate trusted by the client. The user chose inspection with a trusted per-user root; non-provider HTTPS remains tunneled.

**Evidence:** [Microsoft YARP HTTPS and TLS guidance](https://learn.microsoft.com/aspnet/core/fundamentals/servers/yarp/https-tls?view=aspnetcore-10.0), section “TLS tunneling (CONNECT)”; [Titanium Web Proxy library guide](https://www.titaniumproxy.com/docs/library), sections “Explicit man-in-the-middle (MITM) proxy” and “Tunnel only”.

### F3 — The canonical service hosts are stable, explicit API endpoints [READ]

The official API documentation names `api.openai.com`, `api.anthropic.com`, `generativelanguage.googleapis.com`, and `openrouter.ai` as API destinations. These can be recognized without configurable upstream values; the proxy should preserve each requested host as the destination.

**Evidence:** [OpenAI API quickstart](https://developers.openai.com/api/docs/quickstart); [Anthropic proxy network requirements](https://code.claude.com/docs/en/corporate-proxy#network-access-requirements); [Gemini API reference](https://ai.google.dev/api); [OpenRouter developer platform](https://openrouter.ai/developers).

### F4 — TLS interception is needed for provider request detection and compression [INFERRED]

This follows from F2 and the existing compression pipeline, which reads provider request bodies before forwarding them. A machine-wide proxy without a trusted certificate cannot inspect HTTPS provider calls. The accepted design decrypts only the four configured API hosts.

### F5 — Titanium Web Proxy can host the explicit proxy and manage user-level trust [READ]

The library targets .NET 10, is MIT licensed, supports explicit proxy endpoints and system-proxy registration/restoration, and allows tunnel decryption to be selected per CONNECT request. It avoids a custom HTTP CONNECT parser.

**Evidence:** [Titanium.Web.Proxy on NuGet](https://www.nuget.org/packages/Titanium.Web.Proxy), current package details; [library guide](https://www.titaniumproxy.com/docs/library), “System proxy and root CA” and “Tunnel only”.

### F6 — The user selected TLS inspection for provider traffic [DECIDED]

The user selected HTTPS inspection to support provider detection and compression, with a trusted root certificate. The proxy only decrypts the canonical provider hosts; all other HTTPS stays opaque. The user also asked for automatic machine-wide proxy use and default pass-through for other destinations.

## Remaining operational limit

Clients that ignore system proxy settings, pin provider certificates, or do not trust user roots can bypass inspection or fail their HTTPS request. macOS may require user interaction the first time its keychain accepts the inspection root; tests must never perform that operation.
