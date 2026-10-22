# adr/

Architecture decision records: immutable, frozen. Each file records one decision in date
order (`NNNN-slug.md`); records are never edited after acceptance — a new decision gets a
new number. Add an ADR for any architecture-level decision via `create-task-spec` /
`owner-gate-review` workflow.

A record may be indexed as **Draft** before its decision is taken: it is listed here from
the moment the file exists, and it becomes frozen only when it reaches `Accepted`.

## Contents

| ADR | Decision | Amended |
|-----|----------|---------|
| [0001-core-holds-the-rules-and-the-host-holds-the-http.md](0001-core-holds-the-rules-and-the-host-holds-the-http.md) | The core project holds the interfaces and the rules and depends on nothing; the host project holds the middleware, the forwarder and the composition. | Draft |
