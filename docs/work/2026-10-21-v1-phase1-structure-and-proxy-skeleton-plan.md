# v1 phase 1 — the structure and the proxy skeleton

**Date:** 2026-10-21
**Status:** planned. This is the first implementation phase of the v1 plan in
[2026-10-21-ai-proxy-v1-implementation-plan.md](2026-10-21-ai-proxy-v1-implementation-plan.md).

**The basis is the research record**
[2026-10-21-detecting-the-provider-and-forwarding-to-the-right-upstream.md](2026-10-21-detecting-the-provider-and-forwarding-to-the-right-upstream.md).
Every rule below cites the finding it comes from. No third-party implementation is a basis
for any of it.

## What this phase delivers

The repository has a tested detector and a bare `WebApplication` that answers `/health`. This
phase turns that into the v1 skeleton: a host that detects the provider of an inbound request,
routes a provider request through a compression pipeline to that provider's upstream, and
carries every other request through unchanged.

Five things land, in this order.

1. **The structure.** `src/AiProxy.Core` holds the domain, `src/AiProxy` holds the host. Every
   collaborator sits behind an interface and is composed by DI.
2. **Composite detection.** The single static detector becomes a composition of three
   independently testable detectors, consulted with fallback and keeping the conflict rule.
3. **`ICompressionPipeline`,** with a pass-through implementation. The real compression question
   is research finding F10 and stays open; the seam is what this phase builds.
4. **The router,** with the three outcomes the v1 plan names: a provider route, a pass-through
   route, and a refusal.
5. **The forwarder,** which relays the request and the response, drops the hop-by-hop headers
   and streams a streamed response chunk by chunk.

At the end of the phase the proxy can detect providers and pass a request through the
compression pipeline to the right upstream. A request that is not provider traffic is passed
through without any change.

## The layout

```
src/AiProxy.Core/
  Providers/          Dialect, DialectSource, Detection, DetectionRequest,
                      IDialectDetector, PathDialectDetector, BodyDialectDetector,
                      HeaderDialectDetector, CompositeDialectDetector
  Routing/            RouteDecision, CredentialSource, RefusalReason, IRequestRouter,
                      RoutingRequest, ProviderRequestRouter, PassThroughRequestRouter,
                      CompositeRequestRouter, UpstreamCatalog
  Compression/        ICompressionPipeline, CompressionRequest, CompressionResult,
                      PassThroughCompressionPipeline

src/AiProxy/
  Program.cs                    the composition root, five lines long
  ProxyApplication.cs           builds the WebApplication, so a test can start the real host
  Configuration/                ProxyOptions, ConfiguredCredentialProvider
  Forwarding/                   IUpstreamForwarder, HttpUpstreamForwarder, CredentialHeaders
  Hosting/                      ProxyForwardingMiddleware, RequestLoggingMiddleware
```

Folders are named for the domain concept, not for a technical bucket. `AiProxy.Core` keeps no
ASP.NET Core, HTTP client or persistence dependency: it holds interfaces, rules and decisions.
The forwarder and the middleware are HTTP infrastructure, so they live in the host project.

## The contracts

### Detection

`Detection` is the outcome of the whole detection, and it carries the fact the credential rule
needs, which the research record's F4 requires and which a review found missing from the
earlier shape.

```csharp
public enum DetectionOutcome { Resolved, Conflicted, Unresolved }

public sealed record Detection(Dialect Dialect, DialectSource Source, string Reason, DetectionOutcome Outcome)
{
    public bool CredentialBelongsToCaller => Source == DialectSource.Path;
}
```

`Outcome` separates the two ways a detection can name no provider. `Unresolved` means no signal
was found. `Conflicted` means two signals named different providers, which is the F3 case: a
request whose credential has unknown provenance, and which must be refused rather than
guessed. Without this field the router cannot tell the two refusals apart, because both have
`Dialect == Unknown` and `Source == None`.

A detector answers one question, so it returns a signal or nothing:

```csharp
public interface IDialectDetector
{
    Detection? Detect(DetectionRequest request);
}
```

`null` means this detector found no signal. The composite returns a `Detection` always, with
`Outcome.Unresolved` when nothing was found.

`DetectionRequest` is what a detector may look at: the path, the headers, and the body as a
string. A detector reads the parts it cares about and ignores the rest.

