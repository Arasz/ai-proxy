"""Where the characters in captured model requests go, by region kind, and how much of each is new.

Reads the `PROXY_CAPTURE` store (`~/.ai-proxy/capture.db`), keeps `/v1/messages` requests only,
and splits each body into regions: system blocks, tool definitions, and message blocks by role
and type. A region is identified by a hash of its kind and content, so `new` counts a region the
first time it is sent and `sent` counts every send. A cache-safe compressor runs once per new
region; every later send must reproduce its output byte-for-byte. Prints counts only, never content.

    python3 scripts/capture_regions.py [--db PATH] [--json]
"""
import argparse
import gzip
import hashlib
import json
import os
import sqlite3
import sys
from collections import Counter
from dataclasses import dataclass, field

MESSAGES_PATH = "/v1/messages"
CHARS_PER_TOKEN = 4
# Signed thinking is verified upstream; altering a byte rejects the request.
UNTOUCHABLE = {"assistant.thinking"}


@dataclass(frozen=True)
class Region:
    kind: str
    chars: int
    digest: str


@dataclass
class KindTotals:
    count: int = 0
    sent_chars: int = 0
    new_chars: int = 0


@dataclass
class Summary:
    requests: int = 0
    unreadable: int = 0
    cache_breakpoints: int = 0
    models: Counter = field(default_factory=Counter)
    modes: Counter = field(default_factory=Counter)
    kinds: dict = field(default_factory=dict)

    def as_dict(self) -> dict:
        return {
            "requests": self.requests,
            "unreadable": self.unreadable,
            "cache_breakpoints": self.cache_breakpoints,
            "models": dict(self.models),
            "modes": dict(self.modes),
            "kinds": {k: vars(v) for k, v in sorted(self.kinds.items())},
        }


def _canonical(value) -> str:
    return json.dumps(value, sort_keys=True, ensure_ascii=False)


def _without_cache_control(block):
    if isinstance(block, dict):
        return {k: v for k, v in block.items() if k != "cache_control"}
    return block


def _region(kind: str, chars: int, content) -> Region:
    digest = hashlib.sha256(f"{kind}\0{_canonical(_without_cache_control(content))}".encode()).hexdigest()
    return Region(kind, chars, digest)


def _text_of(content) -> str:
    if isinstance(content, str):
        return content
    if isinstance(content, list):
        return "".join(b.get("text", "") for b in content if isinstance(b, dict))
    return ""


def _block_chars(block: dict) -> tuple[str, int]:
    kind = block.get("type", "other")
    if kind == "text":
        return "text", len(block.get("text", ""))
    if kind == "thinking":
        return "thinking", len(block.get("thinking", ""))
    if kind == "redacted_thinking":
        return "thinking", len(block.get("data", ""))
    if kind == "tool_use":
        return "tool_use", len(_canonical(block.get("input", {})))
    if kind == "tool_result":
        return "tool_result", len(_text_of(block.get("content", "")))
    if kind in ("image", "document"):
        return kind, len(block.get("source", {}).get("data", ""))
    return kind, len(_canonical(block))


def regions(request: dict) -> list[Region]:
    """Split one request body into regions, in the order they are sent."""
    found = []
    system = request.get("system")
    if isinstance(system, str):
        found.append(_region("system", len(system), system))
    elif isinstance(system, list):
        found += [_region("system", len(b.get("text", "")), b) for b in system if isinstance(b, dict)]
    for tool in request.get("tools") or []:
        found.append(_region("tools", len(_canonical(tool)), tool))
    for message in request.get("messages") or []:
        role = message.get("role", "other")
        content = message.get("content")
        if isinstance(content, str):
            found.append(_region(f"{role}.text", len(content), content))
            continue
        for block in content or []:
            if isinstance(block, dict):
                kind, chars = _block_chars(block)
                found.append(_region(f"{role}.{kind}", chars, block))
    return found


def _breakpoints(request: dict) -> int:
    blocks = []
    if isinstance(request.get("system"), list):
        blocks += request["system"]
    blocks += request.get("tools") or []
    for message in request.get("messages") or []:
        if isinstance(message.get("content"), list):
            blocks += message["content"]
    return sum(1 for b in blocks if isinstance(b, dict) and "cache_control" in b)


def _mode(request: dict) -> str:
    """`stateless` when the client resends history; `thread.<type>` when the server holds it."""
    thread = request.get("thread")
    if not isinstance(thread, dict):
        return "stateless"
    return f"thread.{thread.get('type', '?')}"


def _parse(raw: bytes):
    try:
        if raw[:2] == b"\x1f\x8b":
            raw = gzip.decompress(raw)
        request = json.loads(raw)
        return request if isinstance(request, dict) else None
    except (OSError, ValueError):
        return None


def summarize(bodies) -> Summary:
    """Total sent and first-seen characters per region kind over request bodies, oldest first."""
    summary, seen = Summary(), set()
    for raw in bodies:
        request = _parse(raw)
        if request is None:
            summary.unreadable += 1
            continue
        summary.requests += 1
        summary.cache_breakpoints += _breakpoints(request)
        summary.models[str(request.get("model", "?"))] += 1
        summary.modes[_mode(request)] += 1
        for r in regions(request):
            totals = summary.kinds.setdefault(r.kind, KindTotals())
            totals.count += 1
            totals.sent_chars += r.chars
            if r.digest not in seen:
                seen.add(r.digest)
                totals.new_chars += r.chars
    return summary


def read_bodies(db_path: str):
    """Captured `/v1/messages` bodies, oldest first."""
    with sqlite3.connect(f"file:{db_path}?mode=ro", uri=True) as connection:
        rows = connection.execute(
            "select body from capture_request where path = ? order by captured_at", (MESSAGES_PATH,)).fetchall()
    connection.close()
    for (raw,) in rows:
        yield raw


def render(summary: Summary) -> str:
    sent_total = sum(k.sent_chars for k in summary.kinds.values()) or 1
    new_total = sum(k.new_chars for k in summary.kinds.values()) or 1
    lines = [
        f"requests {summary.requests}, unreadable {summary.unreadable}, "
        f"cache breakpoints {summary.cache_breakpoints}, ~{CHARS_PER_TOKEN} chars per token",
        "models " + ", ".join(f"{k} {v}" for k, v in summary.models.most_common()),
        "modes  " + ", ".join(f"{k} {v}" for k, v in summary.modes.most_common()),
        f"{'kind':<22}{'regions':>9}{'sent ~tok':>12}{'sent %':>8}{'new ~tok':>11}{'new %':>7}  note",
    ]
    for kind, k in sorted(summary.kinds.items(), key=lambda item: -item[1].sent_chars):
        note = "signed, never touched" if kind in UNTOUCHABLE else ""
        lines.append(
            f"{kind:<22}{k.count:>9}{k.sent_chars // CHARS_PER_TOKEN:>12}"
            f"{100 * k.sent_chars / sent_total:>7.1f}%{k.new_chars // CHARS_PER_TOKEN:>11}"
            f"{100 * k.new_chars / new_total:>6.1f}%  {note}".rstrip())
    return "\n".join(lines)


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--db", default=os.path.expanduser("~/.ai-proxy/capture.db"))
    parser.add_argument("--json", action="store_true", help="print the summary as JSON")
    args = parser.parse_args(argv)
    summary = summarize(read_bodies(args.db))
    print(json.dumps(summary.as_dict(), indent=2) if args.json else render(summary))
    return 0


if __name__ == "__main__":
    sys.exit(main())
