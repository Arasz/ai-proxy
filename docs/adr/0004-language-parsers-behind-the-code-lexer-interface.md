# 0004: Language parsers behind the code lexer interface

Date: 2026-10-24
Status: Accepted by the owner

## Context

The source-view reducer needs to distinguish comments, literals, identifiers and whitespace while preserving original text and positions. Maintaining complete language scanners ourselves would duplicate mature parsers and leave difficult cases such as nested interpolation and escaped identifiers in our code.

The owner chose Roslyn for C# and Tree-sitter for the remaining languages. The reducer remains inactive until exact-file retrieval and storage failure handling satisfy the [activation plan](../work/2026-10-24-csharp-reducer-activation.md).

## Decision

Use Roslyn syntax tokens and trivia for C#. Parse syntax only; no workspace, compilation or semantic model is needed. Keep parser SDK dependencies in an adapter project and retain the pure reducer and its token contract in Core. The language adapter supplies keyword recognition and supported path selection through the common interface. The C# using transformation remains specific to C#; later languages need their own import rules.

Use Tree-sitter for TypeScript/JavaScript, Python, CSS and HTML, in that order and in separate PRs. Evaluate the current TreeSitter.DotNet binding with pinned native runtime and grammar versions. Version 1.3.0 advertises native libraries for Windows, Linux and macOS, including macOS ARM64. Verify actual loading and grammar availability on supported release targets before adoption.

Parse errors or unsupported constructs cause exact-original fallback. Error recovery in the parser does not authorize a transformation. Each language gets its own fixtures, correctness tests, token measurements and edit acceptance through real retrieval. The rendered map and hint count toward savings.

## Consequences

Parser behavior comes from maintained language implementations. We still own classification into the common token model, safe transformations, and the emitted view. Native dependencies add packaging work for Tree-sitter; Roslyn adds managed package and startup costs. Measure the complete adapter on representative files before drawing runtime performance conclusions.

CSLY remains useful for small authored DSLs. It would require us to define and maintain the language grammars for this reducer, so it does not reduce this work.

The simpler shape is one lexer interface and one adapter per language, with no generic compiler framework or semantic rename engine.

## Sources

- [Roslyn syntax analysis](https://learn.microsoft.com/en-us/dotnet/csharp/roslyn-sdk/get-started/syntax-analysis)
- [Tree-sitter design and parser runtime](https://github.com/tree-sitter/tree-sitter)
- [TreeSitter.DotNet 1.3.0 package](https://www.nuget.org/packages/TreeSitter.DotNet/1.3.0)
- [CSLY scope and grammar definitions](https://github.com/b3b00/CSLY)
