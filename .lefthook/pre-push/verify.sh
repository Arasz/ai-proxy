#!/usr/bin/env bash
# ai-proxy local quality gate. One subcommand per lane; `pre-push` selects lanes
# from the changed paths, `all` runs every lane, `doctor` checks the environment
# and hooks.
# Contract: exit 0 = pass, 124 = the whole-gate deadline killed the lane in
# flight, any other non-zero = a lane failed. Nothing outside those three means
# anything.
# Targets bash 3.2 (macOS system bash): no associative arrays, no ${var,,}.
set -uo pipefail

_self_abs="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/$(basename "${BASH_SOURCE[0]}")"
cd "$(git rev-parse --show-toplevel)" || exit 1
readonly SELF="${_self_abs#"$PWD"/}"

# git exports these to a hook and they point a child's git at THIS repo, so a
# test building a throwaway repo would write to the real one. Dropped after the
# cd, so a lane sees the same environment as when run by hand.
unset GIT_DIR GIT_WORK_TREE GIT_COMMON_DIR GIT_INDEX_FILE GIT_OBJECT_DIRECTORY \
      GIT_ALTERNATE_OBJECT_DIRECTORIES GIT_PREFIX GIT_NAMESPACE GIT_CEILING_DIRECTORIES \
      GIT_QUARANTINE_PATH GIT_REFLOG_ACTION GIT_AUTHOR_DATE GIT_COMMITTER_DATE GIT_EDITOR

readonly DEADLINE_SECS="${AI_PROXY_GATE_DEADLINE:-900}"
readonly LOG_DIR="${AI_PROXY_GATE_LOG_DIR:-${TMPDIR:-/tmp}/ai-proxy-gate}"

log() { printf '%s\n' "$*" >&2; }

run_lane() {
  # run_lane <name> <command...>
  local name="$1"; shift
  mkdir -p "$LOG_DIR"
  local logfile="$LOG_DIR/$name.log"
  log "==> $name"
  if "$@" >"$logfile" 2>&1; then
    log "    PASS $name"
    return 0
  else
    # The status has to be captured inside the else branch: an `if` with no
    # else that takes the false path exits 0, so reading $? after the `fi`
    # would report the failed lane as a pass and the push would go through.
    local code=$?
    log "    FAIL $name (exit $code) — see $logfile"
    tail -n 40 "$logfile" >&2 || true
    return "$code"
  fi
}

lane_build() {
  dotnet build --configuration Release --nologo
}

lane_test() {
  dotnet test --configuration Release --nologo
}

lane_docs_index() {
  # Every .md file under docs/ must be indexed by its directory's README, and
  # no README may name a file that does not exist. A README that lists a file
  # that was never written is a lie a reader will trust.
  python3 scripts/check-docs-index.py
  # Every evidence=path:line anchor must name an existing line.
  python3 scripts/check-evidence-anchors.py
}

lane_workflows() {
  # Every third-party action must be pinned to a full commit SHA: a tag is
  # remote code re-fetched on every run, not a fixed dependency.
  local bad
  bad="$(grep -rhoE 'uses: [^@[:space:]]+@[^[:space:]]+' .github/workflows/ \
    | grep -vE '@[0-9a-f]{40}$' || true)"
  if [ -n "$bad" ]; then
    echo "unpinned action reference(s):" >&2
    echo "$bad" >&2
    return 1
  fi
  echo "every action reference is pinned to a full SHA"
}

lane_required_checks() {
  # A required status check whose job does not exist in any workflow blocks
  # every pull request forever, waiting for a check that will never report.
  python3 scripts/check-required-checks.py .
}

changed_files() {
  # The files this push would carry. With an upstream to diff against, that is
  # the commit range; without one (a first push, or a branch with no upstream)
  # there is no range, so every tracked file counts — a first push carries the
  # whole repository, and treating it as "nothing changed" would let an
  # unbuilt repository reach the remote.
  local base
  base="$(git rev-parse --verify --quiet '@{upstream}' 2>/dev/null || true)"
  if [ -n "$base" ]; then
    git diff --name-only "$base" HEAD 2>/dev/null || true
  else
    git ls-files 2>/dev/null || true
  fi
}

select_lanes() {
  # The lanes a push actually needs, from the paths it carries.
  local files="$1"
  local want_build=0 want_test=0 want_docs=0 want_workflows=0

  case "$files" in
    *"src/"*|*"tests/"*|*.csproj*|*.slnx*|*Directory.Build.props*|*Directory.Packages.props*|*global.json*|*nuget.config*|*VERSION*)
      want_build=1; want_test=1 ;;
  esac
  case "$files" in
    *docs/*|README.md) want_docs=1 ;;
  esac
  case "$files" in
    *.github/workflows/*) want_workflows=1; want_checks=1 ;;
  esac

  [ "$want_build" = 1 ] && echo "build"
  [ "$want_test" = 1 ] && echo "test"
  [ "$want_docs" = 1 ] && echo "docs-index"
  [ "$want_workflows" = 1 ] && echo "workflows"
  [ "$want_checks" = 1 ] && echo "required-checks"
  return 0
}

doctor() {
  local ok=0
  command -v dotnet >/dev/null 2>&1 && log "ok   dotnet" || { log "MISS dotnet"; ok=1; }
  command -v python3 >/dev/null 2>&1 && log "ok   python3" || { log "MISS python3"; ok=1; }
  command -v lefthook >/dev/null 2>&1 && log "ok   lefthook" || { log "MISS lefthook"; ok=1; }
  if [ -x "$(git rev-parse --git-path hooks)/pre-push" ]; then
    log "ok   pre-push hook installed"
  else
    log "MISS pre-push hook — run 'lefthook install'"
    ok=1
  fi
  return "$ok"
}

main() {
  local sub="${1:-pre-push}"
  case "$sub" in
    doctor)
      doctor
      return $?
      ;;
    all)
      run_lane build lane_build || return $?
      run_lane test lane_test || return $?
      run_lane docs-index lane_docs_index || return $?
      run_lane workflows lane_workflows || return $?
      run_lane required-checks lane_required_checks || return $?
      return 0
      ;;
    pre-push) ;;
    *)
      echo "usage: $0 {pre-push|all|doctor}" >&2
      return 2
      ;;
  esac

  if ! command -v dotnet >/dev/null 2>&1; then
    log "dotnet is not on PATH; skipping the gate (run '$SELF doctor' for the environment check)"
    return 0
  fi

  local files lanes lane
  local want_build=0 want_test=0 want_docs=0 want_workflows=0 want_checks=0
  files="$(changed_files)"
  lanes="$(select_lanes "$files")"
  if [ -z "$lanes" ]; then
    log "No lane-relevant paths in this push; the gate passes."
    return 0
  fi
  log "Lanes for this push: $(echo "$lanes" | tr '\n' ' ')"

  for lane in $lanes; do
    case "$lane" in
      build) run_lane build lane_build || return $? ;;
      test) run_lane test lane_test || return $? ;;
      docs-index) run_lane docs-index lane_docs_index || return $? ;;
      workflows) run_lane workflows lane_workflows || return $? ;;
      required-checks) run_lane required-checks lane_required_checks || return $? ;;
    esac
  done
  return 0
}

main "$@"
