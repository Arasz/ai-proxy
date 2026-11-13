# Research: implemented ai-proxy features

**Date:** 2026-11-06
**Question:** Which user-facing features are implemented in the current ai-proxy source tree?

## Findings

### F1 — ai-proxy exposes proxy lifecycle, diagnostics, gain, MCP retrieval, and harness commands [READ]

The CLI registers `doctor`, `status`, `stop`, `gain`, `mcp`, and the harness launcher. `doctor` is described as read-only; lifecycle commands report and stop the running instance.

**Evidence:** `src/AiProxy/Cli/CliApplication.cs:90-106`.

### F2 — Provider inspection preserves pass-through for unknown hosts [READ]

Known provider API requests are inspected, while unrecognized hosts return without provider processing. The inspector also removes proxy-only project and authorization headers before forwarding.

**Evidence:** `src/AiProxy/Hosting/Integration/ProviderRequestInspector.cs:47-67`.

### F3 — Optional capture, compression, and CCR are wired into proxy configuration [READ]

Capture can use a bounded SQLite store with eviction; disabled compression selects pass-through behavior and turns CCR off. Enabled compression registers the compression and CCR services.

**Evidence:** `src/AiProxy/ProxyServiceExtensions.cs:56-90`.

### F4 — MCP offers a read-only original-text retrieval tool [READ]

`retrieve_original` accepts a preview ID and a selection mode (`lines`, `pattern`, or `whole`), and calls the proxy retrieval API.

**Evidence:** `src/AiProxy/Mcp/RetrievalTools.cs:10-27`.

### F5 — Runtime support is documented across Windows, macOS, Linux, and Alpine [READ]

The platform reference lists supported Windows x64/ARM64, macOS ARM64, Linux x64/ARM64, and Linux musl x64 payloads. Alpine code views are marked best effort; the documented fallback sends original text when a grammar fails.

**Evidence:** `docs/reference/platforms.md:6-22`.

### F6 — Current source behavior has not been exercised end to end in this investigation [UNVERIFIED]

This inventory was read from source and documentation. I did not run the proxy or its test suite, so runtime success on this checkout remains unverified.

## Still open

- Which provider dialects and hostnames are currently configured? Inspect `IProviderHosts` and its concrete catalog to enumerate them precisely.
- Which compression transformations and retrieval limits are active by default? Read current settings and implementation paths, then verify representative behavior with focused runtime checks.
- Does the current checkout build and pass its relevant tests? Run the project build and affected test suites.
