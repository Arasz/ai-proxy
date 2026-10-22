# Why the proxy compresses the tool output

An agent runs a command, greps a repository, tails a log. The output comes back in the agent's
next request, and the provider is charged for it. That output is the part of the context that
grows without bound, and it is the part nobody reads twice.

The proxy sits between the agent and the provider, so it is the one place where the size of
that output can be reduced without the agent knowing. That is the whole reason for the stage.

## Why a router and not one reducer

The measurements settled this before any code was written. One extractive model per payload
kind, chosen by what survived the test and not by taste:

| the payload | what the measurements chose | why |
|---|---|---|
| a log | rules, in about a millisecond | a rule-based filter is three orders of magnitude faster than a model, and it keeps the line that says the request failed |
| JSON | rules, in about the same time | compacting a document and keeping every value in it is a mechanical job |
| a directory listing | nothing | nothing compresses a listing without losing file names |
| prose | the extractive model | the rules cannot tell a load-bearing sentence from a decorative one, and the model can |

A single reducer for everything would have been simpler and wrong in three of the four cases:
a model on a log keeps everything, and rules on prose throw away what mattered.

The record is [2026-10-22-on-device-compression-models.md](../work/2026-10-22-on-device-compression-models.md),
and the corrections the implementation found in it are at the end of that record.

## Why the reduction never rewrites anything

A summariser was measured and disqualified. On an agent log it returned a single line, and the
line with the error and the trace id was gone. The record calls that signal inversion rather
than data loss, and the distinction matters: a proxy that paraphrases the client's text can
change what the agent believes it was told.

So every reducer is a subset. A log loses the lines that carry no signal, a JSON document
loses its whitespace, a source file loses its blank lines, and the identifiers, numbers,
paths and directives that a reduction dropped are put back by an overlay. A fact that survives
is intact, and a fact that does not survive is in the store.

The overlay has a ceiling, because insurance that eats the savings is not insurance: at most
five percent of the reduction, and the decisions class, a failure verdict or an exit status or
a state transition, is the thing the ceiling protects.

## Why the original is kept

Because a reduction that cannot be undone is a reduction the agent cannot trust. Every request
the stage changes is written to a SQLite database with the original, the compressed text, the
ratio and the path of the reducer that ran, and the record is committed before the request goes
out. The response carries the id, and the original comes back from `/content/{id}`.

That is also what makes the stage safe to run: the proxy can be asked what it changed and can
answer with the thing it changed it from. The storage and the queue that carries the record
are in [ADR 0002](../adr/0002-the-store-is-an-adapter-and-sqlite-is-the-queue.md).

## Why it is off by default

Because the stage changes what the provider is sent, and that is the operator's call, not the
tool's. The proxy's other job is to be transparent, and a proxy that quietly rewrites a
request is not that. `PROXY_COMPRESSION=on` is the switch, and
[the how-to](../how-to/turning-on-the-compression-stage.md) is what an operator reads first.

## What is still open

Whether an agent that reads the reduced context answers as well as one that reads the
original. The tests prove that the facts survive a reduction, and that is not the same thing.
