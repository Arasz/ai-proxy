# 0006 - One tool, with local gain and retrieval clients

Status: Draft (2026-10-26)

## Context

The proxy measures what it removes from requests and keeps originals for retrieval (ADR 0005), but
nothing outside the process could read either. Two readers are wanted: a person checking savings in
a terminal, and an agent reading an original back in a later turn.

## Decision

- The proxy serves `GET /gain`, `POST /gain/reset` and `POST /retrieval` on a loopback listener,
  `http://127.0.0.1:17432` by default. An explicit `urls`, `HTTP_PORTS`, `HTTPS_PORTS` or Kestrel
  endpoint wins.
- The clients reach the proxy only through that API (`AiProxy.Client`), never through its storage.
  A test fails if `AiProxy.Mcp` or `AiProxy.Cli` references `AiProxy.Storage` or SQLite.
  These live in the host assembly, which references both, so the test checks namespaces and
  method bodies, not assembly references (2026-10-30, project-scope review F13).
- Both clients ship inside the `ai-proxy` tool as subcommands, `ai-proxy gain` (Spectre.Console) and
  `ai-proxy mcp` (ModelContextProtocol SDK, stdio), so there is one package, one install and one
  version to keep in step with the API.
- Project attribution comes only from the `X-Ai-Proxy-Project` request header, which the proxy strips
  before forwarding. Missing attribution stays unknown; it is never inferred.
- Savings are labelled estimates (2.89 characters per token, initial request only); retrieval cost
  is reported separately rather than netted.

## Consequences

- The proxy package grows by the Spectre and MCP SDK assemblies.
- Anything local can read `/gain` and `/retrieval`; they hold token counts and originals the user's
  own agents sent, on loopback only, with no authentication. A multi-user machine would need more.
- Only thread-mode originals are retrievable after their request ends.
