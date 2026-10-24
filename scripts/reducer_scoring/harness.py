"""The reducer scoring harness: runs each candidate over the pre-registered payloads, then scores them.

  python3 harness.py run <candidate>... [--out DIR] [--runs N]
  python3 harness.py report [--out DIR]

Candidates: shipped_rules, kompress_int8, rtk_ccr (local); identity, digest_gpt_stand_in,
summarizer_control (through pi); haiku_digest (claude -p, then the pi reader). Raw runner output lands in DIR/<candidate>.jsonl.
"""
import argparse
import datetime
import json
import os
import platform
import subprocess
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from compression_score import compression, latency_score, retention, score  # noqa: E402
from preregistration import HERE, load, payload_text  # noqa: E402
from scoring import QaRun, ReducerRun, Row, extractive_row, kind_row, qa_row, void_reasons  # noqa: E402

STAND_IN = {"digest_gpt_stand_in"}
HOME = Path.home()
KOMPRESS_PYTHON = os.environ.get("KOMPRESS_PYTHON", str(HOME / "RiderProjects/models-conversion/.venv-kompress/bin/python"))
KOMPRESS_MODELS = os.environ.get("KOMPRESS_MODELS", str(HOME / "RiderProjects/models-conversion/models/kompress"))
RULES_DLL = HERE / "rules_runner/bin/Release/net10.0/RulesRunner.dll"
HAIKU_OUTPUTS = "haiku_digest.digests.ndjson"


def _fields(candidate: str, row: Row, prereg: dict) -> dict:
    budgets = sorted({prereg["budget_s"], *prereg["budget_sensitivity_s"]})
    fields = {"candidate": candidate, "not_applicable": row.not_applicable, "stand_in": candidate in STAND_IN}
    m = row.measurement
    if m is None:
        return fields
    floor = prereg["retention_floor"]
    return fields | {
        "original_chars": m.original_chars, "reduced_chars": m.reduced_chars,
        "compression": round(compression(m), 4), "latency_s": round(m.latency_s, 6),
        "latency_score": round(latency_score(m.latency_s, prereg["budget_s"]), 4),
        "retention": round(retention(m), 4), "answers": f"{m.answers_kept}/{m.answers_total}",
        "score": round(score(m, prereg["budget_s"], floor), 4),
        "score_at": {str(b): round(score(m, b, floor), 4) for b in budgets},
    }


def build_report(prereg: dict, records: dict[str, list[dict]], text_of=payload_text) -> dict:
    """Per payload and per kind rows for every candidate that has records, plus the void verdict."""
    extractive = set(prereg["gates"]["extractive"]["candidates"])
    payload_rows, kind_rows, counted_rows, by_candidate = [], [], [], {}
    for candidate, lines in records.items():
        rows, counted = {}, {}
        for name, payload in prereg["payloads"].items():
            mine = sorted((r for r in lines if r["payload"] == name), key=lambda r: r["run"])
            if not mine:
                continue
            original = text_of(name)
            if candidate in extractive:
                reducer_runs = [ReducerRun(r["output"], r["latency_s"], r.get("added_chars", 0)) for r in mine]
                rows[name] = extractive_row(original, payload["questions"], reducer_runs)
                counted[name] = extractive_row(original, payload["questions"], reducer_runs, count_untouched=True)
            else:
                rows[name] = counted[name] = qa_row(original, payload["questions"],
                                                    [QaRun(r["output"], r["latency_s"], r["replies"]) for r in mine])
            payload_rows.append(_fields(candidate, rows[name], prereg) | {"payload": name, "kind": payload["kind"]})
        by_candidate[candidate] = rows
        kinds = dict.fromkeys(prereg["payloads"][n]["kind"] for n in rows)
        for kind in kinds:
            for view, target in ((rows, kind_rows), (counted, counted_rows)):
                pooled = kind_row([row for n, row in view.items() if prereg["payloads"][n]["kind"] == kind])
                target.append(_fields(candidate, pooled, prereg) | {"kind": kind})
    void = _incomplete(prereg, records)
    for control in ("identity", "summarizer_control"):
        if control not in by_candidate:
            void.append(f"{control} did not run, so the controls cannot clear the run")
    if not void:
        void = void_reasons(by_candidate["identity"], by_candidate["summarizer_control"])
    return {"payloads": payload_rows, "kinds": kind_rows, "kinds_counted": counted_rows, "void": void}


def _incomplete(prereg: dict, records: dict[str, list[dict]]) -> list[str]:
    """Reasons the records cannot be scored as they stand: a run missing or repeated, or a reducer that threw."""
    expected = sorted((name, run) for name in prereg["payloads"] for run in range(prereg["runs"]))
    reasons = []
    for candidate, lines in records.items():
        if sorted((r["payload"], r["run"]) for r in lines) != expected:
            reasons.append(f"{candidate} does not have exactly {prereg['runs']} runs of every payload")
        threw = sorted({r["payload"] for r in lines if r.get("failures", 0)})
        if threw:
            reasons.append(f"{candidate}: a reducer threw on {', '.join(threw)}, so its untouched output is not n/a")
    return reasons


