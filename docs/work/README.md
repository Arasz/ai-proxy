# work/

Dated work records: plans, designs, research, reviews, incidents, backlog. Every file is
named `YYYY-MM-DD-slug.md` and is a snapshot — it records what was true when it was
written, not what is true now.

## Contents

Active records:

| File | What it is |
|---|---|
| [2026-10-21-ai-proxy-v1-implementation-plan.md](2026-10-21-ai-proxy-v1-implementation-plan.md) | Work record (the continuation step after the scaffold): what v1 adds — the router, the forwarder, the credential rule, the pass-through — with the acceptance evidence for each, and the five questions the research record leaves open. |
| [2026-10-21-v1-phase1-structure-and-proxy-skeleton-plan.md](2026-10-21-v1-phase1-structure-and-proxy-skeleton-plan.md) | Work record (the first implementation phase of v1): the structure, the composite detection, the compression seam, the router and the forwarder, with the test design that came before the first test was written. |
| [2026-10-22-content-compression-pipeline-architecture.md](2026-10-22-content-compression-pipeline-architecture.md) | Design record: the router, the reducer per payload kind, the decomposer, the compositor and the store, with the five decisions the design needed reviewed. |
| [2026-10-22-content-decomposition-and-format-detection.md](2026-10-22-content-decomposition-and-format-detection.md) | Research record: the measured reduction recipes recovered from the benchmark scripts, which formats a parse can prove and which are a guess, and the five rules that close the decisions-class hole in the must-keep overlay. |
| [2026-10-22-sqlite-pubsub-and-store-layout.md](2026-10-22-sqlite-pubsub-and-store-layout.md) | Research record: what the managed SQLite stack can and cannot do for a pub/sub channel, and where the store lives in a repository whose core project depends on nothing. |
| [2026-10-22-compression-design-gate-audit.md](2026-10-22-compression-design-gate-audit.md) | Review record: the design audited before it was implemented, with the blockers and defects it found and the rulings on the five open decisions. |
| [2026-10-22-test-quality-review.md](2026-10-22-test-quality-review.md) | Review record: whether the tests can fail, what each fake hides, and the coverage map from the design's promises to the tests that pin them. |
| [2026-10-22-implementation-review.md](2026-10-22-implementation-review.md) | Review record: the implementation read against the design and the measured rules, with the claims the pull request and the changelog make checked against the code. |
| [2026-10-22-dotnet-review.md](2026-10-22-dotnet-review.md) | Review record: the .NET idiom, the cost of the hot path, the worker's connection lifetime and the publishing path, read by a .NET engineer. |
| [2026-10-22-compression-test-honesty-audit.md](2026-10-22-compression-test-honesty-audit.md) | Review record: what a test of this feature would have to fake, what each fake would hide, and the checks that would catch it. |
| [2026-10-21-detecting-the-provider-and-forwarding-to-the-right-upstream.md](2026-10-21-detecting-the-provider-and-forwarding-to-the-right-upstream.md) | Research record: the origin document of this project. Can a .NET proxy detect which LLM provider a request belongs to and forward it to the right upstream, and can it pass other local traffic through? Ten findings, graded — 7 measured, 2 read, 1 unverified — including the credential-leak measurement that fixes the credential-substitution rule, and the pass-through measurement that carries ai-raccoon's MCP traffic byte for byte. |
