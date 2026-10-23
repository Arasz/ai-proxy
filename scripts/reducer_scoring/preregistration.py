"""Reads the committed pre-registration and the payloads it names."""
import json
from pathlib import Path

HERE = Path(__file__).resolve().parent


def load() -> dict:
    """The pre-registration exactly as committed."""
    return json.loads((HERE / "preregistration.json").read_text(encoding="utf-8"))


def payload_text(name: str) -> str:
    """The payload's text, read as UTF-8 without newline translation."""
    return (HERE / "payloads" / f"{name}.txt").read_bytes().decode("utf-8")
