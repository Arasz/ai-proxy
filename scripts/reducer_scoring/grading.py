"""Deterministic grading of a QA reply against a pre-registered answer."""
import re


def normalized(text: str) -> str:
    """Casefolded, with every run of whitespace collapsed to one space."""
    return " ".join(text.casefold().split())


def answered(reply: str, expected: str) -> bool:
    """True when the expected answer appears in the reply, not as part of a longer word or number."""
    want = normalized(expected)
    if not want:
        raise ValueError("an expected answer cannot be empty")
    before = r"(?<![0-9a-z])" if want[0].isalnum() else ""
    after = r"(?![0-9a-z])" if want[-1].isalnum() else ""
    pattern = before + re.escape(want) + after
    return re.search(pattern, normalized(reply)) is not None
