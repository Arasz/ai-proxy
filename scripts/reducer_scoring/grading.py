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


FACT_FORMS = (
    (r"\berror\(s\)", "errors"),
    (r"\bwarning\(s\)", "warnings"),
    (r"\bpassed\b", "succeeded"),
    (r"(\d+(?:\.\d+)?)\s*(?:milliseconds|millisecond|ms)\b", r"\1ms"),
    (r"(\d+(?:\.\d+)?)\s*(?:seconds|second|secs|sec|s)\b", r"\1s"),
    (r"\b(failed|succeeded|skipped|errors|warnings|total)\s*:\s*(\d+)\b", r"\2 \1"),
    (r"(?<!\d )\b(failed|succeeded|skipped|errors|warnings|total)\s+(\d+)\b", r"\2 \1"),
    (r"\(\s*(\d+)\s*,\s*(\d+)\s*\)", r"(\1,\2)"),
)


def fact_form(text: str) -> str:
    """One spelling per fact: units written short, counts written number first, positions without spaces."""
    text = normalized(text)
    for pattern, replacement in FACT_FORMS:
        text = re.sub(pattern, replacement, text)
    return text


def same_fact(reply: str, expected: str) -> bool:
    """Like answered, but a unit, count or position written another way still counts."""
    return answered(fact_form(reply), fact_form(expected))
