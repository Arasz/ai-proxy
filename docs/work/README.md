# work/

Dated work records: plans, designs, research, reviews, incidents, backlog. Every file is
named `YYYY-MM-DD-slug.md` and is a snapshot — it records what was true when it was
written, not what is true now.

## Contents

Active records:

| File | What it is |
|---|---|
| [2026-10-21-ai-proxy-v1-implementation-plan.md](2026-10-21-ai-proxy-v1-implementation-plan.md) | Work record (the continuation step after the scaffold): what v1 adds — the router, the forwarder, the credential rule, the pass-through — with the acceptance evidence for each, and the five questions the research record leaves open. |
| [2026-10-21-detecting-the-provider-and-forwarding-to-the-right-upstream.md](2026-10-21-detecting-the-provider-and-forwarding-to-the-right-upstream.md) | Research record: the origin document of this project. Can a .NET proxy detect which LLM provider a request belongs to and forward it to the right upstream, and can it pass other local traffic through? Ten findings, graded — 7 measured, 2 read, 1 unverified — including the credential-leak measurement that fixes the credential-substitution rule, and the pass-through measurement that carries ai-raccoon's MCP traffic byte for byte. |
