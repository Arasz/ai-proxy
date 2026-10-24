"""Token sizes estimated from characters with the ratio fitted in tokens_calibration.json."""
import json
from dataclasses import dataclass
from pathlib import Path

CALIBRATION = Path(__file__).resolve().parent / "tokens_calibration.json"


@dataclass(frozen=True)
class Calibration:
    ratio: float
    rows: int
    models: dict
    first: str
    last: str
    holdout_median_abs_error: float


def load(path=CALIBRATION) -> Calibration:
    data = json.loads(Path(path).read_text(encoding="utf-8"))
    return Calibration(**{k: data[k] for k in Calibration.__dataclass_fields__})


def method(calibration: Calibration) -> str:
    models = ", ".join(f"{k} {v}" for k, v in calibration.models.items())
    return (f"estimated: {calibration.ratio:g} chars/token from {calibration.rows} stateless claude requests "
            f"({models}; {calibration.first[:10]} to {calibration.last[:10]}), "
            f"holdout median error {100 * calibration.holdout_median_abs_error:.1f}%")


def from_chars(chars: int, calibration: Calibration | None = None) -> int:
    """Estimated tokens for a character count; never negative."""
    ratio = (calibration or _default).ratio
    return max(0, round(chars / ratio))


def estimate(text: str, calibration: Calibration | None = None) -> int:
    """Estimated tokens in text."""
    return from_chars(len(text), calibration)


_default = load()
METHOD = method(_default)
