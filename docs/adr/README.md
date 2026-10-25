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
| [0002-the-store-is-an-adapter-and-sqlite-is-the-queue.md](0002-the-store-is-an-adapter-and-sqlite-is-the-queue.md) | The store is its own adapter project, and SQLite is the queue that carries a compressed request from the request path to the writer. | Draft |
| [0003-machine-wide-forward-proxy-and-provider-inspection.md](0003-machine-wide-forward-proxy-and-provider-inspection.md) | Use the current user's system proxy, inspect HTTPS only for canonical provider hosts, and tunnel all other TLS traffic. | Accepted |
| [0004-language-parsers-behind-the-code-lexer-interface.md](0004-language-parsers-behind-the-code-lexer-interface.md) | Use Roslyn for C# and Tree-sitter for later languages behind a common lexer interface, with parser SDKs outside Core. | Accepted |
| [0005-tool-result-previews-with-in-request-retrieval.md](0005-tool-result-previews-with-in-request-retrieval.md) | Large tool results reach the model as a deterministic line-subset preview plus a `proxy_retrieve` tool (lines, pattern or whole) that the proxy answers inside the same client request; Anthropic first, off until a retrieval-aware re-score passes. | Draft |
