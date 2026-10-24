"""How captured model turns are billed: threads rebuilt from cache usage, and system-prompt churn.

Reads the `PROXY_CAPTURE` store (`~/.ai-proxy/capture.db`), keeps `/v1/messages` requests that have
a `capture_response` usage row, and groups them by client session (`metadata.user_id`) and model.
The capture holds no response message id, so a thread is rebuilt from the usage alone: a turn
continues the open thread whose last turn's cache read plus cache creation equals this turn's cache
read. Turns that match no thread start one. Prints counts only, never content.

    python3 scripts/thread_usage.py [--db PATH] [--json] [--min-turns N] [--until ISO-TIME]
"""
import argparse
import hashlib
import json
import os
import sqlite3
from collections import Counter
from contextlib import closing
from dataclasses import dataclass

from capture_regions import _parse

MESSAGES_PATH = "/v1/messages"


@dataclass(frozen=True)
class Turn:
    at: str
    key: tuple
    input: int
    read: int
    creation: int
    output: int

    @property
    def cached_total(self) -> int:
        return self.read + self.creation


def link(turns) -> list[list[Turn]]:
    """Threads, in order of their first turn; each turn joins the thread its cache read continues."""
    threads: list[list[Turn]] = []
    for turn in turns:
        match = next((thread for thread in reversed(threads)
                      if thread[-1].key == turn.key and turn.read > 0 and thread[-1].cached_total == turn.read), None)
        if match is None:
            threads.append([turn])
        else:
            match.append(turn)
    return threads


def system_changes(requests) -> dict:
    """Between consecutive requests with the same key, which system block indexes changed."""
    last: dict = {}
    by_block: Counter = Counter()
    pairs = count_changed = 0
    for key, blocks in requests:
        before = last.get(key)
        last[key] = blocks
        if before is None:
            continue
        pairs += 1
        if len(before) != len(blocks):
            count_changed += 1
            continue
        for index, (old, new) in enumerate(zip(before, blocks)):
            if old != new:
                by_block[index] += 1
    return {"pairs": pairs, "by_block": dict(by_block), "count_changed": count_changed}


def _session(request: dict) -> str:
    try:
        return json.loads(request["metadata"]["user_id"])["session_id"]
    except (KeyError, TypeError, ValueError):
        return "?"


def _system_blocks(request: dict) -> list[str]:
    system = request.get("system")
    if system is None:
        return []
    blocks = [system] if isinstance(system, str) else system
    return [hashlib.sha256(json.dumps(b if isinstance(b, str) else b.get("text", b), sort_keys=True).encode()).hexdigest()[:12]
            for b in blocks]


def read(db: str, until: str = "9999"):
    """(turns, system blocks per turn, thread mode per turn) from the store, oldest first."""
    query = ("select r.captured_at, r.body, s.input_tokens, s.cache_read_input_tokens, "
             "s.cache_creation_input_tokens, s.output_tokens from capture_request r "
             "join capture_response s using(request_id) where r.path = ? and s.input_tokens is not null "
             "and r.captured_at <= ? "
             "order by r.captured_at")
    turns, systems, modes = [], {}, {}
    with closing(sqlite3.connect(db)) as connection:
        for at, raw, new, cache_read, creation, output in connection.execute(query, (MESSAGES_PATH, until)):
            request = _parse(raw)
            if not isinstance(request, dict):
                continue
            key = (_session(request), request.get("model"))
            turn = Turn(at, key, new or 0, cache_read or 0, creation or 0, output or 0)
            turns.append(turn)
            modes[turn] = (request.get("thread") or {}).get("type", "stateless")
            systems[turn] = _system_blocks(request)
    return turns, systems, modes


def report(turns, systems, modes, min_turns: int) -> dict:
    threads = link(turns)
    rows = []
    for thread in threads:
        if len(thread) < min_turns:
            continue
        first, last = thread[0], thread[-1]
        rows.append({
            "session": first.key[0][:8], "model": first.key[1], "turns": len(thread),
            "start": first.at[:19], "end": last.at[:19], "first_mode": modes[first],
            "modes": dict(Counter(modes[t] for t in thread)),
            "uncached_input": sum(t.input for t in thread), "cache_creation": sum(t.creation for t in thread),
            "cache_read": sum(t.read for t in thread), "output": sum(t.output for t in thread),
            "last_turn_read": last.read,
        })
    continuing = [t for t in turns if t.read > 0]
    linked = sum(len(th) - 1 for th in threads)
    return {
        "turns": len(turns), "sessions": len({t.key[0] for t in turns}), "threads": len(threads),
        "linked_turns": linked, "turns_with_reads": len(continuing),
        "system": system_changes((index, systems[t]) for index, th in enumerate(threads) for t in th),
        "system_after_header": system_changes((index, systems[t][1:]) for index, th in enumerate(threads) for t in th),
        "rows": rows,
    }


def render(built: dict) -> str:
    lines = [f"{built['turns']} turns, {built['sessions']} sessions, {built['threads']} threads; "
             f"{built['linked_turns']} of {built['turns_with_reads']} turns with cache reads continue a thread exactly",
             "", "| session | model | turns | first mode | uncached in | cache write | cache read | output | last read |",
             "|---|---|---|---|---|---|---|---|---|"]
    for r in built["rows"]:
        lines.append(f"| {r['session']} | {r['model']} | {r['turns']} | {r['first_mode']} | {r['uncached_input']} | "
                     f"{r['cache_creation']} | {r['cache_read']} | {r['output']} | {r['last_turn_read']} |")
    s = built["system"]
    h = built["system_after_header"]
    lines += ["", f"system prompt along threads: {s['pairs']} consecutive pairs, block count changed {s['count_changed']}, "
                  f"changed blocks by index {s['by_block']}; without block 0: changed blocks {h['by_block']}"]
    return "\n".join(lines)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--db", default=os.path.expanduser("~/.ai-proxy/capture.db"))
    parser.add_argument("--json", action="store_true")
    parser.add_argument("--min-turns", type=int, default=5)
    parser.add_argument("--until", default="9999", help="last captured_at to include, so a figure can be reproduced")
    args = parser.parse_args()
    built = report(*read(args.db, args.until), args.min_turns)
    print(json.dumps(built, indent=1) if args.json else render(built))


if __name__ == "__main__":
    main()