### The composite

`CompositeDialectDetector` takes the detectors in order and applies the precedence the research
record measured.

| Order | Signal | Authority | Conflict |
|---|---|---|---|
| 1 | Path | authoritative | a body that names a different provider makes the whole detection a conflict |
| 2 | Body | consulted when the path is generic | a path that names a different provider makes the whole detection a conflict |
| 3 | Header | last resort | never conflicts; a header that disagrees with the body loses |

The header is a hint, not a second opinion. F1 and F4 treat a header as naming a provider the
caller never addressed, so the header never turns a disagreement into a conflict, and a request
that only the header names is a body-or-header detection, which means the credential is not the
caller's.

The composite is where the conflict is detected, because only it sees both signals.

### Routing

```csharp
public enum CredentialSource { Caller, Proxy }
public enum RefusalReason { Unknown, CredentialAmbiguous }

public abstract record RouteDecision
{
    public sealed record ProviderRoute(Dialect Dialect, Uri Upstream, CredentialSource Credential, string Reason) : RouteDecision;
    public sealed record PassThroughRoute(Uri Upstream, string Reason) : RouteDecision;
    public sealed record RefusedRoute(RefusalReason Refusal, string Reason) : RouteDecision;
}
```

`RefusalReason` maps to the two codes the v1 plan names: `Unknown` is `routing_unknown` and
`CredentialAmbiguous` is `routing_credential_ambiguous`. The mapping lives in one place, so the
wire code and the reason cannot drift apart.

`IRequestRouter` answers where a request goes:

```csharp
public interface IRequestRouter
{
    RouteDecision Route(RoutingRequest request);
}
```

`RoutingRequest` carries the path, the headers, the body and the detection. The detection is
computed once, by the middleware, and handed to the router, so the router never re-derives it.

`CompositeRequestRouter` applies this order, and the order is a decision worth stating:

1. **The path names a dialect endpoint.** The path is the strongest signal there is, and F2
   measured that a request whose path and body disagree carries a credential of unknown
   provenance. A path-named provider is routed, keeping the caller's credential.
2. **The path matches an operator pass-through prefix.** The operator named this prefix and
   its upstream explicitly, so nothing is guessed. The request is carried unchanged.
3. **The body or the headers name a dialect.** The request is routed to that dialect's
   upstream, and the credential is the proxy's own, because the caller addressed no provider
   endpoint.
4. **Nothing named a provider.** The request is refused with `routing_unknown`.

**This differs from the v1 plan's wording, and the difference is deliberate.** The plan says the
pass-through rule is checked after provider detection. Taken literally, an MCP request to
`/mcp` whose body happens to carry a dialect shape, for instance a tool argument called
`contents`, would be detected as a Gemini request and sent to the Gemini upstream with the
caller's credential. F5 and F6 measured the pass-through as the safe way to carry
non-provider traffic, and the operator's explicit prefix is a stronger statement than a body
shape. The order above keeps `/mcp` on the MCP upstream in every case. The plan document is
updated in the same change.

`ProviderRequestRouter` applies the credential rule, which is the plan's section 3 and the
research record's F4:

- A path-named provider keeps the caller's credential, because the caller addressed that
  provider's endpoint.
- A body-detected or header-detected provider gets the proxy's own credential for that
  dialect, read from the environment.
- A body-detected or header-detected provider with no proxy credential configured is refused
  with `routing_credential_ambiguous`, not forwarded.
- A path and body that name different providers is refused with the same code, before any
  upstream is chosen.

`UpstreamCatalog` is the pure lookup from a dialect to its configured upstream, and from a path
prefix to its pass-through upstream. It is constructed from configuration and holds no I/O.

### Compression

```csharp
public interface ICompressionPipeline
{
    ValueTask<CompressionResult> CompressAsync(CompressionRequest request, CancellationToken cancellationToken);
}
```

`CompressionRequest` carries the request body bytes, the content type and the dialect.
`CompressionResult` carries the body bytes and whether the pipeline changed them.

`PassThroughCompressionPipeline` returns the same bytes it was given, unchanged. Its guarantee
is byte identity, which the phase proves with a test and which the real pipeline must keep. It
is the seam for F10, and the seam is what this phase builds; the compression itself is not
this phase's job.

