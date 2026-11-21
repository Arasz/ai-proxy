# 0019 - Capture evicts its oldest rows at the cap

Status: Accepted (plan Revision 1, 2026-11-21; orchestrator in away mode, owner may reverse)

Amends: no earlier ADR holds the capture cap rule. The stop-at-cap behaviour shipped with the
0.6.0 request capture and is described in `docs/reference/environment-variables.md`
(`PROXY__CAPTUREMAXBYTES`). This record replaces that behaviour. It also extends the `status`
reply of [ADR 0010](0010-status-and-stop-reuse-the-control-pipe.md) with capture state.

## Context

Capture checks the cap before each write and drops the write when the file is full
(`src/AiProxy.Storage/SqliteCaptureStore.cs:71-74` for the request, `:100-103` for the
response). Full means live pages above the cap (`:132-152`), 1 GB by default (`:30`). The only
eviction deletes rows older than 14 days (`:59`), run once an hour by
`src/AiProxy/Hosting/Integration/CaptureEvictionWorker.cs:17`. So a full file stays full until its
oldest row ages out.

Measured on a `.backup` copy at 2026-11-21 10:52 UTC: live pages grew 37.9 MB per hour, and a
14-day window at that rate needs about 12.7 GB
([project review](../work/2026-11-21-project-review.md) F3, corrected in its verification
table). At the 1 GB default, capture would have stopped about 2026-11-21 23:50 UTC and stayed off
until about 2026-12-04 (F3). The owner raised the live cap to 8 GiB on 2026-11-21; that cap is
reached about 2026-11-30 (F30, inferred from the same rate).

The request and the response check the cap in separate calls, so a request row can be kept
without its response (F3). The stop is logged once as EventId 1502 (`SqliteCaptureStore.cs:249`),
and nothing else reports it: `status` shows pid, start time, URLs and the data directory only
(`src/AiProxy/Cli/StatusView.cs:8`), built from `ProxyControl.ProxyInfo`
(`src/AiProxy/Hosting/Proxy/ProxyControl.cs:227`) (F25).

## Decision

1. Capture keeps writing at its cap. A request and its response are stored as one pair in one
   transaction. Before the insert, that transaction deletes the oldest rows, by capture time,
   until live pages plus the pair fit under the cap.
2. The cap is checked once per pair, so a request row is never kept without its response. If the
   insert fails, the transaction rolls back and neither row stays.
3. A pair larger than the cap on its own is rejected and recorded as a write error. It evicts
   nothing.
4. The 14-day age prune stays as a second bound and still runs below the cap.
5. The store exposes a `CaptureState` record: live size, cap, oldest row time, last eviction time,
   and last write error. The control pipe's `info` reply carries it, so `status` and `doctor` can
   show it without the loopback API.
6. Each eviction is logged with its own EventId.
7. `Proxy:CaptureMaxBytes` keeps its name and meaning (live pages, not file size). Its default
   stays as it is.

## Consequences

Positive: capture no longer goes dark for days at the cap. The newest traffic, which a
measurement run wants, is always kept. A stop or an error is visible in `status` and `doctor`.

Negative: at the cap the capture window is shorter than 14 days, and the oldest rows of a long
run go first. The one-week capture for the token-return work (to about 2026-11-27) fits under
8 GiB at 37.9 MB per hour (about 9 days); if the release goes live later, eviction removes its
oldest rows. Each write that evicts holds the write lock a little longer.

Neutral: the control-pipe reply grows by one record, because `ProxyInfo` gains a field. Package
P13 checks how a `status` from the previous release reads the longer reply.

## Alternatives considered

- Raise the default cap to 13 GB: rejected. It postpones the stop (F30 shows how) and the owner
  ruled eviction.
- Keep stop-at-cap and only report it in `status`: rejected. A visible outage is still an
  outage.
- Evict in the hourly worker instead of the write transaction: rejected. Between passes the store
  is full and drops writes, which is the defect.

## Sources

- [Project review of 0.34.0](../work/2026-11-21-project-review.md), F3, F30, F25
- [Review fix plan](../work/2026-11-21-project-review-plan.md), packages P4 and P13
- Code: `src/AiProxy.Storage/SqliteCaptureStore.cs`,
  `src/AiProxy/Hosting/Integration/CaptureEvictionWorker.cs`,
  `src/AiProxy/Hosting/Proxy/ProxyControl.cs`, `src/AiProxy/Cli/StatusView.cs`
