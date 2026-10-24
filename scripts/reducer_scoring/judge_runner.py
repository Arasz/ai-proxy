"""Asks the QA reader whether each answer fact grading still calls lost is stated in the candidate's output,
and prints one JSON line per candidate run with the reader's yes/no verdicts.

  python3 judge_runner.py <results dir> > <results dir>/judge.ndjson

Every answer with a number is also asked with one number changed (a decoy), shuffled in with the real
ones, so a judge that says yes too easily is caught.
"""
import json
import random
import re
import sys
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

from grading import same_fact
from pi_runner import PARALLEL, READER_REPLIES, parse_answers, pi_call
from scoring import qa_kept

JUDGE_SYSTEM = (
    "You check whether a text states a fact. You get a text and numbered items, each a question with a proposed "
    "answer. Reply \"yes\" when the text, read on its own, gives that answer to that question, in these or other "
    "words. Reply \"no\" when the text does not contain it or gives a different answer. Reply with a JSON array of "
    "\"yes\" or \"no\", one per item, in order.")
VOTES_TO_ACCEPT = 2


def decoy(answer: str) -> str | None:
    """The answer with its last number raised by one, keeping its width; None when it has no number."""
    numbers = list(re.finditer(r"\d+", answer))
    if not numbers:
        return None
    last = numbers[-1]
    raised = str(int(last.group()) + 1).zfill(len(last.group()))
    return answer[:last.start()] + raised + answer[last.end():]


def lost(prereg: dict, records: dict[str, list[dict]]) -> dict[tuple[str, str, int], list[dict]]:
    """Per candidate run, the questions fact grading still calls lost."""
    extractive = set(prereg["gates"]["extractive"]["candidates"])
    found = {}
    for candidate, lines in records.items():
        for r in lines:
            questions = prereg["payloads"][r["payload"]]["questions"]
            if candidate in extractive:
                missing = [q for q in questions if not same_fact(r["output"], q["a"])]
            else:
                missing = [q for q, replies in zip(questions, r["replies"]) if not qa_kept([replies], [q], same_fact)]
            if missing:
                found[(candidate, r["payload"], r["run"])] = missing
    return found


def items(key: tuple[str, str, int], questions: list[dict]) -> list[dict]:
    """The real answers and their decoys, in an order fixed by the run but not by which is which."""
    asked = [{"q": q["q"], "a": q["a"], "decoy": False} for q in questions]
    asked += [{"q": q["q"], "a": d, "decoy": True} for q in questions if (d := decoy(q["a"])) is not None]
    random.Random("/".join(map(str, key))).shuffle(asked)
    return asked


def judge_prompt(text: str, asked: list[dict]) -> str:
    listed = "\n".join(f"{i}. Question: {item['q']}\n   Proposed answer: {item['a']}" for i, item in enumerate(asked, 1))
    return f"<text>\n{text}\n</text>\n\nItems:\n{listed}"


def verdicts(raw: str, count: int) -> list[bool]:
    return [answer.strip().casefold() == "yes" for answer in parse_answers(raw, count)]


def accepted(lines: list[dict]) -> tuple[dict[tuple[str, str, int], frozenset[str]], tuple[int, int]]:
    """The real answers the judge accepted per candidate run, and (decoys accepted, decoys asked)."""
    judged, decoys_accepted, decoys = {}, 0, 0
    for line in lines:
        key = (line["candidate"], line["payload"], line["run"])
        for item, votes in zip(line["items"], line["verdicts"]):
            yes = sum(votes) >= VOTES_TO_ACCEPT
            if item["decoy"]:
                decoys += 1
                decoys_accepted += yes
            elif yes:
                judged[key] = judged.get(key, frozenset()) | {item["a"]}
    return judged, (decoys_accepted, decoys)


def main() -> None:
    from preregistration import load

    out = Path(sys.argv[1])
    prereg = load()
    records = {p.stem: [json.loads(line) for line in p.open(encoding="utf-8")] for p in sorted(out.glob("*.jsonl"))}
    outputs = {(c, r["payload"], r["run"]): r["output"] for c, lines in records.items() for r in lines}
    models = prereg["amendments"][-1]["models"]
    reader = f"{models['qa_reader']}:{models['qa_effort']}"

    def one(job):
        key, questions = job
        asked = items(key, questions)
        prompt = judge_prompt(outputs[key], asked)
        raws = [pi_call(reader, JUDGE_SYSTEM, prompt)[0] for _ in range(READER_REPLIES)]
        votes = [verdicts(raw, len(asked)) for raw in raws]
        return {"candidate": key[0], "payload": key[1], "run": key[2], "items": asked,
                "verdicts": [[v[i] for v in votes] for i in range(len(asked))], "raw": raws}

    with ThreadPoolExecutor(PARALLEL) as pool:
        for line in pool.map(one, sorted(lost(prereg, records).items())):
            print(json.dumps(line), flush=True)


if __name__ == "__main__":
    main()
