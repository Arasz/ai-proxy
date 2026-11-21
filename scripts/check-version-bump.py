#!/usr/bin/env python3
"""Check that a pull request which changes VERSION is a releasable bump.

A PR that leaves VERSION alone passes. A PR that changes VERSION must add a
``docs/changelog/{VERSION}-*.md`` entry, and VERSION must be greater than the
latest ``v*`` tag (a version equal to the tag was already released).

Usage: check-version-bump.py BASE_SHA HEAD_SHA [repo-root]
Exit 0 = pass; exit 1 = fail, with the reason on stderr.
"""

import re
import subprocess
import sys
from fnmatch import fnmatchcase
from pathlib import Path

SEMVER = re.compile(r"^v?(\d+)\.(\d+)\.(\d+)")


def parse(version: str) -> tuple[int, int, int] | None:
    match = SEMVER.match(version)
    return tuple(int(part) for part in match.groups()) if match else None


def latest_tag(tags: list[str]) -> str | None:
    """The highest ``v*`` tag by version number, or None when there is none."""
    versions = [(parse(tag), tag) for tag in tags if tag.startswith("v") and parse(tag)]
    return max(versions)[1] if versions else None


def check(version: str, tag: str | None, changed: list[str]) -> tuple[bool, str]:
    """Return (passed, reason) for a VERSION value, the latest tag and the changed paths."""
    if "VERSION" not in changed:
        return True, "VERSION is unchanged."
    current = parse(version)
    if current is None:
        return False, f"VERSION '{version}' is not a major.minor.patch version."
    pattern = f"docs/changelog/{version}-*.md"
    if not any(fnmatchcase(path, pattern) for path in changed):
        return False, f"VERSION changed to {version} but the PR adds no {pattern}."
    released = parse(tag) if tag else None
    if released is not None and current <= released:
        return False, f"VERSION {version} is not greater than the latest tag {tag}."
    return True, f"VERSION {version} is a new release with a changelog entry."


def git(root: Path, *args: str) -> str:
    return subprocess.run(["git", "-C", str(root), *args], capture_output=True, text=True, check=True).stdout


def main(argv: list[str]) -> int:
    if len(argv) < 3:
        print(__doc__, file=sys.stderr)
        return 1
    base, head = argv[1], argv[2]
    root = Path(argv[3]) if len(argv) > 3 else Path.cwd()
    changed = git(root, "diff", "--name-only", f"{base}...{head}").split()
    version = (root / "VERSION").read_text(encoding="utf-8").strip()
    ok, reason = check(version, latest_tag(git(root, "tag", "-l", "v*").split()), changed)
    print(reason, file=sys.stderr if not ok else sys.stdout)
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv))
