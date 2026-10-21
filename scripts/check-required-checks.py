#!/usr/bin/env python3
"""Check that every required status check names a job that exists in build.yml.

Branch protection blocks a pull request forever waiting for a check that will
never report. A required check whose job does not exist in the workflow is
exactly that: a repository where no PR can ever merge.

Usage: check-required-checks.py [repo-root]
Exit 0 = every required check has a job; exit 1 = a required check is missing.
"""

import json
import re
import subprocess
import sys
from pathlib import Path

JOB = re.compile(r"^  ([a-z0-9-]+):\s*$")


def workflow_jobs(path: Path) -> set[str]:
    """The job names in a workflow file — the keys under `jobs:`."""
    jobs: set[str] = set()
    in_jobs = False
    for line in path.read_text(encoding="utf-8").splitlines():
        if line.startswith("jobs:"):
            in_jobs = True
            continue
        if in_jobs:
            match = JOB.match(line)
            if match:
                jobs.add(match.group(1))
            elif line and not line.startswith(" "):
                in_jobs = False
    return jobs


def required_checks(repo: str) -> list[str]:
    """The status checks the repo's rulesets require."""
    raw = subprocess.run(
        ["gh", "api", f"repos/{repo}/rulesets", "--jq", ".[] | .id"],
        capture_output=True,
        text=True,
        check=True,
    ).stdout.split()

    checks: list[str] = []
    for ruleset_id in raw:
        detail = subprocess.run(
            ["gh", "api", f"repos/{repo}/rulesets/{ruleset_id}"],
            capture_output=True,
            text=True,
            check=True,
        ).stdout
        for rule in json.loads(detail).get("rules", []):
            if rule.get("type") != "required_status_checks":
                continue
            for entry in rule.get("parameters", {}).get("required_status_checks", []):
                checks.append(entry["context"])
    return checks


def main() -> int:
    root = Path(sys.argv[1] if len(sys.argv) > 1 else ".").resolve()
    repo = "Arasz/ai-proxy"

    workflows = sorted((root / ".github" / "workflows").glob("*.yml"))
    available: set[str] = set()
    for workflow in workflows:
        available |= workflow_jobs(workflow)

    required = required_checks(repo)
    if not required:
        print("no required status checks are configured on the repository", file=sys.stderr)
        return 1

    missing = [check for check in required if check not in available]
    if missing:
        for check in missing:
            print(
                f"FAIL required check '{check}' has no job in any workflow "
                f"(jobs found: {', '.join(sorted(available))})",
                file=sys.stderr,
            )
        print(f"\n{len(missing)} required check(s) would block every pull request", file=sys.stderr)
        return 1

    print(f"every required check has a job: {', '.join(sorted(required))}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