def _command(candidate: str, runs: int, names: list[str], out: Path) -> tuple[list[str], dict | None]:
    clean = {"HOME": str(HOME), "PATH": os.environ["PATH"]}
    if candidate == "shipped_rules":
        return ["dotnet", str(RULES_DLL), str(HERE / "payloads"), str(runs), *names], clean
    if candidate == "kompress_int8":
        return [KOMPRESS_PYTHON, str(HERE / "kompress_runner.py"), KOMPRESS_MODELS, str(runs), *names], clean
    if candidate == "rtk_ccr":
        return [sys.executable, str(HERE / "rtk_runner.py"), str(runs), *names], None
    if candidate in ("identity", "digest_gpt_stand_in", "summarizer_control"):
        return [sys.executable, str(HERE / "pi_runner.py"), candidate, str(runs), *names], None
    if candidate == "haiku_digest":
        return [sys.executable, str(HERE / "pi_runner.py"), candidate, str(runs),
                "--outputs", str(out / HAIKU_OUTPUTS), *names], None
    raise SystemExit(f"unknown candidate {candidate}")


def run(candidates: list[str], out: Path, runs: int) -> None:
    out = out.resolve()
    out.mkdir(parents=True, exist_ok=True)
    recorded = out / "environment.json"
    environments = json.loads(recorded.read_text(encoding="utf-8")) if recorded.exists() else {}
    environments |= dict.fromkeys(candidates, _environment())
    recorded.write_text(json.dumps(environments, indent=1) + "\n", encoding="utf-8")
    names = list(load()["payloads"])
    if "shipped_rules" in candidates:
        subprocess.run(["dotnet", "build", str(HERE / "rules_runner"), "-c", "Release", "-v", "q"], check=True)
    for candidate in candidates:
        if candidate == "haiku_digest":
            with (out / HAIKU_OUTPUTS).open("w", encoding="utf-8") as sink:
                made = subprocess.run([sys.executable, str(HERE / "claude_digest_runner.py"), str(runs), *names],
                                      cwd=HERE, stdout=sink)
            if made.returncode != 0:
                raise SystemExit(f"haiku digests failed (exit {made.returncode}); partial output kept in {out / HAIKU_OUTPUTS}")
        command, env = _command(candidate, runs, names, out)
        target = out / f"{candidate}.jsonl"
        with target.open("w", encoding="utf-8") as sink:
            done = subprocess.run(command, cwd=HERE, env=env, stdout=sink)
        if done.returncode != 0:
            raise SystemExit(f"{candidate} failed (exit {done.returncode}); partial output kept in {target}")
        print(f"{candidate}: {sum(1 for _ in target.open(encoding='utf-8'))} runs -> {target}", flush=True)


def _environment() -> dict:
    def out(*command):
        try:
            return subprocess.run(command, capture_output=True, text=True, check=False).stdout.strip()
        except OSError:
            return "unavailable"
    return {
        "date": datetime.date.today().isoformat(),
        "machine": f"{out('sysctl', '-n', 'machdep.cpu.brand_string')}, "
                   f"{int(out('sysctl', '-n', 'hw.memsize') or 0) // 2**30} GB, macOS {platform.mac_ver()[0]}",
        "commit": out("git", "-C", str(HERE), "rev-parse", "--short", "HEAD"),
        "rtk_proxy": out("rtk-proxy", "--version"),
        "pi": out("pi", "--version"),
    }


def _table(rows: list[dict], label: str) -> str:
    head = f"| candidate | {label} | score | compression | latency s | retention | answers | score @0.5 s | score @2 s |\n"
    head += "|---|---|---|---|---|---|---|---|---|\n"
    lines = []
    for r in rows:
        name = r["candidate"] + (" (stand-in)" if r["stand_in"] else "")
        where = r.get("payload", r["kind"])
        if r["not_applicable"]:
            lines.append(f"| {name} | {where} | n/a | | | | | | |")
            continue
        lines.append(f"| {name} | {where} | {r['score']} | {r['compression']} | {r['latency_s']:.4f} | "
                     f"{r['retention']} | {r['answers']} | {r['score_at']['0.5']} | {r['score_at']['2.0']} |")
    return head + "\n".join(lines) + "\n"


def report(out: Path) -> None:
    records = {p.stem: [json.loads(line) for line in p.open(encoding="utf-8")] for p in sorted(out.glob("*.jsonl"))}
    recorded = out / "environment.json"
    environments = json.loads(recorded.read_text(encoding="utf-8")) if recorded.exists() else {}
    built = build_report(load(), records) | {"environment": {c: environments.get(c, "not recorded") for c in records}}
    (out / "report.json").write_text(json.dumps(built, indent=1) + "\n", encoding="utf-8")
    verdict = "VOID: " + "; ".join(built["void"]) if built["void"] else "controls clear the run"
    md = f"{verdict}\n\n{json.dumps(built['environment'])}\n\n## Per kind\n\n{_table(built['kinds'], 'kind')}" \
         f"\n## Per kind, untouched payloads counted as nothing removed\n\n{_table(built['kinds_counted'], 'kind')}" \
         f"\n## Per payload\n\n{_table(built['payloads'], 'payload')}"
    (out / "report.md").write_text(md, encoding="utf-8")
    print(md)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("action", choices=["run", "report"])
    parser.add_argument("candidates", nargs="*")
    parser.add_argument("--out", type=Path, default=HERE / "results")
    parser.add_argument("--runs", type=int, default=load()["runs"])
    args = parser.parse_args()
    if args.action == "run":
        run(args.candidates, args.out, args.runs)
    else:
        report(args.out)


if __name__ == "__main__":
    main()
