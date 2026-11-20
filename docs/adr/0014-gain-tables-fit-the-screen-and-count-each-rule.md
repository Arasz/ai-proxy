# 0014 - Gain tables fit the screen and count each rule

Status: Accepted (owner request 2026-11-18); amended 2026-11-20 (harness by host)

## Context

`ai-proxy gain` is the proxy's only view a person reads. On 0.29.1 it was hard to read:

- The default view printed one ten-column table per harness. At 80 columns every header wrapped
  mid-word (`Request│s`, `Origina│l`).
- `--by-rule` grouped by the full rule path that `ContentCompressionPipeline` records, for example
  `below-minimum,json.compact,ccr.preview`. The table had one row per combination, and most rows
  wrapped over several lines. It could not answer "what does `json.compact` save?".
- `--history` printed the full request GUID and project path, so each row took four lines.
- The output had no colour, so a loss looked the same as a saving.

The store keeps one rule string per request. It does not keep the tokens each rule saved.

## Decision

1. **One row per rule.** `GainReportBuilder` splits the rule path at `,` and adds the request's
   totals to each rule it names. A request that used three rules counts under all three, so the
   rows overlap and do not sum to the summary. The view says so under the table. Other dimensions
   are unchanged.
2. **`--rule X` matches a member of the path**, not the whole string, so the filter and the table
   name the same thing. `--reset --rule X` deletes the same rows the filter shows, so it also removes
   requests where `X` ran together with other rules. A value with a comma (an old full path) still
   matches only that exact path, so a reset written for 0.29 does not grow.
3. **One summary table, one row per harness**, plus a `total` row when more than one harness has
   traffic. Short headers. The table fits in 80 columns. Other traffic stays a line under it.
4. **Colour by sign.** Saved, net and percent cells are green when positive, red when negative and
   grey when zero. Headers are bold. Spectre drops colour when the output is not a terminal or
   `NO_COLOR` is set.
5. **Shortened identifiers in tables.** Request and preview ids show their last 8 characters, the
   project shows its last folder name, and times drop the year. `--format json` and `--format csv`
   keep the full values.
6. Attribution tables sort by saved tokens, largest first.

## Consequences

Positive: every default table fits in 80 columns, and `--by-rule` gives one short row per rule.

Negative: per-rule rows show the savings of the requests that used the rule, not the rule's own
share. A rule that always runs with a stronger one looks better than it is.

Neutral: the JSON report's `attributions` for `rule` now overlap. A client that summed them gets a
larger number than the summary. No client in this repository sums them.

Confirmation: unit tests render the view on a `TestConsole` of width 80 and assert that no line is
longer; a builder test asserts a mixed-rule request counts under each rule; a store test asserts
`--rule json.compact` matches a combined path.

## Alternatives

- Do nothing: rejected; the owner asked for a readable view.
- Record each rule's own saved tokens at compression time: exact, but it needs a new store field,
  a migration, and old rows would stay unattributed. Kept as a later option.
- Keep combination rows and only fix the layout: rejected; it still cannot answer what one rule
  saves.
- Transpose the summary (harnesses as columns): fits easily, but the owner chose harness rows.

## Amendment 2026-11-20: harness by host

Status: Accepted (owner rulings 2026-11-20, plan review)

`GainHarness.Of` labels every row of the `openai` provider "codex"
(`src/AiProxy.Core/Gain/GainHarness.cs:15-22`). The 11-20 record found that 221 of those rows,
56.7M original tokens, were the pi agent's DeepSeek requests on OpenRouter, not Codex
([token return record](../work/2026-11-20-token-return-vs-rtk.md) F2). The provider alone cannot
tell the two apart, because both use the OpenAI dialect.

1. Each request row in the ledger carries the request host. `GainHistoryEntry` gets a `Host`
   field, filled where the proxy writes the row
   (`src/AiProxy/Hosting/Integration/ProviderRequestInspector.cs:412`).
2. The harness label comes from provider and host together:

   | Provider | Host | Harness |
   |---|---|---|
   | `anthropic` | any | `claude` |
   | `openai` | `chatgpt.com` | `codex` |
   | `openai` | `openrouter.ai` or a subdomain such as `eu.openrouter.ai` | `openrouter` |
   | `openai` | `api.openai.com` or a regional `*.api.openai.com` | `openai` |
   | `openai` | any other host | `other` |
   | anything else | any | `other` |

   Host matching ignores case and a trailing dot. A look-alike such as `evilopenrouter.ai` is
   `other`, not `openrouter`.
3. Follow-up and recall rows carry the host of the request they belong to, so they count under the
   same harness as that request.
4. Rows written before this change have no host. An `openai` row without a host shows as
   `openai (host unknown)`, not `codex`. Old rows are not rewritten, because the ledger never kept
   the host and guessing it from tokens would be a guess presented as data.

Positive: pi traffic on OpenRouter gets its own row, and the `codex` row counts only Codex. Negative:
the history before the change stays in one mixed row under a new name. The summary table can grow
to six harness rows plus `total`, and decision 3 above still requires it to fit in 80 columns.
