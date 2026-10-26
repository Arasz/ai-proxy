# Research: Codex project header and SQL gain reports

Task aip-validate-codex-header-aggregate-gain, low loop, base c2088b8.

- User scope: verify Codex custom-header option for X-Ai-Proxy-Project; avoid loading all ledger events for GET /gain and CLI --watch.
- Source trace: GainWatch.cs:21-22 -> ProxyClient.GetGainAsync (ProxyClient.cs:30) -> GainEndpoints.cs:19-25 -> IGainStore.QueryAsync -> SqliteGainStore.cs:45-60 SELECT/deserializes all matching JSON -> GainReportBuilder.cs:17-40 groups in memory. Introduced in 5b3a4b0 (#73).
- Existing payloads already persist per-request rounded token totals. SUM these fields in SQL; recomputing tokens from summed characters changes semantics. SQLite json_extract can aggregate existing payloads without schema migration.
- Existing one-SELECT path returns one coherent snapshot. Multiple new aggregate/detail queries must use one deferred read transaction.
- Buckets are exact UTC day, Monday week, month. Integer ticks must establish day/week and integer Unix seconds month inputs, preserving one-tick-before-boundary values.
- Gain guide docs/how-to/reading-gain-and-retrieving-originals.md:36-44 currently documents only Claude project headers.
- Codex lane observed codex-cli 0.164.0 in isolated temporary configs/workspaces, mock loopback server and fake key. POST /v1/responses: no header option => absent header; env_http_headers enabled with AI_PROXY_CODEX_PROJECT equal to subprocess cwd => exact cwd header; missing environment variable => absent; static http_headers => exact static header. Mock deliberately returns HTTP400 after capture, expected CLI exit1 and no config error. Scripts /tmp/ai-proxy-codex-header-probe.py and /tmp/ai-proxy-codex-header-negative-probe.py are reproduction evidence, not repository artifacts.
- Official Codex sources: https://learn.chatgpt.com/docs/config-file/config-advanced#custom-model-providers and https://learn.chatgpt.com/docs/enterprise/sign-in-with-chatgpt-through-a-gateway#add-routing-headers-optional. Header maps belong in the selected custom provider table; built-in provider IDs are reserved. ChatGPT gateway auth is documented but not authenticated in local probe. Preserve existing provider base URL/auth; port8080 is a forward proxy, not a model base URL.
- Main checkout contains pre-existing tracker modifications and content/; untouched. Separate active publisher worktree announced through project bus, no confirmed SQL overlap.
