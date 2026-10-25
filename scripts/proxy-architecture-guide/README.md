# Proxy architecture guide

Interactive research snapshot for 2026-10-25. Runtime source is pinned to
`c2cce0cf754ef84e6318305fedd8c087881ff65c`. Twelve exported plans include
local drafts and locally updated exports; those carry embedded
source text and SHA-256 rather than a link claiming they existed at that commit.

Open [the standalone guide](../../docs/work/2026-10-25-proxy-architecture-guide.html).
It contains Anthropic, OpenAI and Default tabs, two interactive Archify charts,
all plan steps and criteria, 34 research-record links and 32 sourced terms.
The guide distinguishes wired behavior, staged code and proposals. It does not
start the proxy or make provider requests.

## Build

With Node 20+ and pnpm (lockfile produced by pnpm 12):

```sh
cd scripts/proxy-architecture-guide
pnpm install --frozen-lockfile
pnpm run bundle
```

`build.sh` type-checks the React 18/TypeScript source, bundles with Parcel and
inlines JavaScript/CSS into the dated HTML. React, Tailwind and Vite setup began
with web-artifacts-builder; unused scaffold dependencies were removed.
`src/data.json` holds the frozen content and exact validated Archify HTML.
The JSON specifications in `diagrams/` are the editable chart inputs. To revise
a chart, validate and deliver it with Archify, then replace the matching HTML
string in `data.json` and rebuild. The frontend build does not re-render charts.

## Validation

Provider and diagram tabs, keyboard selection, plan expansion/search, glossary
filter/search and empty state, and theme switching were exercised in Chromium.
The standalone HTML was also loaded with networking disabled. Widths 320, 390,
1440, 1600, 1920 and 2048 were checked for horizontal overflow. Diagram viewers
were checked at four desktop sizes in light and dark; each passed nine Archify
artifact checks with zero composition errors or warnings. Screenshots were
inspected, and independent review corrected failure-branch continuation and
accessible diagram labels. A reader test corrected provider-host and plan-source
ambiguities. Runtime behavior was checked against source, not a running proxy.

The project pre-push build/test/docs/workflow gates also apply. This is a dated
documentation artifact, not an application release; VERSION is unchanged.
