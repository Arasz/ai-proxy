# Research: compression state in ai-proxy, extractive-model readiness, and the rtk pipeline configuration

**Date:** 2026-10-23
**Question:** Where does ai-proxy's compression stage stand, can its extractive model run today,
and is the logic in the user's rtk pipeline configuration sound alongside it?

## Findings

### F1 — The rule-based compression stage is built, wired, opt-in and green [MEASURED]

`ProxyApplication` reads `CompressionSettings` and registers `ContentCompressionPipeline` only
when `PROXY_COMPRESSION=on|true`. Core has region finding, decomposition, a router, a compositor,
a content store, a must-keep overlay and eleven handlers (log, JSON, XML, TOML, Markdown, shell
transcript, source code, prose, directory listing, pass-through). 23 test files touch it; the
whole suite passes.

**Evidence:** `dotnet test` on the M4 (darwin arm64, net10.0, main at `c1ea168`), 2026-10-23:
154 passed, 0 failed, 4.3 s wall. Wiring: `src/AiProxy/ProxyApplication.cs:23,33,57,99`;
switch: `src/AiProxy/Configuration/CompressionSettings.cs:17-19`.

### F2 — The prose path is line de-duplication plus overlay, with no measured benefit [READ]

`ProseCompressionHandler` reports `prose.dedupe`: it de-duplicates lines and re-applies the
must-keep overlay. The research record says the 0.902 prose figure belonged to the model, the
measured script's dedup was a no-op, and the owner shipped the rule path anyway.

**Evidence:** `src/AiProxy.Core/Content/Handlers/ProseCompressionHandler.cs:13-20`;
`docs/work/2026-10-22-on-device-compression-models.md:246-251`.

### F3 — The model is not ready to run inside ai-proxy: no loader, no runtime package [MEASURED]

`PROXY_COMPRESSION_MODEL` is documented as reserved and is read nowhere in `src` or `tests`.
No ONNX Runtime or tokenizer package is in central package management. Setting the variable
changes nothing.

**Evidence:** `rtk bypass grep -rln "COMPRESSION_MODEL|Onnx|onnx|InferenceSession|LLMLingua|Lingua" src tests Directory.Packages.props`
→ exit 1, no matches. `Directory.Packages.props:7-15` lists only Titanium.Web.Proxy, Dapper,
Sqlite, test packages. Docs: `docs/reference/environment-variables.md:28`,
`docs/how-to/turning-on-the-compression-stage.md:67-73`.

### F4 — The model artifact itself exists on this machine [MEASURED]

`kompress-int8-wo.onnx` (261.4 MB, the variant the record measured), `kompress-fp32.onnx`
(572.7 MB), `tokenizer.json`, static-shape fp16/fp32 variants and CoreML exports are present in
the sibling repo. Only the C# integration is missing.

**Evidence:** `ls -la ~/RiderProjects/models-conversion/models/kompress/` on the M4, 2026-10-23.

### F5 — The research record's raw evidence directory is gone [MEASURED]

The record cites `~/kompress-work` for scripts, `reducer_benchmark.json` and agent notes. The
directory does not exist, so F1-F2 of that record can no longer be re-derived from local files;
the `models-conversion` copy of the record is the remaining source.

**Evidence:** `ls ~/kompress-work` → "No such file or directory", 2026-10-23.
Citation: `docs/work/2026-10-22-on-device-compression-models.md:13-16,283-286`.

### F6 — rtk-proxy owns 127.0.0.1:8080, the port ai-proxy hard-binds [MEASURED]

`rtk-proxy` 1.0.1 (pid 94269, started 08:42 local) listens on `127.0.0.1:8080`, and the shell
exports `ANTHROPIC_BASE_URL=http://127.0.0.1:8080`. ai-proxy's listener "always binds to loopback
port 8080" and sets the system proxy to it. The two cannot run together as configured.

**Evidence:** `lsof -nP -iTCP:8080 -sTCP:LISTEN`; `rtk-proxy status` → "running at
127.0.0.1:8080"; `env | grep ANTHROPIC_BASE`; `docs/reference/environment-variables.md`
(Local health endpoint section).

### F7 — The live rtk proxy loaded the current pipeline.toml [MEASURED]

The startup log names `pipeline=haiku-compaction ccr=true align=true retire=false normalize=true`
and stages `ingest_filter, cache_align, normalize, pixel_compress, ccr`. The file was last edited
Oct 18, the daemon started Oct 23, so what is on disk is what runs.

