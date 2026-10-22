# 0002 — The store is an adapter, and SQLite is the queue

**Date:** 2026-10-22
**Status:** Draft

## Context

The proxy now shrinks the tool output in a request before the provider sees it, and keeps
what it dropped so the original can be served back. Two things follow from that.

The first is persistence. The proxy needs a place to put the original of every request it
changed, the compressed text, the ratio and the path of the reducer that ran. The project's
layering rule says the core project depends on nothing but the base class library, so
persistence cannot live there, and the web host is about HTTP, so a schema and a set of
queries do not belong in it either.

The second is a promise. "The original is always kept" is only true if the record is
committed before the request is forwarded, and only survives a crash if it is committed at
all. An in-process queue is faster and simpler, and it loses the record when the process
dies.

The measurements settled the rules the reducers follow and the cost of the models that do
not, and they left one operational question open: how the record gets from the request path
to the disk without the request waiting for the disk.

## Decision

The store is its own project, `src/AiProxy.Storage`. It references Dapper and
`Microsoft.Data.Sqlite` and nothing from the host, and it implements the `IContentStore`
port that the core declares. The core keeps its dependency-free promise, the web host keeps
its own, and the adapter between them can be replaced without touching either.

SQLite is the pub/sub channel, as a queue table. The request path publishes a record to
`content_queue` and returns; the row is committed before the request is forwarded. A
background worker drains the queue and writes `content_request` and `content_chunk`. The
read path looks in the content tables first and in the queue second, so a record that has
not been written yet is still served.

The worker polls, and the poll is gated on `PRAGMA data_version` rather than on a blind
timer. The pragma moves when another connection commits, so the worker skips the query when
nothing has changed. The producer and the poller use separate connections, because the
pragma does not move for a connection's own commits. The database runs in WAL mode, so a
reader and the writer do not block each other.

## Consequences

The positive: the request path never waits for the disk beyond one insert, and the record is
durable the moment the row is committed. The promise that the original is kept survives the
process dying in the second after the request was forwarded, because the work is still
queued when the next start of the proxy comes. The core stays testable without a database,
and the adapter is testable against a real database file, which is the only way the SQL
gets tested at all.

The negative: two components write to one file. The producer writes the queue table and the
worker writes the content tables, and the worker also deletes from the queue. That is one
writer per table and two writers per file, and the project's rule of a single writer to a
shared datastore is bent, not obeyed. The bend is deliberate, because a queue is a channel
rather than a dataset, and the alternative (a channel in the process) loses the record on
a crash.

Neutral: the read path has to look in two places, which is one more query than a single
table would need. The worker's poll interval is a constant rather than a setting, because
a local proxy for one user does not need to be told how often to look.

## Alternatives considered

**The store in the web host.** One project fewer, and the DI composition stays in one
place. It was rejected because the host is about HTTP, and a schema, a set of queries and a
background worker are not HTTP.

**The store in the core project.** The port and the adapter in one place, which is the
simplest shape. It was rejected because the core would then depend on Dapper and on
SQLitePCLRaw, which is the thing ADR 0001 exists to prevent.

**An in-process channel instead of the queue table.** Faster, and no second writer. It was
rejected because a channel loses the record when the process dies, and the promise that the
original is kept is worth more than the microseconds it saves.

**The producer writing the content tables directly, with the queue carrying only the
deferred work.** One writer, and the record is retrievable at once. It was rejected for v1
because the deferred work is the model compression, and the model is not in v1, so the queue
would carry nothing. When the model arrives, this shape is the one to revisit.
