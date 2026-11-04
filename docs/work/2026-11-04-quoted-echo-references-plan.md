# Plan: replace text a user copied from an earlier answer with a reference

**Date:** 2026-11-04
**Status:** next task, owner request of 2026-11-04. Research comes first; nothing is built.

## The scenario

People often copy part of an agent's answer back into their next message, to point at something said earlier. The copied part is context, not a new instruction. Only the last line is the request. An owner example (the quoted part is an echo, not a command):

> The code review found one MAJOR: a non-string JSON field crashes the whole report. There are also eight MINORs. Waiting for the test review so both sets of fixes go to the implementer in one batch.
>
> \- fix the issue

The first paragraph repeats an assistant turn that is already in the same request's history. Every later request carries that history, so the echo is sent and billed twice. It can also blur what the user is asking.

## The idea

When a user turn contains text that repeats an earlier assistant turn (or a tool result, or code already in the request), the proxy replaces that span with a short reference to where the original sits. The model then reads "the user points at X, and asks Y".

## What the research has to answer

1. **Detection.** How do we find echoed spans reliably? Options: exact substring match against earlier assistant and tool text after whitespace normalisation, or line-hash shingles for edits and partial copies. What is the minimum span length that is worth replacing? How do we handle a quote that is lightly edited, or wrapped in markdown quote markers?
2. **Reference format.** Candidates:
   - a position in the request, for example `ref:context:start{row,col}:end{row,col}`, or a message index plus a line range;
   - a retrieval id served by the existing `proxy_retrieve` tool (ADR 0005, 0008), so the model can expand the reference when it needs the exact text;
   - a short summary of the span ("the code review findings above") plus the id;
   - a pointer into code (`path:line`) when the echo is a code block the request already holds.

   The research should measure which format models follow best, and what each one costs in tokens.
3. **Save for retrieval.** Should echoed spans go into the content store, the way large tool results do, so a reference survives after the original turn falls out of the window or is compacted?
4. **Exploration hooks.** On the client side, could a Claude Code `UserPromptSubmit` hook do the replacement before the request leaves? Compare that with the proxy doing it. The proxy covers every client; a hook sees the prompt before it is sent and can ask the user.
5. **Prompt cache.** The provider caches the request prefix. The rewrite must be deterministic, and it must apply the same way to the same turn on every later request. Otherwise it changes a cached prefix and costs more than it saves (see [provider cache strategies](2026-10-24-provider-cache-strategies.md)).
6. **Safety.** Never drop the user's own words. A false positive must leave the request unchanged in meaning. Text the user typed that happens to match must not turn into a pointer the model can't resolve.
7. **Size of the win.** How often does this happen in real captured traffic (the capture store), and how many tokens do echoes cost per thread?

## Steps

1. Research record (`evidence-first-research`): answer questions 1 to 7, measure echo frequency in captured requests, and prototype two reference formats against a model. No credentials are available for paid scoring, so measure locally and label any estimate as estimated.
2. Owner gate on the reference format and on proxy versus hook.
3. Build behind a setting, off by default, with tests that prove an echo is replaced and that a near-miss is left alone.

## Where it sits in the backlog

It comes before installed-tool CI and the other items in [the 2026-11-04 open list](2026-11-04-installed-0.19.1-test-and-open-features.md).
