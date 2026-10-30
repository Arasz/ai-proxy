#!/usr/bin/env bash
# Every third-party action must be pinned to a full commit SHA: a tag is remote
# code re-fetched on every run, not a fixed dependency. Used by the pre-push
# workflows lane and the build-bdd CI job, so the rule lives in one place.
set -euo pipefail
bad="$(grep -rhoE 'uses: [^@[:space:]]+@[^[:space:]]+' .github/workflows/ | grep -vE '@[0-9a-f]{40}$' || true)"
if [ -n "$bad" ]; then
  echo "unpinned action reference(s):" >&2
  echo "$bad" >&2
  exit 1
fi
echo "every action reference is pinned to a full SHA"
