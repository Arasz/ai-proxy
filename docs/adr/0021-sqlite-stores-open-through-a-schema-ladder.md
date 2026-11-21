# 0021 - SQLite stores open through a schema ladder

Status: Accepted (plan Revision 1, 2026-11-21; orchestrator in away mode, owner may reverse)

Amends: [ADR 0002](0002-the-store-is-an-adapter-and-sqlite-is-the-queue.md). The store adapter
now also owns the version of each file it opens.

## Context

The three stores change their schema in three different ways
([project review](../work/2026-11-21-project-review.md) F16).

- capture.db runs `CREATE TABLE IF NOT EXISTS` (`src/AiProxy.Storage/SqliteCaptureStore.cs:213`),
  then adds a column after a `pragma_table_info` probe (`:217-245`). Its `user_version` is 0.
- gain.db also uses `CREATE TABLE IF NOT EXISTS` (`src/AiProxy.Storage/SqliteGainStore.cs:227`,
  `Sql.Schema`) and ad hoc probes. Its `user_version` is 0.
- content.db declares `SchemaVersion = 3` (`src/AiProxy.Storage/ContentSchema.cs:21`) and stamps
  it (`:116`). But it has one step: a file below 3 runs every statement, all of them `IF NOT
  EXISTS` (`:113-117`), and a file at 3 or above returns at once (`:105-108`). A file newer than
  the code opens without a warning.

So any non-additive change (a renamed column, a new constraint, a changed type) does not reach an
older file, and nothing says so.

## Decision

1. Each SQLite store stamps `PRAGMA user_version` and opens through an ordered list of steps.
   Step N moves a file from version N-1 to N.
2. On open, the store runs every step above the file's version in order. Each step and its
   version stamp run in one `BEGIN IMMEDIATE` transaction, and the version is read again under the
   lock, as `ContentSchema.Ensure` does today.
3. Step 1 of capture.db and gain.db is the current DDL and probes, so a 0.34.0 file moves from 0
   to 1 without data change. content.db's current statements are its steps 1 to 3, so a stamped
   0.34.0 file stays at 3.
4. A file whose version is higher than the code's newest step refuses to open. The error names
   the file, its version and the code's version.
5. One shared ladder type in `src/AiProxy.Storage/` serves all three stores. A new schema change
   is a new step, never an edit of an old one.

## Consequences

Positive: a non-additive change reaches older files, and the order of changes is in one place per
store. A downgrade fails loudly instead of writing into a schema it does not know.

Negative: after an upgrade the user cannot run an older ai-proxy on the same data directory. A
downgrade needs the files moved aside.

Neutral: the first open after the release writes one version stamp to capture.db and gain.db.

## Alternatives considered

- Keep `IF NOT EXISTS` and add probes per change: rejected. That is the current state, and it
  fails silently on the first non-additive change.
- A migration package (for example FluentMigrator or DbUp): rejected. Three small files with one
  writer each need an ordered list, not a framework and its history table.
- Drop and recreate on a version change: rejected. gain.db is the ledger, and content.db holds
  originals that a preview may still need for 14 days (ADR 0008).

## Sources

- [Project review of 0.34.0](../work/2026-11-21-project-review.md), F16
- [Review fix plan](../work/2026-11-21-project-review-plan.md), packages P4, P5 and P6a, join
  test 5
- Code: `src/AiProxy.Storage/SqliteCaptureStore.cs`, `src/AiProxy.Storage/SqliteGainStore.cs`,
  `src/AiProxy.Storage/ContentSchema.cs`
