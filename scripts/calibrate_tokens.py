"""Fits one chars-per-token ratio from captured stateless Claude requests and writes tokens_calibration.json.

    python3 scripts/calibrate_tokens.py [--db PATH] [--out PATH]

Thread-mode bodies carry only the new turn while the reported tokens include server-held history,
so only stateless requests without image or document blocks are used.
"""
import argparse
import gzip
import json
import os
import sqlite3
import statistics
import sys
import zlib
from collections import Counter
from contextlib import closing
from dataclasses import dataclass
from pathlib import Path

FOLDS = 5
MEDIA = {"image", "document"}
HERE = Path(__file__).resolve().parent
QUERY = """select q.body, q.captured_at, r.input_tokens, r.cache_read_input_tokens, r.cache_creation_input_tokens
from capture_request q join capture_response r using(request_id)
where q.path like '/v1/messages%' and r.status = 200 and r.input_tokens is not null
order by q.captured_at"""


@dataclass(frozen=True)
class Sample:
    chars: int
    tokens: int
    model: str
    captured_at: str


def _has_media(value) -> bool:
    if isinstance(value, dict):
        kind = value.get("type")
        return (isinstance(kind, str) and kind in MEDIA) or any(_has_media(v) for v in value.values())
    if isinstance(value, list):
        return any(_has_media(v) for v in value)
    return False


def _decode(raw: bytes):
    try:
        if raw[:2] == b"\x1f\x8b":
            raw = gzip.decompress(raw)
        text = raw.decode("utf-8")
        request = json.loads(text)
        return (text, request) if isinstance(request, dict) else None
    except (OSError, ValueError, EOFError, zlib.error):
        return None


def select(rows) -> list[Sample]:
    """Stateless, media-free rows with usage, as samples. Rows are dicts: body, captured_at, input, cache_read, cache_creation."""
    samples = []
    for r in rows:
        if r["input"] is None:
            continue
        decoded = _decode(r["body"])
        if decoded is None:
            continue
        text, request = decoded
        if "thread" in request or _has_media(request):
            continue
        tokens = r["input"] + (r["cache_read"] or 0) + (r["cache_creation"] or 0)
        if tokens > 0:
            samples.append(Sample(len(text), tokens, str(request.get("model", "?")), r["captured_at"]))
    return samples


def fit_ratio(samples: list[Sample]) -> float:
    """Chars per prompt token: the median of the per-request ratios."""
    return statistics.median(s.chars / s.tokens for s in samples)


def holdout_error(samples: list[Sample], folds: int = FOLDS) -> float:
    """Median absolute percentage error of the single ratio, each request predicted by a fit that excluded its fold."""
    errors = []
    for fold in range(folds):
        train = [s for i, s in enumerate(samples) if i % folds != fold]
        ratio = fit_ratio(train)
        errors += [abs(s.chars / ratio - s.tokens) / s.tokens for i, s in enumerate(samples) if i % folds == fold]
    return statistics.median(errors)


def calibration(samples: list[Sample]) -> dict:
    dates = sorted(s.captured_at for s in samples)
    return {
        "ratio": round(fit_ratio(samples), 4),
        "rows": len(samples),
        "models": dict(Counter(s.model for s in samples).most_common()),
        "first": dates[0], "last": dates[-1],
        "holdout_median_abs_error": round(holdout_error(samples), 4),
        "method": "median of per-request chars / prompt tokens (input + cache_read + cache_creation) over stateless "
                  "requests without image or document blocks; chars = length of the decoded body text; "
                  f"error = {FOLDS}-fold holdout median absolute percentage error",
    }


def read_rows(db_path: str):
    with closing(sqlite3.connect(f"file:{db_path}?mode=ro", uri=True)) as connection:
        for body, at, i, cr, cc in connection.execute(QUERY):
            yield {"body": body, "captured_at": at, "input": i, "cache_read": cr, "cache_creation": cc}


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--db", default=os.path.expanduser("~/.ai-proxy/capture.db"))
    parser.add_argument("--out", default=str(HERE / "tokens_calibration.json"))
    args = parser.parse_args(argv)
    result = calibration(select(read_rows(args.db)))
    Path(args.out).write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(result, indent=2))
    return 0


if __name__ == "__main__":
    sys.exit(main())
