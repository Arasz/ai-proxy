#!/usr/bin/env python3
"""Check that the docs tree is truthful in both directions.

Every ``.md`` file under ``docs/`` must be indexed by its own directory's
``README.md``, and no ``README.md`` may name a file that does not exist on disk.
A README that lists a file nobody wrote is a lie a reader will trust, and a
document nobody indexed is a document nobody will find.

Usage: check-docs-index.py [docs-root]
Exit 0 = every check passed; exit 1 = at least one failed, each named on stderr.
"""

import re
import sys
from pathlib import Path

# A markdown link to a sibling document: ](path/to/file.md)
LINK = re.compile(r"\]\(([^)#:]+\.md)\)")


def readme_of(directory: Path) -> Path:
    return directory / "README.md"


def links_in(text: str) -> list[str]:
    """The link targets in a README, de-duplicated, ignoring http(s) and anchors."""
    seen: list[str] = []
    for target in LINK.findall(text):
        if target.startswith(("http://", "https://", "mailto:")):
            continue
        if target not in seen:
            seen.append(target)
    return seen


def main() -> int:
    root = Path(sys.argv[1] if len(sys.argv) > 1 else "docs").resolve()
    if not root.is_dir():
        print(f"no docs directory at {root}", file=sys.stderr)
        return 1

    failures: list[str] = []

    # 1. Every directory has a README.
    for directory in sorted(p for p in root.rglob("*") if p.is_dir()):
        readme = readme_of(directory)
        if not readme.is_file():
            failures.append(f"{directory.relative_to(root)}/ has no README.md")

    # 2. Every README link target exists, relative to the README's own directory.
    for readme in sorted(root.rglob("README.md")):
        directory = readme.parent
        for target in links_in(readme.read_text(encoding="utf-8")):
            if not (directory / target).is_file():
                failures.append(
                    f"{readme.relative_to(root)} links to {target}, which does not exist"
                )

    # 3. Every .md file under docs/ is indexed by its directory's README.
    indexed: set[Path] = set()
    for readme in root.rglob("README.md"):
        directory = readme.parent
        for target in links_in(readme.read_text(encoding="utf-8")):
            candidate = (directory / target).resolve()
            if candidate.is_file():
                indexed.add(candidate)

    for document in sorted(root.rglob("*.md")):
        if document.name == "README.md":
            continue
        if document.resolve() not in indexed:
            failures.append(f"{document.relative_to(root)} is not indexed by any README")

    if failures:
        for failure in failures:
            print(f"FAIL {failure}", file=sys.stderr)
        print(f"\n{len(failures)} docs-index failure(s)", file=sys.stderr)
        return 1

    print("docs index: every document is indexed and every link resolves")
    return 0


if __name__ == "__main__":
    sys.exit(main())
