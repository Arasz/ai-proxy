# Design: an in-process code reducer with per-language lexers

**Date:** 2026-10-24
**Status:** approved by owner on 2026-10-24. The [inactive C# core](2026-10-24-csharp-reducer-build.md) builds first; [runtime activation](2026-10-24-csharp-reducer-activation.md) follows CCR retrieval.
**Goal:** take the code score from the shipped rules' 0.30 toward 0.6 without the agent losing the
ability to edit what it read.

The reducer scoring record ([2026-10-24-reducer-scoring-results.md](2026-10-24-reducer-scoring-results.md))
scored the shipped rules at 0.30 on code: they keep every answer but remove only about 18%, because
`SourceCodeCompressionHandler` drops blank lines and nothing else. rtk's preview reached 0.61 on
code in the relaxed view, but by cutting the file's head and tail and losing half the answers on
the large file. This design gets the same order of saving by removing what carries no facts, and
keeps the original one call away.

## What was measured before this design

Three small experiments ran on 2026-10-24. Their scripts are in
[scripts/code_reducer_spike/](../../scripts/code_reducer_spike/), and the same tests run again
once the lexers exist (see "Acceptance").

**Agents edit from what they read.** Haiku and Sonnet each got three edit tasks on
`DialectDetector.cs` (the scoring harness's `code_file` payload) through three tools: `read_file`
returning a compacted view (imports, comments, blank lines and indentation removed),
`retrieve_original`, and `edit_file` with exact matching against the file on disk. Each task ran
three times with and without a hint at the end of the compacted view, 36 runs in all
(`edit_results_2026-10-24.jsonl`).

| | retrieved before editing | first edit applied | edits that failed | final file right |
|---|---|---|---|---|
| no hint | 0 of 18 | 12 of 18 | 6 | 16 of 18 |
| with hint | 13 of 18 | 18 of 18 | 0 | 18 of 18 |

Without the hint, no agent fetched the original first. Single-line edits still worked, because a
line's own text survives compaction. Multi-line edits failed and were then retried after a fetch,
except in two Sonnet runs, where the edit applied with its new line at column 0. That compiles in
C#. In Python it would change the program, and nothing would report it.

**Indentation costs few tokens.** Haiku's own input token counts (`tokens.py`, through `claude -p`)
for the same file:

| version | characters removed | tokens removed |
|---|---|---|
| comments, imports and blank lines removed, indentation kept | 28.8% | 27.4% |
| and indentation stripped | 49.4% | 33.7% |
| with indentation stripped and line numbers added | 41.9% | 22.1% |

Stripping indentation removes 20 more points of characters but only 6 of tokens. Comments and
imports are worth about their size in tokens. The scoring harness counts characters, so it
over-rates any reducer that mostly removes whitespace.

**A published replication agrees.** Hrubec and Cito ("Reducing Token Usage of State-in-Context
Agents using Minification", ICPC 2026, arXiv 2606.01326) minified Python for a SWE-bench Verified
repair agent. All cuts together saved 42% of input tokens and lost 12 points of solved tasks. Per
cut, docstrings saved 22% and comments 11% at little cost. Reducing indentation saved 3% and cost
the most, through broken patches. Shortened identifiers saved 1 to 2%. Their agent could not fetch
the original file; ours can, so their 12-point loss is an upper bound we expect not to reach. That
expectation is not measured.

## Decisions

The owner made these in the 2026-10-24 design conversation.

1. **Every compacted code view ends with a retrieval hint**, telling the agent to fetch the original
   before editing. The spike showed it is what makes every edit land.
2. **A fetch is scored as the full file.** A file the agent reads and then edits costs compact plus
   full.
3. **Indentation is kept in every language.** No per-language split between indentation-sensitive
   languages and the rest.
4. **Line numbers are the original's.** Every surviving line keeps the number it had in the
   original file, so a gap shows where lines went and "line 21" still means line 21 on disk. When a
   later step joins lines (collapsing a method body, say), the surviving line carries the range it
   replaced, written like `60-104`.
5. **Unnumbered output stays unnumbered.** Claude Code's Read numbers its lines, and `cat -n` does;
   those keep original numbers. Plain `cat` output gets no numbers added. `sed -n` and other partial
   reads are out of scope.
6. **The reducer runs in process, with language adapters behind our lexer interface.** No external minifier processes (esbuild,
   python-minifier). Existing minifiers all print one line, which breaks decisions 3 and 4; what we
   need from them is comment- and string-aware scanning. Roslyn supplies C# syntax; Tree-sitter
   supplies the later language frontends (owner approved on 2026-10-24).
7. **One interface for every language**, with options per step.
8. **An option removes comments**, doc comments and docstrings included. Imports and usings go too
   (the owner accepts losing them).
9. **An option renames variables, methods and parameters to short names**, but only names used more
   than once in the file. Each renamed name goes in a map, `short_name:original`, sent with the
   compacted view so the agent can read the code with its real names in mind.
10. **The tests repeat once the lexers are ready**: the edit spike, the token comparison and the
    scoring harness, this time on the real reducer (see "Acceptance").

## The pipeline

```
original ──▶ store original (retrievable) ──▶ lex ──▶ lossless steps ──▶ lossy steps ──▶ render
```

1. **Store the original** through the existing `IContentStore`, so it can be served back.
2. **Lex** the file with the lexer for its language into tokens that know what is a comment, a
   string, an identifier, a keyword, whitespace and a newline.
3. **Lossless steps** (the code means the same): drop blank lines and trailing whitespace. Strings
   and verbatim blocks are never touched; the current handler's `LiteralBlocks` rule already
   protects them.
4. **Lossy steps**, each behind its own option: remove comments and doc comments, remove import and
   using lines, rename identifiers (below).
5. **Render** the surviving lines with their original line numbers where the input had them, then the
   rename map if there is one, then the hint.

Indentation passes through every step unchanged.

## The common interface

The shape below is a sketch for the builder, not a fixed API. It should fit behind the existing
`ICompressionHandler`, so the router and pipeline do not change.

- A **lexer per language** turns text into a list of tokens, each with a kind and its line
  number. The kinds are Comment, DocComment, String, Identifier, Keyword, Whitespace, NewLine and
  Other. A lexer never fails on code it does not understand; it gives up by returning the text as
  Other, and the reducer then leaves that file unchanged.
- **Steps** take tokens and return tokens, and know nothing about the language beyond token kinds
  and a small per-language table (the import keywords and the language's keyword list for
  renaming).
- **Options** switch each lossy step on or off: `RemoveComments`, `RemoveImports`,
  `RenameIdentifiers`.
- **Languages, in order** (my call on the order): C#, then TypeScript and JavaScript (one lexer), Python, CSS, HTML.
  C# uses Roslyn. TS/JS, Python, CSS and HTML use Tree-sitter, through the same interface.
  Each language still needs its own safe token classification, transformations and acceptance fixtures.
  See [ADR 0004](../adr/0004-language-parsers-behind-the-code-lexer-interface.md).

## Renaming and the map

Renaming is lexical. Every token of kind Identifier whose text equals the name is replaced, across
the whole file. No scope analysis is needed, because the map makes the change reversible.

- A name is renamed only when it occurs more than once in the file. A single occurrence costs a map
  entry and saves nothing.
- Short names are chosen so they never collide with a keyword or with a name already in the file.
- Names inside strings and comments are not renamed. Comments are usually removed by then anyway.
- The map is rendered once, after the code, as lines of `short_name:original`.
- Whether a rename pays is a token question, not a character one. The paper measured only 1 to 2%
  for renaming. My call, not the owner's: this option ships off by default until the token test
  below shows it pays on our payloads.

## Scoring changes this needs

These belong to the scoring harness, not the reducer, and are a precondition for judging it.

- **Score tokens, not characters.** The harness's compression counts characters. Count Claude input
  tokens instead (the `tokens.py` method), or at least report both.
- **Count retrieval.** Per decision 2, an edited file costs compact plus full. This is the plan's
  `ccr-retrieval-scoring` step; the code reducer is the first candidate that needs it.
- **Score understanding with the map.** The QA questions are asked of the compacted view with the
  map included. A renamed answer counts as kept only if the reader gives the original name.

## Goals, non-goals, anti-goals

- **Goal:** a code score near 0.6 on the size-normalized, token-based score, with every answer kept
  under the headline rule.
- **Non-goal:** minified output that a compiler accepts as-is. The view is for an agent to read.
- **Non-goal:** partial reads (`sed -n`, `head`), and numbering output that had no numbers.
- **Anti-goal:** an edit that applies and silently changes the program. If any repeated edit run ends
  with a wrong file that the agent believed it fixed, the design has failed, whatever the score.
- **Anti-goal:** hiding the original. The hint and the retrieval path ship together with the
  reducer, or the reducer does not ship.

## Settled after the builder check

A reviewer read this document cold and asked eight questions. These are the answers. Where an
answer is my call rather than the owner's, it says so.

- **The reducer does not ship before the retrieval tool.** The `ccr-design` step's tool is a hard
  predecessor: its name and contract set the hint wording, and acceptance 1 runs through it in the
  real proxy, not only through the spike's `fs_server.py`. (Follows from the anti-goal above.)
- **Language comes from the file path, never from sniffing** (my call). The path of the tool call
  that produced the result travels with the part, so `ContentPart` or the decompositor gains it.
  With no path, or an extension no lexer claims, only the lossless steps run. Sniffing cannot tell
  C# from TypeScript or CSS from HTML reliably, and a wrong lexer can strip something that is not a
  comment.
- **Numbered input means every line carries a number prefix** (my call): Claude Code Read's
  `N\t` and `cat -n`'s right-aligned `N\t`. If any line lacks the prefix, the text is treated as
  unnumbered and the reducer adds none.
- **Retrieval cost is a scoring rule, not a runtime one** (my call). The live proxy does not count or
  limit fetches.
- **C# ships first, in its own pull request; each further language is its own** (my call). The
  acceptance checks run per language as it lands. The scoring payloads hold only C# code today
  (`code_file`, `code_file_large`), so each new language adds at least two payloads of its own, with
  questions, before it is scored.
- **The map and the hint count toward the compacted size** (my call). If the view, map and hint
  together are not smaller than the original, the original goes through unchanged.
- **A lexer bails on any ambiguity** (my call). An unterminated string or comment, or a construct
  the lexer does not model (raw and interpolated strings it cannot close, regex and template
  literals, f-strings), leaves the file to the lossless steps. Skipping a file costs savings;
  mangling one breaks the anti-goal.
- **The new reducer replaces the blank-line handler**, whose rule becomes its first lossless step
  (my call). The options live in `CompressionOptions`, globally: `RemoveComments` on,
  `RemoveImports` on, `RenameIdentifiers` off.

## What the builder decides

- How the file path reaches the reducer: a new field on `ContentPart`, or the decompositor routing
  by path.
- The exact hint wording, as long as it names the retrieval tool and says to use it before editing.
- The language adapter implementation and grammar versions, within the Roslyn/Tree-sitter decision.
- The short-name alphabet and the order names are assigned in.

## Acceptance

The reducer is done when all of these pass, each run against the real reducer instead of the
spike's regex:

1. **Edit spike, repeated:** `scripts/code_reducer_spike/run.py` with `fs_server.py` calling the real
   reducer, Haiku and Sonnet, three tasks, hint on, three runs each. Every run's final file is
   right, and no edit applies wrongly. Repeat with renaming on.
2. **Token comparison, repeated:** `tokens.py` per language on the scoring payloads, reporting
   tokens removed per option.
3. **Scoring harness:** the code kind scored on tokens, with retrieval counted, against the shipped
   rules' 0.30. Every QA answer is kept in the headline view, with the map present when renaming is
   on.
4. **Lexer tests:** per language, comments and strings that look like each other (`"// not a
   comment"`, `/* "not a string" */`) are classified right, and a file the lexer cannot read comes
   back unchanged.
