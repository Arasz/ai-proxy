# ai-proxy v1 — implementation plan

**Date:** 2026-10-21
**Status:** queued. This is the continuation step after the repository scaffold.

The scaffold left the project with a tested provider detector and nothing else. This plan
says what v1 adds, what proves each piece is done, and which questions from the research
record it must answer.

**The basis is the research record, not another implementation.** The design comes from
[2026-10-21-detecting-the-provider-and-forwarding-to-the-right-upstream.md](2026-10-21-detecting-the-provider-and-forwarding-to-the-right-upstream.md) —
ten findings, graded, each with its evidence. No rtk code is vendored, ported or copied;
`rtk-proxy` is a BUSL-1.1 project and appears in the record only as a measured comparison
point.

## Scope

### 1. The router

A `Router` in `src/AiProxy.Core/` that takes a path, headers and body and answers one
question: where does this request go?

- A request whose provider the detector resolved goes to that dialect's configured upstream.
- A request whose path matches an operator-configured pass-through prefix goes to that
  prefix's upstream, unchanged.
- A request that matches neither is refused with a `routing_unknown` body, and the refusal
  names what was not known.

The refusal is the design, not a fallback. Finding F2 records why: a 404 on an unknown path
is strictly better than a credential at the wrong host.

**Done when:** the router's three outcomes are covered by tests, and a request that matches
neither is refused rather than relayed.

**Evidence:** F2, F6, F9.

### 2. The forwarder

Relays the request to the chosen upstream and the response back to the caller.

- The caller's headers travel with the request, minus the hop-by-hop set
  (`connection`, `keep-alive`, `proxy-authenticate`, `proxy-authorization`, `te`,
  `trailer`, `transfer-encoding`, `upgrade`, `host`, `content-length`, `accept-encoding`).
- The body streams in both directions. A streamed (SSE) response is relayed chunk by chunk,
  not buffered to its end.

**Done when:** a test asserts the hop-by-hop headers are dropped and the caller's
credential arrives at the upstream, and a test asserts an SSE response's chunks reach the
caller as they arrive rather than at the end.

**Evidence:** F7 measured the chunk spacing surviving the hop (upstream ~800 ms of stream
duration, ~814 ms through the proxy), and F4 recorded the credential arriving upstream.

### 3. The credential rule

The rule the whole proxy is shaped around, and the part most likely to be got wrong.

- A **path-named** provider keeps the caller's credential. The caller addressed that
  provider's endpoint; the credential is theirs.
- A **body-detected** provider receives the proxy's own credential for that dialect
  (`PROXY_ANTHROPIC_KEY` and its siblings), because the caller's credential may belong to
  the other provider.
- A body-detected provider with **no** proxy credential configured is refused with
  `routing_credential_ambiguous`, not forwarded.
- A **path/body conflict** is refused with the same care, before any upstream is chosen.

The detector already carries the fact this rule needs, and the router must read it from
there rather than re-derive it:

```csharp
var detection = DialectDetector.Detect(path, headers, body);
if (!detection.CredentialBelongsToCaller) { /* substitute the proxy's own credential */ }
```

`Detection.Source` says which signal named the provider — `Path`, `Body`, `Header` or
`None` — and `CredentialBelongsToCaller` is true only for `Path`. A header names a
provider the caller never addressed either, so it is treated like a body: the credential's
provenance is no better established, and the proxy's own credential is used.

**Done when:** all four cases are covered by tests, and the body-detected case is shown
reaching the upstream with the proxy's key, never the caller's.

**Evidence:** F3 measured the leak in the sibling implementation; F4 measured all three
behaviours of the corrected rule. The detector's provenance was added after a review
found the rule unimplementable on the earlier `Detection` shape, which carried the dialect
and a reason but not where the dialect came from.

### 4. The pass-through

A path-prefix rule set, carrying non-provider traffic.

`/mcp` is the worked example: an MCP server such as ai-raccoon on the same listener. The
traffic must arrive unchanged — the handshake, the tool list, the tool results and the
token gate all behave exactly as they do without the proxy in front.

**Correction, 2026-10-21, from the phase 1 implementation.** This section originally said
the rule was checked *after* provider detection. It is checked after the path signal and
before the body signal, and the difference is not cosmetic. Under the original wording a
request to `/mcp` whose body happened to carry a dialect shape — a tool argument called
`contents`, say — would be detected as a Gemini request and sent to the Gemini upstream with
the caller's credential. The operator's prefix is a stronger statement than a body shape, so
the prefix wins, and a pass-through request is carried without the proxy reading its body at
all. The phase 1 plan records the reasoning in full.

**Done when:** an ai-raccoon server behind the proxy answers an `initialize`, a `tools/list`
and a tool call with the same payloads it answers directly.

**Evidence:** F5 measured byte-identical `memory_stats` payloads direct and proxied, and
the same three ranked `memory_search` results.

## Questions v1 must answer

These are the research record's `Still open` section, and they belong to v1 rather than to
the scaffold.

- **Does the body-shape detector misclassify a real client?** The 12-case matrix in the
  record is synthetic. Capture one real request per provider and replay it through the
  detector. The proxy logs the path and the detection reason, so the hook is one log line.
- **Is the conflict refusal right for a translating gateway?** A gateway that serves
  Anthropic shapes on `/v1/messages` and OpenAI shapes on `/v1/chat/completions` would have
  its translated requests refused. Decide whether the escape hatch is to drop the body
  signal or to add an explicit operator rule.
- **Is the credential substitution acceptable?** An operator who wants the caller's own key
  to reach a body-detected provider gets a refusal today. Decide whether that stays.
- **Does an MCP server's session handling survive a proxy that re-uses connections?** The
  pass-through was measured against one ai-raccoon version, with stateless HTTP transport.
- **What is this project for beyond forwarding?** A proxy that only forwards adds nothing
  over the client's own HTTPS call. F10 leaves the compression question open, and it is the
  question about what this project is. Answering it is not v1's job, but v1 should not
  foreclose it.

## Out of scope

- **Compression.** Not investigated, not planned. See F10.
- **A hosted or network-exposed proxy.** The proxy is a single-operator local process and
  binds loopback. `SECURITY.md` says so, and the bind is not a configuration knob to be
  opened casually.
- **Credential storage.** The proxy holds provider credentials in its environment, not in a
  store. Anything else is a later decision.
