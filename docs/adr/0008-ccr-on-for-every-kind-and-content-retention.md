# 0008 - CCR is on for every kind, and content records expire

Status: Accepted (owner, 2026-10-30)

Supersedes, in [ADR 0005](0005-tool-result-previews-with-in-request-retrieval.md):

- §Configuration, the `Proxy:Ccr:Enabled` default (`false` there).
- §Activation gate, the whole section.
- §Storage and eviction, the last sentence ("The existing `/content/{requestId}` endpoint and
  `content_request` record are left as they are").
- The tool cost of 147 estimated tokens in §Context, §Cache placement and §Consequences.

Every other part of ADR 0005 stands.

## Context

ADR 0005 shipped CCR off, with a per-kind gate: a kind would preview only after a retrieval-aware
re-score kept every answer, retrieved on at most 25% of reader calls and sent at most 80% of its
tokens net. Release 0.10.0 turned CCR on for every kind instead and said so in its changelog
(`docs/changelog/0.10.0-ccr-runtime-on-by-default.md`). The ADR was never amended. Since then the
code and the record have given two answers (project-scope review, F12).

The review also measured three things the old record got wrong or left open:

- The appended `proxy_retrieve` definition is 641 bytes, 642 with its separating comma
  (`src/AiProxy.Core/Content/Ccr/CcrRetrieval.cs:12`). At the gain estimate of 2.89 characters per
  token (`src/AiProxy.Core/Gain/GainReportBuilder.cs:6`) that is about 222 tokens, not 147.
- The organic retrieval rate is unmeasured. The only recalls on record came from the review's
  own agents, on one day (F16).
- `content_request` and `content_chunk` kept every full request body with no retention: 199 MB
  of originals on one heavy day (F3).

## Decision

1. `Proxy:Ccr:Enabled` defaults to `true`, and every content kind previews once its reduced region
   reaches 3,000 estimated tokens. There is no per-kind switch.
2. The activation gate is withdrawn. It is not owed. The organic retrieval rate is a monitoring
   item: measure it on a normal working week, excluding review traffic by date, and reopen this
   decision if one kind retrieves on most of its previews.
3. The tool costs about 222 estimated tokens. ADR 0005's cache arithmetic scales by the same
   factor: about 278 token-equivalents to write once at 1.25x, and about 11 per later turn at
   0.05x. A 3,000-token preview still repays it on its first send.
4. `content_request` and `content_chunk` expire 14 days after they are written, the same window
   as `ccr_original` and `ccr_thread`. `GET /content/{requestId}` then answers 404 for an expired
   record. Retrieval is unaffected, because MCP and in-request retrieval read `ccr_original`, which
   expires at the same age.

## Consequences

The record now matches what ships. A user who wants the reducers without previews still sets
`PROXY__CCR__ENABLED=false`.

No measurement backs "every kind" as better than a gated subset. Removing the gate is a
maintenance decision, because the gate never ran. It is not an efficacy result, and this record
must not be quoted as one.

Content records are no longer a permanent archive. Anyone who relied on `/content/{id}` for an
old request loses it after 14 days.

## Rejected

- **Keep the gate as owed.** The re-score needs paid reader runs this owner cannot make (no paid
  LLM credentials, 2026-10-24), so "owed" would mean "never".
- **Amend ADR 0005 in place.** It is Accepted and therefore frozen (`docs/adr/README.md`).