**Evidence:** `~/Library/Application Support/rtk/llm-proxy/rtk-proxy.log`, lines at
`2026-10-23T06:42:28Z`; `ls -la ~/.config/rtk/pipeline.toml`.

### F8 — The pipeline stacks a lossy Haiku digest under the CCR layer [READ]

Shell, web and MCP output above 2,500 tokens is routed to `claude-haiku-5-5` as a `digest` task
(lines 57-76); then the proxy's CCR replaces tool results above 1,000 tokens with a head+tail
preview (lines 43-46). The digest is a paraphrase. ai-proxy's own research disqualified
summarizers because they "drop the root cause" (record F2, "The summarizer is out"). The full
output survives in `rtk recall`, so it is recoverable, but only if the agent knows to ask.

**Evidence:** `~/.config/rtk/pipeline.toml:30-35,43-46,57-76`;
`docs/work/2026-10-22-on-device-compression-models.md:43,199-201`.

### F9 — keep-verbatim misses common diff shapes [MEASURED]

The rule is anchored at `^`, so any prefix defeats it. `git -C src diff`, `cd repo && git diff`,
`git log -p` fall through to the Haiku digest once over 2,500 tokens. It also over-matches
`recall-foo`, harmlessly.

**Evidence:** `grep -Eq "^(rtk )?(git (diff|show)|recall)\b"` over nine sample commands on the
M4: verbatim = `git diff`, `rtk git diff HEAD~1`, `rtk recall abc`, `git diff --stat`,
`recall-foo`; digestable = `git -C src diff`, `cd repo && git diff`, `git log -p`,
`git difftool`. POSIX ERE stands in for rtk's Rust regex; `\b` and `^` mean the same in both.

### F10 — The restart comment names a command the running daemon is not [MEASURED]

`pipeline.toml:42` says restart with `rtk llm-proxy stop && rtk llm-proxy start`. The running
daemon is the standalone binary `~/.local/bin/rtk-proxy start --bind 127.0.0.1:8080`, whose CLI
is `rtk-proxy stop|start|service`.

**Evidence:** `ps aux | grep rtk`; `rtk-proxy --help`; `~/.config/rtk/pipeline.toml:42`.

### F11 — Haiku digests are billed to an API key, with a 15 s worst case per tool call [READ]

`api_key_command` fetches a key outside the subscription login, and `timeout_secs = 15` bounds
each hook call; on timeout the original passes through.

**Evidence:** `~/.config/rtk/pipeline.toml:19-28`.

### F12 — Read results are exempt from CCR, as the comment claims [MEASURED]

In this session, Read calls returning the 77-line pipeline.toml and a 100-line markdown range
came back whole, while Bash results of similar size were compressed with `rtk_retrieve` markers.

**Evidence:** this session's tool results, 2026-10-23, Claude Code via
`ANTHROPIC_BASE_URL=http://127.0.0.1:8080`.

### F13 — The tokens_gt = 1000 CCR threshold may not be what the proxy enforces [UNVERIFIED]

Bash results in this session showed small visible portions with markers such as "compressed 385
tokens", and the log carries `tokens_compressed` values from 118 to 34,831. Whether that field is
per block or per request, and whether `RTK_PROXY_CCR_MIN_TOKENS` (present in the binary) overrides
the TOML, was not checked.

### F14 — If both proxies ran, rtk-proxy's upstream calls would not pass through ai-proxy [UNVERIFIED]

ai-proxy sets the macOS system proxy; whether rtk-proxy's HTTP client honours the system proxy
(rather than only `HTTPS_PROXY`) was not tested, because starting ai-proxy changes system state.

## Still open

- How to resolve the 8080 collision: make ai-proxy's port configurable, move rtk-proxy, or chain
  them (Claude Code → rtk-proxy → ai-proxy). The choice decides whether two compressors stack.
- Whether the Haiku digest keeps answers. ai-proxy's record measured answer retention for its
  reducers; nobody measured it for the rtk digest on build or test output.
- The CCR threshold semantics in F13: read rtk-proxy's docs or set `RTK_PROXY_CCR_MIN_TOKENS`
  explicitly and compare log counts.
- Model integration cost: ONNX Runtime package, tokenizer in C#, 0.36 s per prose call on CPU,
  261 MB artifact distribution. None of it is designed yet.
- Re-creating `~/kompress-work` evidence, or pointing the record at the `models-conversion` copy.
