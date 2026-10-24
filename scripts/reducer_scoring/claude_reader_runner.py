"""Reads rtk's saved CCR previews with the QA reader through `claude -p`, optionally letting it retrieve.

  python3 claude_reader_runner.py <rtk_ccr_reader|rtk_ccr_retrieval> <runs> <rtk_ccr.jsonl> <payload name>...

One JSON line per payload and run. The call is isolated like claude_digest_runner's (no tools, settings,
MCP servers or saved session, proxy variables cleared). A quota, rate-limit or credit error is counted;
the run aborts on the third, and never falls back to another provider.
"""
import json
import os
import statistics
import subprocess
import sys
import threading
import time
from concurrent.futures import ThreadPoolExecutor

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))

import tokens  # noqa: E402
from claude_digest_runner import child_env, command
from pi_runner import PARALLEL, READER_REPLIES, READER_SYSTEM, parse_answers, qa_prompt
from retrieval import RETRIEVAL_SYSTEM, marker_ids, parse_retrieve, retrieval_prompt

READER = "claude-opus-5-5"
EFFORT = "low"
QUOTA_ERRORS_ALLOWED = 2
QUOTA = ("rate limit", "rate_limit", "quota", "credit", "usage limit", "429", "overloaded", "billing")
ARMS = {"rtk_ccr_reader": False, "rtk_ccr_retrieval": True}


class QuotaError(RuntimeError):
    """The call failed because the account is out of quota, credit or rate."""


def is_quota_error(text: str) -> bool:
    """True when the failure text reads as a quota, rate-limit or credit problem."""
    lowered = text.lower()
    return any(word in lowered for word in QUOTA)


def parse_reader_result(raw: str, model: str) -> tuple[str, float]:
    """The reply and the model's own time in seconds; raises QuotaError or RuntimeError when there is no reply."""
    result = json.loads(raw)
    text = result.get("result", "")
    if result.get("is_error") or not text.strip():
        message = f"claude -p did not produce a reply: {text[:300]!r}"
        raise (QuotaError if is_quota_error(text) else RuntimeError)(message)
    models = set(result.get("modelUsage", {model: {}}))
    if models != {model}:
        raise RuntimeError(f"the reply came from {sorted(models)}, not {model}")
    return text, result["duration_api_ms"] / 1000


def read_once(ask, preview: str, original: str, questions: list[dict], retrieval: bool) -> dict:
    """One reader reply, with at most one retrieval; ask(system, prompt) returns (reply, wall seconds)."""
    system = RETRIEVAL_SYSTEM if retrieval else READER_SYSTEM
    raw, wall = ask(system, qa_prompt(preview, questions))
    found = parse_retrieve(raw, preview) if retrieval else None
    if found is None:
        return {"raw": raw, "retrieved": False, "id": None, "first_wall_s": wall, "wall_s": [wall]}
    raw2, wall2 = ask(READER_SYSTEM, retrieval_prompt(preview, original, questions))
    return {"raw": raw2, "retrieved": True, "id": found, "first_wall_s": wall, "wall_s": [wall, wall2]}


def check_markers(source: list[dict]) -> dict[str, str | None]:
    """The marker id of each payload's own output; raises when it differs between runs or repeats across payloads."""
    ids: dict[str, str | None] = {}
    for record in source:
        found = marker_ids(record["output"])
        if len(found) > 1:
            raise RuntimeError(f"{record['payload']} has {len(found)} markers")
        mine = found[0] if found else None
        if ids.setdefault(record["payload"], mine) != mine:
            raise RuntimeError(f"{record['payload']} changes its marker id between runs")
    seen = [i for i in ids.values() if i]
    if len(seen) != len(set(seen)):
        raise RuntimeError("two payloads share a marker id")
    return ids


class Ask:
    """The isolated claude -p call, counting quota errors across threads."""

    def __init__(self):
        self.env = child_env(dict(os.environ))
        self.quota_errors = 0
        self.lock = threading.Lock()

    def __call__(self, system: str, prompt: str) -> tuple[str, float]:
        while True:
            started = time.perf_counter()
            done = subprocess.run(command(system, EFFORT, READER), input=prompt, env=self.env,
                                  capture_output=True, text=True, timeout=900)
            wall = time.perf_counter() - started
            try:
                if done.returncode != 0 and not done.stdout.strip():
                    raise (QuotaError if is_quota_error(done.stderr) else RuntimeError)(
                        f"claude -p exit {done.returncode}: {done.stderr[:300]}")
                return parse_reader_result(done.stdout, READER)[0], round(wall, 3)
            except QuotaError:
                with self.lock:
                    self.quota_errors += 1
                    if self.quota_errors > QUOTA_ERRORS_ALLOWED:
                        raise
                time.sleep(30)


def main() -> None:
    from preregistration import load, payload_text

    arm, runs, source_path, names = sys.argv[1], int(sys.argv[2]), sys.argv[3], sys.argv[4:]
    retrieval = ARMS[arm]
    with open(source_path, encoding="utf-8") as lines:
        source = [json.loads(line) for line in lines]
    check_markers(source)
    by_key = {(r["payload"], r["run"]): r for r in source}
    prereg, ask = load(), Ask()

    def one_run(job: tuple[str, int]) -> dict:
        name, run = job
        saved = by_key[(name, run)]
        original, questions = payload_text(name), prereg["payloads"][name]["questions"]
        reads = [read_once(ask, saved["output"], original, questions, retrieval) for _ in range(READER_REPLIES)]
        parsed = [parse_answers(r["raw"], len(questions)) for r in reads]
        replies = [[p[i] for p in parsed] for i in range(len(questions))]
        firsts = [r["first_wall_s"] for r in reads if r["retrieved"]]
        extra = statistics.median(firsts) if firsts else 0.0
        return {"payload": name, "run": run, "output": saved["output"],
                "added_tokens": saved.get("added_tokens", 0) or _from_chars(saved),
                "rtk_latency_s": saved["latency_s"], "retrieval_round_trip_s": extra,
                "latency_s": saved["latency_s"] + extra, "replies": replies,
                "raw_replies": [r["raw"] for r in reads], "retrievals": [r["retrieved"] for r in reads],
                "retrieved_ids": [r["id"] for r in reads], "reader_wall_s": [r["wall_s"] for r in reads],
                "reader": f"{READER}:{EFFORT} via claude -p"}

    jobs = [(name, run) for name in names for run in range(runs)]
    with ThreadPoolExecutor(PARALLEL) as pool:
        for record in pool.map(one_run, jobs):
            print(json.dumps(record), flush=True)


def _from_chars(record: dict) -> int:
    return tokens.from_chars(record.get("added_chars", 0))


if __name__ == "__main__":
    main()
