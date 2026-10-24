"""One score per reducer candidate: answer retention gates, compression and latency rank.

Used by the scoring harness (task-graph step `scoring-harness`) to rank the shipped rules, the
extractive model, the Haiku digest and CCR head+tail on the same payloads.
"""
from dataclasses import dataclass


@dataclass(frozen=True)
class Measurement:
    """What one candidate did to one payload, medians over three runs."""

    original_tokens: int
    reduced_tokens: int
    latency_s: float
    answers_kept: int
    answers_total: int


def compression(m: Measurement) -> float:
    """Share of the payload removed: 0.0 keeps everything, 1.0 removes everything."""
    if m.original_tokens == 0:
        return 0.0
    return max(0.0, 1.0 - m.reduced_tokens / m.original_tokens)


def retention(m: Measurement) -> float:
    """Share of the pre-registered answers the downstream QA model still got right."""
    if m.answers_total == 0:
        raise ValueError("a payload without answers cannot be scored")
    return m.answers_kept / m.answers_total


def latency_score(latency_s: float, budget_s: float) -> float:
    """Latency mapped onto (0, 1]: a free call is 1.0, a call at the budget 0.5, never zero."""
    return budget_s / (budget_s + latency_s)


def score(m: Measurement, budget_s: float, retention_floor: float = 1.0) -> float:
    """Harmonic mean of compression and latency; zero when retention is under the floor."""
    if retention(m) < retention_floor:
        return 0.0
    c = compression(m)
    lat = latency_score(m.latency_s, budget_s)
    if c == 0.0:
        return 0.0
    return 2 * c * lat / (c + lat)


def size_score(m: Measurement, budget_per_ktoken_s: float, retention_floor: float = 1.0) -> float:
    """The score with a latency budget that scales with the payload: budget_per_ktoken_s per 1,000 original tokens."""
    return score(m, budget_per_ktoken_s * m.original_tokens / 1000, retention_floor)
