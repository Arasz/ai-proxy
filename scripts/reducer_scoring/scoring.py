"""Turns raw runner output into one Measurement per candidate per payload, and per kind."""
import statistics
import sys
from dataclasses import dataclass
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from compression_score import Measurement  # noqa: E402
from grading import answered  # noqa: E402
from tokens import estimate  # noqa: E402

MISSES_THAT_LOSE = 2


@dataclass(frozen=True)
class ReducerRun:
    """One run of an extractive candidate: the text it produced, how long that took, and what it added outside it, in estimated tokens."""

    output: str
    latency_s: float
    added_tokens: int = 0
    judged: frozenset[str] = frozenset()


@dataclass(frozen=True)
class QaRun:
    """One run of an abstractive candidate: its output, its latency, and the reader's replies per question.

    added_tokens is what the candidate sent outside its output; fetched_tokens is the original the reader
    retrieved, which is not a saving.
    """

    output: str
    latency_s: float
    replies: list[list[str]]
    judged: frozenset[str] = frozenset()
    added_tokens: int = 0
    fetched_tokens: int = 0


@dataclass(frozen=True)
class Row:
    """The measurement for one candidate on one payload or kind; n/a when the candidate left it untouched."""

    measurement: Measurement | None
    not_applicable: bool = False


def qa_kept(replies: list[list[str]], questions: list[dict], match=answered, judged: frozenset[str] = frozenset()) -> int:
    """Questions still answered: a question is lost only when at least two of its replies miss and the judge
    did not accept its answer."""
    if len(replies) != len(questions):
        raise ValueError(f"{len(replies)} reply sets for {len(questions)} questions")
    kept = 0
    for question, answers in zip(questions, replies):
        misses = sum(not match(reply, question["a"]) for reply in answers)
        if misses < MISSES_THAT_LOSE or question["a"] in judged:
            kept += 1
    return kept


def _median_row(original: str, total: int, lengths: list[int], latencies: list[float], kept: list[int]) -> Row:
    return Row(Measurement(
        original_tokens=estimate(original),
        reduced_tokens=statistics.median_low(lengths),
        latency_s=statistics.median(latencies),
        answers_kept=statistics.median_low(kept),
        answers_total=total,
    ))


def extractive_row(original: str, questions: list[dict], runs: list[ReducerRun], count_untouched: bool = False,
                   match=answered) -> Row:
    """Verbatim retention: an answer is kept when it still appears in the candidate's output.

    Untouched output is n/a, as pre-registered, unless count_untouched measures it as nothing removed.
    """
    if not count_untouched and all(run.output == original for run in runs):
        return Row(None, not_applicable=True)
    kept = [sum(match(run.output, q["a"]) or q["a"] in run.judged for q in questions) for run in runs]
    lengths = [estimate(r.output) + r.added_tokens for r in runs]
    return _median_row(original, len(questions), lengths, [r.latency_s for r in runs], kept)


def qa_row(original: str, questions: list[dict], runs: list[QaRun], match=answered) -> Row:
    """QA retention: an answer is kept when the reader, given the candidate's output, still finds it."""
    kept = [qa_kept(run.replies, questions, match, run.judged) for run in runs]
    lengths = [estimate(r.output) + r.added_tokens + r.fetched_tokens for r in runs]
    return _median_row(original, len(questions), lengths, [r.latency_s for r in runs], kept)


def kind_row(rows: list[Row]) -> Row:
    """Pools the payloads of one kind; untouched payloads are left out, and one lost answer fails the kind."""
    measured = [row.measurement for row in rows if not row.not_applicable]
    if not measured:
        return Row(None, not_applicable=True)
    if len(measured) == 1:
        return Row(measured[0])
    return Row(Measurement(
        original_tokens=sum(m.original_tokens for m in measured),
        reduced_tokens=sum(m.reduced_tokens for m in measured),
        latency_s=statistics.median(m.latency_s for m in measured),
        answers_kept=sum(m.answers_kept for m in measured),
        answers_total=sum(m.answers_total for m in measured),
    ))


def _passes(row: Row) -> bool:
    m = row.measurement
    return m is not None and m.answers_kept == m.answers_total


def void_reasons(identity: dict[str, Row], summarizer: dict[str, Row]) -> list[str]:
    """Why the whole run is void under the pre-registered rules; empty when it stands."""
    reasons = []
    failed = sorted(name for name, row in identity.items() if not _passes(row))
    if failed:
        reasons.append(f"identity lost answers on {', '.join(failed)}: those questions are ill-posed")
    passing = sum(_passes(row) for row in summarizer.values())
    if passing * 2 > len(summarizer):
        reasons.append(f"summarizer_control passed the retention gate on {passing} of {len(summarizer)} payloads")
    return reasons