Only provider requests go through the pipeline. A pass-through request is carried unchanged,
because its bytes belong to another server and the proxy has no business rewriting them.

### Forwarding

The forwarder lives in the host project, because forwarding is HTTP infrastructure.

- The caller's headers travel with the request, minus the hop-by-hop set: `connection`,
  `keep-alive`, `proxy-authenticate`, `proxy-authorization`, `te`, `trailer`,
  `transfer-encoding`, `upgrade`, `host`, `content-length`, `accept-encoding`.
- The method and the path are preserved, and the query string travels with them.
- When the route says the credential is the proxy's, the caller's credential headers are
  removed and the proxy's own is written in the header that dialect expects: `x-api-key` for
  Anthropic, `authorization: Bearer` for OpenAI, `x-goog-api-key` for Gemini.
- The response comes back with its status code, its headers and its body. A streamed response
  is relayed chunk by chunk, not buffered to its end, which is what F7 measured and what the
  test proves.

## The host

`Program.cs` composes and runs. The composition is explicit, and every registration is a
singleton, because every collaborator in this phase is stateless. `ProxyApplication.Create`
builds the host, so a test starts the same host a person runs rather than a stand-in for
it; it takes an optional `configure` callback, applied after the default registrations, so
a test can put a different pipeline in its place and watch what it does to the wire.

```csharp
builder.Services.AddSingleton<PathDialectDetector>();
builder.Services.AddSingleton<IDialectDetector>(sp => sp.GetRequiredService<PathDialectDetector>());
// ... body, header
builder.Services.AddSingleton<IDialectDetector, CompositeDialectDetector>();
builder.Services.AddSingleton<ICompressionPipeline, PassThroughCompressionPipeline>();
builder.Services.AddSingleton<UpstreamCatalog>();
builder.Services.AddSingleton<IRequestRouter, CompositeRequestRouter>();
builder.Services.AddSingleton<IUpstreamForwarder, HttpUpstreamForwarder>();
```

The middleware order is short, and the reason for it is the reason for everything else here:
the proxy must not touch traffic it does not own.

1. `RequestLoggingMiddleware` logs the method, the path, the detection reason and the status.
   It wraps everything else, so a refusal is logged like a forwarded request.
2. `ProxyForwardingMiddleware` does the work: detect, route, compress, forward, or refuse. It
   skips a request that already has a matched endpoint, which is how `/health` stays a local
   answer.

A request that is not provider traffic is carried through unchanged, and it is carried
without being read into memory first, which is what keeps a streamed upload streamed.

A provider request's body is read into memory, because the body is what the detector looks
at, what the conflict rule compares, and what the compression pipeline is handed. The proxy
reads at most 8 MiB and refuses a larger body rather than forwarding a body it never
looked at.

### Configuration

The environment is the configuration, and the environment is where the credentials live, so that
no credential is ever written into a tracked file.

| Variable | Meaning |
|---|---|
| `PROXY_ANTHROPIC_UPSTREAM` | the Anthropic upstream, for example `https://api.anthropic.com` |
| `PROXY_OPENAI_UPSTREAM` | the OpenAI upstream |
| `PROXY_GEMINI_UPSTREAM` | the Gemini upstream |
| `PROXY_ANTHROPIC_KEY` | the proxy's own Anthropic credential |
| `PROXY_OPENAI_KEY` | the proxy's own OpenAI credential |
| `PROXY_GEMINI_KEY` | the proxy's own Gemini credential |
| `PROXY_PASSTHROUGH` | comma-separated `prefix=upstream` pairs, for example `/mcp=http://127.0.0.1:7801` |

## The test design

The tests are designed before the first one is written, and each row names the failure mode it
targets and the wrong implementation that would turn it red. A test with no plausible mutant
is not a test.

### Unit: the detectors

