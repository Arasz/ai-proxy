# 0001 — The core holds the rules and the host holds the HTTP

**Date:** 2026-10-21
**Status:** Draft

## Context

The proxy has two kinds of knowledge. It knows which provider a request belongs to, which
credential that request may carry, and where it goes. It also knows how to speak HTTP to an
upstream, how to stream a response back, and how to read a body off a socket.

The first kind is worth testing on its own, and the second kind is not. A rule about
credentials has no business knowing what a `HttpRequestMessage` is, and a rule that has to be
tested through a socket is a rule that is only tested when the socket works.

The project already had the seam half-built. `src/AiProxy.Core` held the detector and
nothing else, and `src/AiProxy` held a `WebApplication` that answered `/health`. The v1
phase makes the seam real, and the question this record answers is where the line goes.

## Decision

`src/AiProxy.Core` holds the interfaces, the rules and the decisions, and depends on
nothing but the base class library. It does not reference ASP.NET Core, `HttpClient`,
persistence or any third-party package.

`src/AiProxy` holds the host: the middleware, the HTTP forwarder, the configuration and the
composition root. Everything that touches a socket lives there.

The rule that decided it is the credential rule. It is the rule most likely to be got
wrong, it is the rule the research record measured a leak with, and it is a rule about
which credential travels with a request, not about how bytes move. A rule that can be
tested without a socket is a rule that gets tested.

The composition runs the other way: the host registers the implementations of the core's
interfaces, and the core never learns who implements them. Every collaborator in the
composition is a singleton, because every one of them is stateless.

## Consequences

The positive: the credential rule, the detection precedence and the refusal decisions are
all tested as plain objects. A change to the rules cannot break because a socket misbehaved,
and the integration tests that do use sockets are few and say what they are about.

The negative: a request crosses two boundaries before it is relayed, so the shape of a
forwarded request is a set of records that have to be kept in step with the middleware that
builds them. The integration tests are what hold that in step.

Neutral: the compression pipeline sits in the core, so a pipeline that changes a body works
on bytes and knows nothing about the request around them. That is a constraint on what a
future pipeline can do, and it is the right one to start from.

## Alternatives considered

**One project, with the rules and the host together.** Simpler to build, and the rules
would still be testable in isolation, since they are already plain objects. It was rejected
because the layering is worth keeping when the HTTP surface grows, and because a rule that
can reach for `HttpContext` eventually will.

**A separate project per concern, one per provider.** More folders, and the provider
differences are a handful of path rules today. It was rejected as speculative; a dialect
that grows its own request shape would earn its own project.

**The core depending on `Microsoft.Extensions.Logging` and `Http.Abstractions`.** Both are
cheap and would let the core log and describe requests without a project reference to
ASP.NET. It was rejected for the same reason the rest: the rules should not be able to reach
for a request at all.
