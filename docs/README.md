# ai-proxy docs

Local proxy that detects the LLM provider dialect of an inbound request and forwards it to that provider's upstream, with pass-through for other local traffic.

## Map

| Directory | Purpose |
|---|---|
| `adr/` | Frozen architecture decisions |
| `work/` | Dated work records |
| `reference/` | Lookups: contracts, env vars |
| `explanation/` | Why the architecture is shaped so |
| `how-to/` | Recipes starting with imperative verb |
| `tutorials/` | Walkthroughs starting with imperative verb |
| `meta/` | Machine state / ledger |
| `notes/` | Standing notes not yet folded |
| `features/` | Feature dossiers |
| `changelog/` | One file per release; VERSION is truth |
| `assets/` | Images / diagrams |

## Conventions

- Kebab-case filenames, `.md`, no capitals except `README.md`.
- Grammar: `tutorials/` and `how-to/` start with imperative verb; `reference/` bare noun; `explanation/` noun phrase or `why-`; `work/` is `YYYY-MM-DD-slug`; `adr/` frozen (`NNNN-slug.md`); `changelog/` `{version}-{slug}.md`.
- A directory's README is a complete map: every file, one line of purpose.
- Depth cap: three path segments below `docs/`.
- `adr/` never moved, renamed, or reformatted.
