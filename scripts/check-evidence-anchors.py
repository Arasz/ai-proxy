#!/usr/bin/env python3
"""Check that every ``evidence=path:line`` anchor in the docs points at real code.

An anchor is the marker ``evidence=<repo-relative path>:<line>`` inside a
``trust:trustchecked`` comment. The file must exist and the line must be inside it.
A dead anchor tells a reader a claim was checked against code that has since moved.

Scans README.md and every ``.md`` file under docs/, except docs/work/ (dated
research records that mention anchors as prose).

Usage: check-evidence-anchors.py [repo-root]
Exit 0 = every anchor resolves; exit 1 = at least one does not, each named on stderr.
"""

import re
import sys
from pathlib import Path

ANCHOR = re.compile(r"evidence=([^\s:>]+):(\d+)")


def documents(root: Path) -> list[Path]:
    found = [root / "README.md"] if (root / "README.md").is_file() else []
    work = root / "docs" / "work"
    found.extend(p for p in sorted((root / "docs").rglob("*.md")) if work not in p.parents)
    return found


def line_count(path: Path) -> int:
    return len(path.read_text(encoding="utf-8", errors="replace").splitlines())


def main() -> int:
    root = Path(sys.argv[1] if len(sys.argv) > 1 else ".").resolve()
    failures: list[str] = []
    checked = 0

    for document in documents(root):
        text = document.read_text(encoding="utf-8")
        for number, line in enumerate(text.splitlines(), start=1):
            for target, wanted in ANCHOR.findall(line):
                checked += 1
                where = f"{document.relative_to(root)}:{number}"
                source = root / target
                if not source.is_file():
                    failures.append(f"{where} anchors {target}:{wanted}, but the file does not exist")
                elif not 1 <= int(wanted) <= line_count(source):
                    failures.append(
                        f"{where} anchors {target}:{wanted}, but the file has {line_count(source)} lines"
                    )

    if failures:
        for failure in failures:
            print(f"FAIL {failure}", file=sys.stderr)
        print(f"\n{len(failures)} dead evidence anchor(s) of {checked}", file=sys.stderr)
        return 1

    print(f"evidence anchors: all {checked} resolve")
    return 0


if __name__ == "__main__":
    sys.exit(main())
