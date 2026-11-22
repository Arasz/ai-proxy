# 0018 - A token on the loopback API, and an audit row for each gain reset

Status: Accepted (owner rulings 2026-11-21, project review)

Amends: [ADR 0006](0006-one-tool-with-local-gain-and-retrieval-clients.md), the consequence that
the loopback API has no authentication (0006, lines 33-34).
[ADR 0010](0010-status-and-stop-reuse-the-control-pipe.md): `status` and `stop` stay on the
control pipe and need no token.

## Context

The gain log on this machine starts at sequence 49,704. Rows 1 to 49,703 went in one contiguous
cut on or before 2026-11-11 14:53, and only a reset deletes gain rows
([project review](../work/2026-11-21-project-review.md) F4, `sqlite3 -readonly ~/.ai-proxy/gain.db`).
Nothing records who ran that reset, when, or with which filter.

`POST /gain/reset` is mapped with no check beyond filter validation
(`src/AiProxy/Hosting/Api/GainEndpoints.cs:20-26`). The store deletes inside one transaction
bounded by a sequence snapshot (`src/AiProxy.Storage/SqliteGainStore.cs:96-115`, the delete at
`:252-254`) and writes no audit row. The only guards are the loopback `AllowedHosts` default
(`src/AiProxy/ProxyApplication.cs:27-30`) and the JSON content type. They stop a browser, not a
local process: a `curl -X POST .../gain/reset` against a probe instance returned 200 (F4). ADR
0006 accepted this: "on loopback only, with no authentication" (0006, lines 33-34). The same
holds for `POST /retrieval` (`GainEndpoints.cs:27`), which returns originals the user's agents
sent.

`/health` is mapped next to the other routes (`src/AiProxy/ProxyServiceExtensions.cs:148`).
`ApiCheck` probes it (`src/AiProxy/Doctor/ApiCheck.cs:20`), and the MCP server probes it before
it starts (`src/AiProxy/Mcp/McpServerProcess.cs:17`, called at `:42`). The data directory is
created 0700 on POSIX (`src/AiProxy/Hosting/Integration/TitaniumProxyService.cs:373-384`).
Clients find it the way `ProxyDataDirectory.Resolve` does: option, then `Proxy:DataDirectory`,
then the default (`src/AiProxy/Hosting/Proxy/ProxyDataDirectory.cs:6-10`).

## Decision

### Token

1. At each start the proxy writes a new random token (from the platform's cryptographic random
   source) to a file in the data directory, mode 0600 on POSIX. The old value stops working.
2. Every loopback API route except `/health` needs `Authorization: Bearer <token>`. A request
   without it, or with a wrong one, gets 401 and has no effect. The check is one middleware on
   the API pipeline, so a new route is covered without a change.
3. `/health` stays open. `ApiCheck` and `McpServerProcess.IsReachableAsync` call it before they
   read any token, and it returns only product and version.
4. A client finds the token through the data directory, in this order: a `--data-directory`
   option (on `gain` and `mcp`, and the existing ones on `doctor`), then `Proxy__DataDirectory`,
   then `~/.ai-proxy`. `replay` keeps its `--home` option for the same purpose. The CLI, the MCP
   retrieval tools and `doctor` send the token.
5. `status` and `stop` use the control pipe (ADR 0010) and need no token. The pipe stays their
   trust boundary.

### Reset audit

6. Each gain reset, from the CLI or over HTTP, writes one `gain_reset` row in the same
   transaction as the delete: UTC time, the filter, the removed count, and the caller.
7. The caller is a string the client sends about itself, for example `cli gain --reset pid 1234`.
   An HTTP reset without it is recorded with the caller empty.

## Consequences

Positive: a local process without read access to the data directory can no longer reset the
ledger or read originals. The next reset is traceable: time, filter, count and the client's own
name.

Negative: every client and many integration tests must carry the token (package P6b lists them).
The caller field is not authenticated; it says what the client claims. A token file on a
configured data directory on Windows has no ACL that ai-proxy sets, so there the token is only as
private as that folder. The cause of the 2026-11-11 reset stays unknown; this record cannot
recover it.

Neutral: the token protects against other users and sandboxed processes, not against the user's
own agents, which can read the file like any other user process.

## Alternatives considered

- A token on the reset route only: rejected by the owner's ruling. Retrieval returns originals,
  which are as sensitive as the reset.
- Remove `POST /gain/reset` and keep a CLI-only reset: rejected. The CLI reaches the store only
  through this API (ADR 0006), so the route is the only path.
- A token path that the proxy publishes for clients: rejected. A `--data-directory` option reuses
  the option that `stop`, `status` and `doctor` already have and needs no new discovery file.
- A second option on `replay` beside `--home`: rejected. Two names for one concept; the rename of
  the pair is deferred.

## Sources

- [Project review of 0.34.0](../work/2026-11-21-project-review.md), F4
- [Review fix plan](../work/2026-11-21-project-review-plan.md), packages P6a and P6b, Revision 1
  rows 9, 11 and 12, and the owner ruling on token scope
- Code: `src/AiProxy/Hosting/Api/GainEndpoints.cs`, `src/AiProxy.Storage/SqliteGainStore.cs`,
  `src/AiProxy/ProxyServiceExtensions.cs`, `src/AiProxy/Doctor/ApiCheck.cs`,
  `src/AiProxy/Mcp/McpServerProcess.cs`, `src/AiProxy/Hosting/Proxy/ProxyDataDirectory.cs`
