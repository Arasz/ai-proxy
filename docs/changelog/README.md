# changelog/

One file per release, `{version}-{slug}.md`, written in the same PR that bumps the root
`VERSION` file. Each entry says what changed for a user of the installed tool, and names the
PRs it ships.

A PR that leaves `VERSION` alone cuts no release and adds no file: the entry of the release
that next bumps `VERSION` describes and names it, so several PRs can ship as one release.

The root `VERSION` file is the version source of truth. The build reads it
(`Directory.Build.props`), and the release workflow tags on it. This directory holds the
per-release entries and nothing else — it has no `VERSION` file of its own, because a second
copy of the version is a second thing to keep in step.

## Index

Newest first; add each release's line in the same PR that adds its file.

| Version | What changed | PRs |
|---|---|---|
| [0.18.1](0.18.1-null-check-analyzer.md) | Build rejects `ThrowIfNull` on non-nullable values (APGEN003); primary constructors; injectable `ProxyLease`. | Pending |
| [0.18.0](0.18.0-review-campaign.md) | Strict startup and `--version`; 14-day retention for content and capture with a capture ceiling; unescaped compressed output; relay headers deadline and no redirects; ADR 0008. | [#90](https://github.com/Arasz/ai-proxy/pull/90) |
| [0.17.3](0.17.3-adjacent-line-dedupe.md) | Drop only adjacent exact repeats, so source code read through a shell keeps its braces and repeated lines. | Pending |
| [0.17.2](0.17.2-marker-owner-pid.md) | Report an unambiguous marker-holder PID when an older proxy needs a manual stop. | Pending |
| [0.17.1](0.17.1-control-pipe-and-port-binding-fixes.md) | Disconnect server control pipe unconditionally to tolerate broken client pipes, normalize empty proxy port before binding, and set persist-credentials false in lifecycle CI. | [#86](https://github.com/Arasz/ai-proxy/pull/86) |
| [0.17.0](0.17.0-owned-instance-restart.md) | Gracefully restart an owned instance, retain its ports, serialize competing starts, and configure isolated runtime state. | [#85](https://github.com/Arasz/ai-proxy/pull/85) |
| [0.16.1](0.16.1-harness-arguments.md) | Preserve empty harness arguments and pass child options literally after the launcher separator. | [#84](https://github.com/Arasz/ai-proxy/pull/84) |
| [0.16.0](0.16.0-claude-launcher.md) | Launch Claude Code through the running proxy; root help avoids startup, and compression summaries are logged again. | #83 |
| [0.15.3](0.15.3-nuget-readme-images.md) | The token icon uses NuGet-supported Markdown, the version badge URL is fixed, and M8ven uses a text link. | #82 |
| [0.15.2](0.15.2-public-ca-and-install-guide.md) | Startup exports the public CA as PEM; the README documents installed-tool and agent setup and shows the package icon. | #80 |
| [0.15.1](0.15.1-sql-gain-and-codex-project-header.md) | Gain reports aggregate in SQLite with bounded detail reads; the guide documents verified Codex project headers. Grammar-load warnings and Python fallback literal preservation also ship. | #77, #78 |
| [0.15.0](0.15.0-gain-and-retrieval-clients.md) | `ai-proxy gain` and `ai-proxy mcp` read a new gain ledger and kept originals over a loopback API on port 17432; the health and content endpoints move there from 5000. | #73 |
| [0.14.1](0.14.1-trust-root-certificate-once.md) | macOS start no longer asks three times to approve keychain trust: the root certificate is trusted only when it is not trusted already. | #76 |
| [0.14.0](0.14.0-javascript-typescript-code-views.md) | A whole JavaScript or TypeScript file (`.js`, `.mjs`, `.cjs`, `.jsx`, `.ts`, `.tsx`) a tool call read gets the code view; `.jsx` now counts as source code. | #75 |
| [0.12.0](0.12.0-python-code-view.md) | A whole Python file a tool call read gets the code view: comments and `import` lines removed, exact text back through `proxy_retrieve`. | #74 |
| [0.11.0](0.11.0-csharp-code-view-on-by-default.md) | A whole C# file a tool call read goes upstream with comments and `using` lines removed, and `proxy_retrieve` returns the exact file; on by default, `PROXY__COMPRESSION__CODEVIEWS=false` switches it off. | #72 |
| [0.9.0](0.9.0-proxy-settings-section.md) | **Breaking.** Settings move from flat `PROXY_*` variables to the `Proxy` configuration section (`PROXY__PORT`, `PROXY__COMPRESSION__ENABLED`, ...); switches take `true` or `false`. | #69 |
| [0.10.0](0.10.0-ccr-runtime-on-by-default.md) | CCR runs: large tool results become previews, `proxy_retrieve` is answered inside the request, and compression and CCR are on by default. | #71 |
| [0.8.0](0.8.0-regional-provider-hosts.md) | The regional OpenAI and OpenRouter API hosts are recognized like the main ones; matching stays exact, so `mcp.openrouter.ai` stays pass-through. | #46 |
| [0.7.1](0.7.1-upstream-drop-closes-client.md) | An upstream that drops a chunked response between chunks now fails at the client at once, instead of leaving it waiting until its own timeout. | #40 |
| [0.7.0](0.7.0-response-usage-and-timings.md) | With capture on, each captured request also records its response's token usage, sizes and per-stage timings, ours and Titanium's; no response body or header is stored. | #37 |
| [0.6.0](0.6.0-request-capture.md) | `PROXY_CAPTURE=on` keeps every provider request body, as sent, in a user-only local file; headers and query strings are not stored. | #32 |
| [0.5.1](0.5.1-failing-reducer-keeps-its-part.md) | A reducer that throws leaves only its own part unreduced and logs a warning; the other parts in the request are still reduced. | #31 |
| [0.5.0](0.5.0-configurable-proxy-port.md) | `PROXY_PORT` sets the listener and system proxy port; an invalid value or a port in use stops startup. | #27 |
| [0.4.1](0.4.1-clear-system-proxy-after-crash.md) | The system proxy is cleared after a forced kill, by a guard process or at the next start; a proxy set before the run is not restored in that case. | #23 |
| [0.4.0](0.4.0-machine-wide-forward-proxy.md) | Machine-wide forward proxy with provider-only HTTPS inspection and default pass-through. | #19 |
| [0.3.1](0.3.1-the-reducers-change-the-text-not-the-formatting.md) | The reducers change the text and not the formatting: the XML reducer compacts again instead of falling back to the line rule, and the whitespace, the comments and the hash lines that are content now survive the reduction. | #15 |
| [0.3.0](0.3.0-content-compression-pipeline.md) | The content compression pipeline: the tool output in a request is reduced before the provider sees it, and the original is kept and served back. | #4 |
| [0.2.0](0.2.0-v1-phase1-proxy-skeleton.md) | The proxy skeleton: the provider is detected, the request is compressed and forwarded to the right upstream, and other traffic is carried through unchanged. | #2 |
