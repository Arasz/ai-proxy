# ai-proxy docs

Machine-wide HTTP(S) forward proxy that inspects canonical provider traffic and passes every other destination through.

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
| `changelog/` | One file per release; the root `VERSION` is the version of truth |
| `assets/` | Images / diagrams |

## Conventions

- Kebab-case filenames, `.md`, no capitals except `README.md`.
- Grammar: `tutorials/` and `how-to/` start with imperative verb; `reference/` bare noun; `explanation/` noun phrase or `why-`; `work/` is `YYYY-MM-DD-slug`; `adr/` frozen (`NNNN-slug.md`); `changelog/` `{version}-{slug}.md`.
- A directory's README is a complete map: every file, one line of purpose.
- Depth cap: three path segments below `docs/`.
- `adr/` never moved, renamed, or reformatted.
- The root `VERSION` file is the only copy of the version. `changelog/` holds the per-release
  entries and has no `VERSION` of its own.
- Four quadrants of the sibling project are not here yet, because nothing belongs in them:
  `plans/`, `research/`, `reviews/` and `qa-sessions/`. A quadrant joins when it has a file.
