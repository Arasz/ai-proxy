#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")"
pnpm run typecheck
pnpm exec parcel build index.html --dist-dir dist --no-source-maps --public-url ./
pnpm exec html-inline dist/index.html > bundle.html
cp bundle.html ../../docs/work/2026-10-25-proxy-architecture-guide.html
