# Release notes draft: project review fix campaign

Running draft for the release that closes the
[project review](2026-11-21-project-review.md) campaign
([plan](2026-11-21-project-review-plan.md)). The version number is set in package V; until then
this file is the only place the notes live. Amended at the end of each wave.

## Merged

- **Decisions.** ADR 0017 (root names exact hosts), 0018 (loopback API token and reset audit),
  0019 (capture evicts the oldest exchanges at its cap; amended to keep two writes per exchange),
  0020 (rolling log file), 0021 (schema ladder per store), and a follow-up deadline line in
  ADR 0005. (#141, #146)
- **Replay cannot reach the live stores.** `replay` now registers only the compression pipeline
  and its read-only readers. The boundary test follows the whole chain of types, not one hop.
  (P12, #142)
- **Log event ids are unique.** The content store worker now uses 1204 and 1205, and the MCP
  stdin give-up warning uses 1615. A log filter on the old ids 1201, 1202 or 1601 must change. A
  test fails when two log messages share an id. Five tests pin known behaviour that a cleanup
  could break. (P14, #143)
- **Compression core.** The must-keep identifier list of a preview no longer fills with the line
  numbers of a numbered file read; numbers in tab-separated output are kept. A request that
  already has `stream_options` gets `include_usage` spliced in, and every other byte stays the
  same, so the prompt cache keeps matching. A test bounds the failed structure scans at 32.
  (P9, #144)
  - Replay of the stored bodies since 2026-11-20 (2,080 rows, run back to back on 2026-11-21):
    0.34.0 saves 1,345,827 tokens, P9 saves 1,345,831. The change does not move savings; it
    changes which names a preview keeps.
- **Log file.** The proxy writes its log to `proxy.log` in the data directory as well as to the
  console. It rolls once at 5 MiB to `proxy.1.log`, and on POSIX only the owner can read it.
  (P3, #145)
- **Typos no longer start the proxy.** A mistyped command (`ai-proxy stauts`) or a start line with
  a stray `--key` and no value prints the usage on stderr and exits 1. `stop`, `status`, `gain`
  and `replay` options with no value at the end of the line are rejected by name. (P1, #147)
- **Tables fit 80 columns.** `gain` and `replay` print every row on one line; numbers and headers
  are never split. The summary and replay kind tables drop their outer box, and the unknown-host
  OpenAI row shows as `openai (no host)` on screen (JSON and CSV keep `openai (host unknown)`).
  (P2, #148)
- **Capture keeps running at its cap.** At `Proxy:CaptureMaxBytes`, each write deletes the oldest
  whole request and response pairs to make room, walking the time index. A single write larger
  than the cap is rejected and recorded. capture.db carries a schema version, and a file written
  by a newer build refuses to open. (P4, #149)
- **Content store eviction** deletes at most 16 MiB of originals per batch, so it no longer holds
  the write lock longer than a request waits; a row refreshed during eviction is kept. content.db
  opens through the shared schema ladder. (P5, #154)
- **Gain ledger.** A reset writes a `gain_reset` row (time, filter, removed count, caller) in the
  same transaction as the delete. The report reads the ledger in one query with unchanged output;
  on a copy of the live ledger it took 0.41 s instead of 1.24 s (observation). gain.db has a
  schema version. (P6a, #156)
- **CI.** A PR that changes `VERSION` must add its changelog entry and a version above the latest
  tag. `publish` builds the tagged commit. The `labeled` trigger no longer cancels a PR's first
  run, lifecycle runs on main are not cancelled, and the Python script tests run in CI. (P8, #152)
- **Retrieval follow-up deadline.** A follow-up that gets no response headers in 10 minutes ends
  the turn with the dialect's error event instead of pinging forever. `Proxy:Ccr:KeepAliveInterval`
  must be positive and `Proxy:Compression:Minimum` at least 1; nested settings sections are now
  validated at start. (P10, #150)
- **MCP schema.** `retrieve_original` publishes the schema the proxy enforces, and a bad-input
  error names the allowed values. (P11, #153)
- **Status and doctor show capture.** `status` prints capture on or off, size against the cap,
  oldest row, last eviction, last write error and the `proxy.log` path. `doctor` warns on a
  write failure in the last hour, or capture off while `Proxy__Capture` asks for it. A capture
  fault never stops the control pipe. (P13, #155)

## In review

- P7 root names exact hosts and rotates once. At the first start after release the root is
  replaced, and macOS shows one trust prompt.
- P6b loopback API token.
