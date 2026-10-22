# SQLite as the pub/sub channel, and where the store lives

**Date:** 2026-10-22
**Question:** The proxy will keep every payload it compresses, so the original can be served back
later. Which SQLite mechanism carries that promise across a process restart, and where does the
persistence code live in a repository whose core project depends on nothing?

**Provenance.** Two research lanes were dispatched for this record: one on the SQLite pub/sub
mechanisms, one on the store layout. The first lane's file never landed on disk (the same failure
the compression record documents for two earlier agents), so its findings were carried over from
the delegation transcript and every load-bearing claim below was then checked against the primary
source by the orchestrator. Claims that survived are graded `READ` with the source; claims that
were only reasoned are graded `INFERRED`.

## Findings

### F1 — Microsoft.Data.Sqlite has no change-notification primitive [READ]

The package's shipped XML documentation lists 352 documented members across
`Microsoft.Data.Sqlite.Core` 10.0.13, and exactly one of them matches `Hook`, `Notify`,
`Notification`, `Watch` or `Change`: `SqliteConnection.ChangeDatabase(string)`, which points the
connection at a different database file. There is no update hook, no change event and no
subscription surface, so a consumer cannot be woken by the database itself.

**Evidence:** `Microsoft.Data.Sqlite.Core` 10.0.13 nupkg, `lib/net8.0/Microsoft.Data.Sqlite.xml`,
352 `<member name=...>` entries, one match for the pattern above, and it is
`M:Microsoft.Data.Sqlite.SqliteConnection.ChangeDatabase(System.String)`. The claim was checked by
counting the shipped documentation rather than by reading the API browser, because the shipped
file is the package's own statement of its public surface.

### F2 — The native hook exists in SQLite, and .NET cannot reach it [READ]

SQLite itself has `sqlite3_update_hook`, a callback fired for every row change on a connection.
It is a C API, and the managed SQLite stack (`SQLitePCLRaw`, which `Microsoft.Data.Sqlite` sits
on) does not bind it. Reaching it would mean writing a custom native binding, which is a
dependency this project would then have to publish for six runtimes.

**Evidence:** SQLite's `sqlite3_update_hook` documentation,
<https://sqlite.org/c3ref/update_hook.html>; the absence of a binding in the managed surface is
the same fact as F1, read from the package side.

### F3 — The mechanism that works is a work table plus a poll, and `PRAGMA data_version` is what makes the poll cheap [READ]

A producer writes a row into a queue table; a consumer polls for unprocessed rows. The poll does
not have to be a blind `SELECT` on a timer: `PRAGMA data_version` returns an integer that changes
when another connection commits to the database, and the value is meant to be compared across two
reads on the same connection.

Two details in the primary source constrain the design. The value "is unchanged for commits made
on the same database connection", so the producer and the poller must not share a connection. And
"it is only meaningful to compare the `PRAGMA data_version` values returned by the same database
connection at two different points in time", so the poller keeps one connection open and compares
its own successive readings.

**Evidence:** <https://sqlite.org/pragma.html#pragma_data_version> (read 2026-10-22): "The
integer values returned by two invocations of 'PRAGMA data_version' from the same connection will
be different if changes were committed to the database by any other connection in the interim. The
'PRAGMA data_version' value is unchanged for commits made on the same database connection."

### F4 — WAL mode is what makes a second process see the first one's commits [READ]

In rollback-journal mode a reader blocks a writer, so a proxy and a CLI sharing one database file
would fight over the write lock. WAL mode lets readers and a writer work at the same time, and a
connection in another process sees committed rows as soon as the commit returns.

**Evidence:** <https://sqlite.org/wal.html> (read 2026-10-22): "The WAL journal mode is a
persistent property of the database" and "readers do not block writers and writers do not block
readers".

### F5 — A queue table is the durable part, and it is the reason the promise survives a crash [INFERRED]

The pipeline's promise is that the original of every compressed payload is kept. If the record
were handed to an in-process channel and the process died before the writer drained it, the
promise would be broken silently. A row in a SQLite table is committed before the request is
forwarded, so the pending work survives the crash and the next start of the proxy finds it.

**Evidence:** reasoned from the durability of a committed transaction in SQLite
(<https://sqlite.org/lang_transaction.html>) against the requirement in the task. The
measurement of the crash path is not in this record.

### F6 — The store belongs in its own adapter project, not in the core and not in the web host [INFERRED]

ADR 0001 puts the rules in `src/AiProxy.Core` and the host in `src/AiProxy`. Dapper and
`Microsoft.Data.Sqlite` are neither: they are an adapter that implements a port the core
declares. Putting them in `src/AiProxy` would make the web host carry the schema, and putting the
port in the core with the adapter beside it would make the core depend on persistence, which is
the thing ADR 0001 exists to prevent. A third project, `src/AiProxy.Storage`, keeps the web host
about HTTP and lets the tests reference the store without pulling in the web stack.

**Evidence:** `docs/adr/0001-core-holds-the-rules-and-the-host-holds-the-http.md` (the core
"does not reference ASP.NET Core, `HttpClient`, persistence or any third-party package") and
`.ai-badger/instructions/csharp.instructions.md` ("keep thin adapter projects (MCP, CLI) mapping
to the core API without embedding business logic"). The layout is a decision, not a measurement.

### F7 — The package versions are the .NET 10 ones, and Dapper's trimming story is the one to check [READ]

`Microsoft.Data.Sqlite` 10.0.13 is the current stable release, and `Dapper` 2.1.89 is the
current stable release. Dapper is a reflection-heavy library, so a trimmed or AOT-published
build needs `DapperAot` and the source-generated `SqlMapper` types, and the project publishes a
self-contained tool for six runtimes, which bundles the native SQLite library.

**Evidence:** the NuGet flat-container index for `microsoft.data.sqlite` (195 stable versions,
the last being 10.0.13) and for `dapper` (the last being 2.1.89), both read 2026-10-22;
`Directory.Packages.props` centralises versions and `src/AiProxy/AiProxy.csproj` declares the six
`RuntimeIdentifiers`. Whether the published binary needs `DapperAot` is not settled here.

## What this means for the pipeline

- SQLite is the pub/sub channel as a work table, and the poll is gated on `PRAGMA data_version`
  rather than on a blind timer.
- The producer and the poller use separate connections, because `data_version` does not move for a
  connection's own commits.
- The database runs in WAL mode, so the proxy and any other reader can share the file.
- The store lives in a new `src/AiProxy.Storage` adapter project; the core declares the port and
  keeps its dependency-free promise.
- The pending work is committed before the request is forwarded, so "the original is always kept"
  survives a crash.

## Still open

- Whether the published single-file tool needs `DapperAot` and the source-generated mapper, or
  whether the trimming annotations are enough.
- The poll interval, and whether a poll belongs at all for a single-user local proxy that could
  read the queue once per request.
- The multi-writer question: the producer writes the queue table and the consumer writes the
  content tables, and whether that counts as one writer or two under the project's
  single-writer rule.