| Behaviour | Level | Fails when | Wrong implementation that turns it red |
|---|---|---|---|
| A path that names a provider decides it | unit | the path rule is dropped or misspelled | the path detector returns nothing |
| A generic path with an Anthropic body resolves to Anthropic | unit | the body shape rules are dropped | the body detector returns nothing |
| A body that is not JSON resolves to nothing rather than throwing | unit | the body detector parses without a guard | the body detector throws on malformed JSON |
| The `anthropic-version` header resolves a path that names nothing | unit | the header rules are dropped | the header detector returns nothing |
| The path beats the body | unit | the precedence is inverted | the body wins over the path |
| A path and a body that disagree conflict rather than guess | unit | the conflict rule is missing | the composite picks the body |
| A body that the header disagrees with still resolves | unit | the header is treated as a second opinion | the composite conflicts on a header disagreement |
| A request with no signal is unresolved | unit | the composite invents a default | the composite returns a dialect when nothing was found |
| A path-named provider reports the path as its source | unit | the source is lost | the composite always reports the body |
| A body-detected provider does not treat the credential as the caller's | unit | the credential rule is lost | `CredentialBelongsToCaller` is always true |

The nineteen existing tests in `DialectDetectorTests` cover the same behaviours against the
static detector. They move to the composite: the file becomes `CompositeDialectDetectorTests`,
and the construction line changes from a static call to one composite built from the three
detectors. No behaviour expectation changes. The per-detector tests are new, and they are what
makes the composition a composition.

### Unit: the routing decisions

| Behaviour | Level | Fails when | Wrong implementation that turns it red |
|---|---|---|---|
| A path-named provider routes to that dialect's upstream with the caller's credential | unit | the credential rule is lost | the route always says the proxy's credential |
| A body-detected provider routes with the proxy's credential | unit | the substitution is missing | the route keeps the caller's credential |
| A body-detected provider with no proxy credential is refused `routing_credential_ambiguous` | unit | the refusal is missing | the route is produced anyway |
| A conflicting detection is refused before an upstream is chosen | unit | the conflict is not refused | the router routes a conflicted request |
| A path matching a pass-through prefix routes to that prefix's upstream | unit | the pass-through rule is missing | the request is refused |
| A body that names a dialect on a pass-through path still goes to the pass-through upstream | unit | the order is the plan's literal wording | the body wins over the operator's prefix |
| A request that matches nothing is refused `routing_unknown` | unit | the refusal is missing | the request is routed somewhere |
| The refusal names what was not known | unit | the refusal body is empty | the reason is a constant string |

### Unit: the compression pipeline

| Behaviour | Level | Fails when | Wrong implementation that turns it red |
|---|---|---|---|
| The pass-through pipeline returns the body byte for byte | unit | the pipeline rewrites the body | the pipeline trims or re-encodes the bytes |
| The pass-through pipeline says it changed nothing | unit | the changed flag lies | the flag is always true |
| The pass-through pipeline is the pipeline the host uses | unit | the host wires a different pipeline | the host registers another implementation |

### Integration: the host, over real HTTP

The host is started on a loopback port, and a fake upstream is started beside it. The fake
upstream records what it received, so the credential rule is asserted on what the upstream
saw, not on what the proxy intended.

| Behaviour | Level | Fails when | Wrong implementation that turns it red |
|---|---|---|---|
| A provider request reaches the fake upstream | integration | the routing never fires | the host refuses everything |
| The upstream received the caller's credential for a path-named provider | integration | the credential leaks or is lost | the proxy's key is sent, or no key is sent |
| The upstream received the proxy's key for a body-detected provider | integration | F3's leak is back | the caller's key is sent |
| A body-detected provider with no proxy key is refused `routing_credential_ambiguous` | integration | the refusal is missing | the request is forwarded |
| A conflicting request is refused and no upstream sees it | integration | the guess is back | the request is forwarded to one of the two |
| A `/mcp` request reaches the pass-through upstream unchanged | integration | the pass-through is broken | the request is refused or routed to a provider |
| A pass-through request's body arrives byte for byte | integration | the body is rewritten | the body is re-encoded or truncated |
| An unknown path is refused `routing_unknown` | integration | the refusal is missing | the request is relayed somewhere |
| The hop-by-hop headers are dropped | integration | the header set is wrong | `connection` or `content-length` travels |
| The caller's other headers arrive | integration | the header copy drops too much | `x-custom` is missing |
| The method and the path arrive unchanged | integration | the request is rewritten | the method becomes GET |
| A streamed response is relayed chunk by chunk | integration | the response is buffered | the whole body arrives at the end |
| The upstream's status and body reach the caller | integration | the response is mangled | the status becomes 200 whatever the upstream said |
| A local endpoint still answers locally | integration | the proxy swallows it | `/health` is proxied |

