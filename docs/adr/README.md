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
| [0001-core-holds-the-rules-and-the-host-holds-the-http.md](0001-core-holds-the-rules-and-the-host-holds-the-http.md) | The core project holds the interfaces and the rules and depends on nothing; the host project holds the Titanium proxy, the relay and the composition. | Accepted, amended 2026-10-30 |
| [0002-the-store-is-an-adapter-and-sqlite-is-the-queue.md](0002-the-store-is-an-adapter-and-sqlite-is-the-queue.md) | The store is its own adapter project, and SQLite is the queue that carries a compressed request from the request path to the writer. | Accepted |
| [0003-machine-wide-forward-proxy-and-provider-inspection.md](0003-machine-wide-forward-proxy-and-provider-inspection.md) | Use the current user's system proxy, inspect HTTPS only for canonical provider hosts, and tunnel all other TLS traffic. | Accepted |
| [0004-language-parsers-behind-the-code-lexer-interface.md](0004-language-parsers-behind-the-code-lexer-interface.md) | Use Roslyn for C# and Tree-sitter for later languages behind a common lexer interface, with parser SDKs outside Core. | Accepted |
| [0005-tool-result-previews-with-in-request-retrieval.md](0005-tool-result-previews-with-in-request-retrieval.md) | Large tool results reach the model as a deterministic line-subset preview plus a `proxy_retrieve` tool (lines, pattern or whole) that the proxy answers inside the same client request; Anthropic first. | Accepted; superseded in part by 0008 |
| [0006-one-tool-with-local-gain-and-retrieval-clients.md](0006-one-tool-with-local-gain-and-retrieval-clients.md) | The proxy serves gain and retrieval on a loopback API; `ai-proxy gain` and `ai-proxy mcp` ship in the one tool and reach the proxy only through that API. | Draft (boundary test note 2026-10-30) |
| [0007-restart-owned-instances-through-a-leased-control-pipe.md](0007-restart-owned-instances-through-a-leased-control-pipe.md) | Serialize startup and restart with a short claim, hold an independent process lease, and restart only through a same-user control pipe after exact-process exit. | Accepted |
| [0008-ccr-on-for-every-kind-and-content-retention.md](0008-ccr-on-for-every-kind-and-content-retention.md) | CCR is on for every content kind with no activation gate; the tool costs about 222 tokens; content records expire at 14 days. Supersedes parts of 0005. | Accepted |
