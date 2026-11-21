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

## In review

- P1 typos and dangling options print usage (#147), P2 tables at 80 columns (#148), P4 capture at
  its cap and capture.db ladder (#149), P10 follow-up deadline and setting ranges (#150).
