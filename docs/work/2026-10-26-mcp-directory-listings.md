# Research: ai-proxy MCP listings

**Date:** 2026-10-26
**Question:** How can we list the ai-proxy MCP server on Smithery, npm, PyPI, the Claude Directory, and ChatGPT/OpenAI, and what does each route require?

## Findings

### F1 — ai-proxy currently distributes its MCP command as a .NET tool with local stdio transport [READ]

The installable product is the `ai-proxy` .NET global tool. Its MCP subcommand launches a stdio server and exposes the `retrieve_original` read-only tool; users register it by launching `ai-proxy mcp`. The repo currently has no npm or Python package metadata intended to distribute this MCP server. It already has a NuGet listing and an M8ven MCP score badge.

**Evidence:** [README.md](../../README.md), install and MCP setup at lines 7-8, 40-48, 120-128; [CliApplication.cs](../../src/AiProxy/Cli/CliApplication.cs), lines 72-77; [0.15.0 changelog](../changelog/0.15.0-gain-and-retrieval-clients.md), lines 15-18; current tracked file inventory contains no MCP-specific `package.json`, `pyproject.toml`, `.mcp.json`, or Smithery manifest.

### F2 — Smithery is a public registry, but its connection proxy and CLI are scheduled to shut down October 31, 2026 [READ]

Smithery says its public directory remains, while Smithery-hosted connection URLs, API, CLI, and SDKs stop working October 31. Until that date, the current docs describe both URL publishing (public HTTPS Streamable HTTP) and local publishing as a pre-built MCPB bundle. Smithery explicitly supports stdio bundles, so it is the most plausible directory route for the existing local MCP, provided we package ai-proxy and its runtime/API dependency in a working MCPB. The registry remains after October 31, but hosted Smithery connection URLs will not; use a direct vendor endpoint or the bundle distribution rather than depending on Smithery hosting.

**Evidence:** [Smithery's future notice](https://smithery.ai/future); [Smithery publish documentation](https://smithery.ai/docs/build/publish) specifies HTTPS Streamable HTTP for URL publishing and pre-built MCPB for stdio publishing, with `smithery mcp publish ./server.mcpb -n ...`. The bundle packaging and launch behavior still need a dry run.

### F3 — npm and PyPI listings mean publishing installable packages, not registering an MCP endpoint [READ]

npm publishes packages from a directory containing `package.json`; PyPI publishes Python source/wheel distributions. Neither package registry is an MCP-specific catalogue. ai-proxy already has a NuGet tool package, so an npm or PyPI listing would require maintaining a real Node or Python wrapper/installer that launches the .NET tool, rather than uploading the existing binary as if it were a native package. That adds a separate package, runtime prerequisites, release workflow, and support surface.

**Evidence:** [npm `publish` docs](https://docs.npmjs.com/cli/v11/commands/npm-publish/) describe publishing a `package.json` folder; [Python Packaging User Guide](https://packaging.python.org/en/latest/tutorials/packaging-projects/) describes building and uploading Python distributions; [README install instructions](../../README.md#install) use `dotnet tool install --global ai-proxy`.

### F4 — Claude has a public connector directory with a submission portal for remote MCP servers [READ]

The Claude directory is a genuine public listing route. The current official Claude announcement says developers can submit either a single remote MCP connector or a plugin bundle through the directory portal, with review and publication. The current directory submission documentation requires a reachable HTTPS MCP endpoint for remote servers and listing information such as name, description, docs, privacy policy, support contact, icon, use cases, authentication, and data-handling information. Since ai-proxy's current MCP process is stdio and requires local access to a running proxy, it cannot be submitted as a remote connector unchanged. A remote-hosted variant would need careful isolation/auth because the data belongs to each user's local proxy.

**Evidence:** [Anthropic's September 25, 2026 announcement](https://claude.com/resources/articles/build-plugins-for-claude); [Claude Connectors Directory submission documentation](https://docs.claude.com/en/docs/connectors/building/submission) describes remote MCP and the portal. The direct documentation URL was represented by an Anthropic documentation mirror in search results; verify access to the canonical page before submission.

### F5 — ChatGPT has both private custom MCP connections and a separate public app/plugin directory [READ]

A workspace can add a public HTTPS MCP endpoint as a private custom connection, but that only publishes it inside that workspace. For public discovery, OpenAI requires submitting a ChatGPT app/plugin through the Developer Platform for review and approval. The app submission route expects app metadata, MCP connectivity details, testing/review materials, and privacy/safety compliance. OpenAI's docs mark local stdio submissions desktop-only and limited to approved partners, so ai-proxy's current stdio shape is a poor public-directory fit. A hosted remote MCP endpoint or a partner-approved desktop package would be needed.

**Evidence:** [OpenAI's app submission announcement](https://openai.com/index/developers-can-now-submit-apps-to-chatgpt/); [Upload and submit your plugin](https://developers.openai.com/plugins/deploy/submission); [desktop-only plugin submissions](https://developers.openai.com/plugins/deploy/desktop-only); [custom MCP server connections in ChatGPT](https://help.openai.com/en/articles/12584461-connecting-custom-mcp-servers-in-chatgpt).

### F6 — The simplest viable path is to strengthen the existing NuGet install and submit directories only if a remote service is part of the product [INFERRED]

This follows from F1-F5: the current tool is deliberately local and can already be installed through NuGet and registered by an MCP client. npm/PyPI would duplicate distribution without providing a directory listing, while Claude and ChatGPT's public listings require a remote endpoint or a desktop submission route. A remote service is an architectural/product decision because this MCP tool retrieves users' locally stored request originals; making that data remotely reachable changes its trust boundary. The near-term low-risk discovery work is improve the NuGet README, keep the M8ven profile current, and publish a Smithery listing only if its bundle flow supports the local executable cleanly.

## Still open

- Does Smithery's current UI accept a .NET global tool as a local MCPB/bundle, or does it require building a self-contained bundle specifically? Confirm by dry-running the current submission route.
- Is there a product-approved hosted MCP design for retrieving per-user local originals, including auth, tenancy, retention, and consent? Without that, Claude and ChatGPT public remote directory submissions are not ready.
- Does ai-proxy have access to the required paid Claude plan and Developer Platform organization/verification needed to submit and review listings? Account entitlements were not inspected.
- Should we invest in npm/PyPI wrapper packages for discoverability/install convenience, or keep NuGet as the canonical package and use registries that list MCP servers directly?