The streamed-response test is the one that would catch the most likely silent defect. It
asserts that the first chunk reaches the caller while the upstream is still producing, so a
forwarder that buffers the response to its end cannot pass it.

### The five that make the phase honest

1. **The conflicting request is refused and no upstream sees it.** It is the only test that
   proves F3's leak cannot come back.
2. **The upstream received the proxy's key for a body-detected provider.** It is the only test
   that proves the credential rule is about the bytes on the wire.
3. **A streamed response is relayed chunk by chunk.** It is the only test that proves the
   forwarder streams.
4. **A `/mcp` request reaches the pass-through upstream unchanged.** It is the only test that
   proves the proxy leaves other people's traffic alone.
5. **A provider request reaches the fake upstream.** It is the only test that proves the
   pieces are wired together at all.

## The steps

Every step ends green, with `dotnet build` and `dotnet test` from the repository root, and
every step writes its failing test first. The steps share files, so they run in order.

### Step 1 — the detection composite

**Files:** `src/AiProxy.Core/Providers/*`, `tests/AiProxy.Tests/Unit/Providers/*`.

**Test first:** the per-detector tests and the composite tests, with the existing nineteen
tests moved onto the composite.

**Done when:** the composite resolves each signal, applies the fallback order, conflicts when
the path and the body disagree, and reports the source; the existing behaviour is unchanged.

### Step 2 — the compression pipeline

**Files:** `src/AiProxy.Core/Compression/*`, its tests.

**Test first:** the byte-identity test and the changed-flag test.

**Done when:** the pass-through pipeline returns the body unchanged and says so.

### Step 3 — the router

**Files:** `src/AiProxy.Core/Routing/*`, its tests.

**Test first:** the eight routing behaviours in the table above.

**Done when:** the four outcomes of the credential rule and the two refusals are covered, and
the refusal reason names what was not known.

### Step 4 — the host

**Files:** `src/AiProxy/Program.cs`, `src/AiProxy/ProxyApplication.cs`,
`src/AiProxy/Configuration/*`, `src/AiProxy/Forwarding/*`, `src/AiProxy/Hosting/*`.

**Test first:** the integration tests, against the real host over a loopback port.

**Done when:** a provider request reaches the fake upstream through the compression pipeline,
a `/mcp` request reaches the pass-through upstream unchanged, an unknown path is refused, and
the hop-by-hop headers are dropped.

### Step 5 — the documents

**Files:** the v1 plan, `docs/work/README.md`, `docs/reference/*`, `docs/changelog/*`,
`VERSION`.

**Done when:** the plan's ordering correction is recorded, the environment variables are
documented, the changelog entry and the version bump are in place, and the docs index is
truthful.

## The risks

- **The body must be read to detect it, and a pass-through must not be read.** The middleware
  therefore decides between the pass-through prefix and the body read before it reads
  anything. A body that is too large to inspect is reported rather than guessed at, with a
  documented limit.
- **The credential rule lives in two places.** The detection says whose credential it is, and
  the router says what to do about it. The rule is stated once, in the router, and the
  detection only carries the fact.
- **The pass-through ordering deviates from the plan's wording.** The deviation is recorded
  here and in the plan, with the reason, so the next reader does not "fix" it back.
- **The host is where the tests get expensive.** The integration tests start two servers.
  They are the only tests that need them, and they are the ones that prove the phase.

## What was built

The five steps landed as five commits. The suite is 82 tests: 49 over the detection
composite and the three detectors, 13 over the routing decisions, 3 over the compression
pipeline, and 17 over the host over real HTTP.

Two things in the plan changed while it was being built, both recorded above: the
credential provider is `ConfiguredCredentialProvider` rather than an
`EnvironmentCredentialProvider`, and the pass-through ordering is the one this plan
describes rather than the one the v1 plan originally said. The body limit of 8 MiB is the
number the plan said it would pick.

Two checks were watched go red before they were trusted: the streamed-response test fails
when the forwarder buffers the response to its end, and the credential test fails when the
forwarder keeps the caller's credential instead of the proxy's.

## Open questions

- The body size limit above which a body is not inspected is a number this phase picks and the
  documentation states. It was not measured.
- Whether a body-detected provider should be refused outright rather than given the proxy's
  credential is one of the research record's open questions. The plan's rule stands for now.
- The compression question itself, F10, is untouched. The seam is what this phase builds.
