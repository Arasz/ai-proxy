# Platforms

The tool package carries one payload per runtime identifier. The shell picks the payload for the
machine it runs on.

| Runtime identifier | Proxy, gain, retrieval | Code views (Tree-sitter) |
|---|---|---|
| `win-x64`, `win-arm64` | Supported | Supported |
| `osx-arm64` | Supported | Supported |
| `linux-x64`, `linux-arm64` | Supported | Supported |
| `linux-musl-x64` (Alpine) | Supported | Best effort |

## Code views on musl

TreeSitter.DotNet ships no musl native, so the `linux-musl-x64` payload carries the glibc
`libtree-sitter.so`. It loads on Alpine today, but nothing guarantees it will: `ldd` reports
glibc-only symbols as missing, and some other loaded library happens to provide them.

When a grammar fails to load, the proxy logs one warning per grammar and sends the original text
unchanged. You lose the compression of code views, not correctness.

The `musl-code-view` CI job runs the code-view tests in an Alpine image, so a change in this
behaviour shows up there first.
