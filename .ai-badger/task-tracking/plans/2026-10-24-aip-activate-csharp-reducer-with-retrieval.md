# aip-activate-csharp-reducer-with-retrieval — task plan

Loop: low. Owner rulings 2026-10-25: S4's 36 real-proxy edit runs and per-option token report are cut;
the C# view goes on by default, with an off switch, and live data comes from this machine.
S2 is satisfied by #71 (CCR runtime, 0.10.0).

**S1 — Carry trusted whole-file paths from matched calls to the reducer (complete, #63)**

**S2 — CCR runtime retrieval (complete, #71)**

**A — Code view in SourceCodeCompressionHandler**
- Handler takes `IEnumerable<ICodeLexer>`; a part whose trusted `FilePath` a lexer supports goes through
  `CodeReducer` with hint `[proxy view <id>: comments and imports removed. Call proxy_retrieve with this id for the exact file.]`,
  id = `CcrText.IdOf(part.Text)`; path `sourcecode.code-view`; result marked `Recovery.Retrieval`.
- No lexer, unknown path, or no saving: today's blank-line path, `Recovery.None`.
- [ ] AC: .cs part with path → view text ends with hint naming IdOf(original); no path / .txt path → blank-line path.
  check: handler unit tests; mutation: drop the path check → red.

**B — Pipeline keeps the original of every Retrieval part (exact-file retention)**
- After reduce, a `Recovery.Retrieval` part: CCR Off → original text, path `compression.original-not-kept`;
  Stateless → `Originals[id] = part.Text`; Thread → `CcrPreviewer.KeepAsync` (1 s timeout) then `Originals[id]`.
  Keep failure → PartFailure + original text (no hint ever leaves without its original kept).
- [ ] AC exact: two .cs files with same basename in one request → each id resolves byte-exact to its own original.
- [ ] AC failure: throwing store in Thread mode → forwarded body carries original bytes, no `[proxy view` hint.
- [ ] AC off: Off mode → no hint, original bytes.
  check: ContentCompressionPipelineCcrTests; mutations: skip keep, key by wrong text → red.

**C — proxy_retrieve description names view lines**
- Definition text: "Use the id from a [proxy preview ...] or [proxy view ...] line."
- [ ] AC: existing CcrRequest tool-append tests green with new bytes.

**D — Wiring, on by default**
- AiProxy references AiProxy.Code; `Proxy:Compression:CodeViews` (default true) registers `CSharpCodeLexer` as `ICodeLexer`.
- [ ] AC: real-proxy relay test — request with Read(Foo.cs) result: upstream sees the view + hint; model's
  `proxy_retrieve(id, whole)` is answered with the byte-exact original; client gets one response.
  check: CcrRelayTests; mutation: CodeViews=false → upstream sees no hint.

**E — Release**: VERSION 0.11.0, changelog entry, index row. Gates: touched suites locally once, CI.
