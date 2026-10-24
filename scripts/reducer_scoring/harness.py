"""The reducer scoring harness: runs each candidate over the pre-registered payloads, then scores them.

  python3 harness.py run <candidate>... [--out DIR] [--runs N]
  python3 harness.py report [--out DIR]

Candidates: shipped_rules, kompress_int8, rtk_ccr (local); rtk_ccr_reader, rtk_ccr_retrieval (claude -p reads
the saved rtk_ccr previews, the second may retrieve the original); identity, digest_gpt_stand_in,
summarizer_control (through pi); haiku_digest, haiku_digest_checked (claude -p, then the pi reader). Raw runner output lands in DIR/<candidate>.jsonl.
"""
import argparse
import datetime
import json
import os
import platform
import statistics
import subprocess
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from compression_score import compression, latency_score, retention, score  # noqa: E402
from preregistration import HERE, load, payload_text  # noqa: E402
import tokens  # noqa: E402
from grading import same_fact  # noqa: E402
from judge_runner import accepted, lost  # noqa: E402
from retrieval import retrieval_counts  # noqa: E402
from scoring import QaRun, ReducerRun, Row, extractive_row, kind_row, qa_row, void_reasons  # noqa: E402

STAND_IN = {"digest_gpt_stand_in"}
HOME = Path.home()
KOMPRESS_PYTHON = os.environ.get("KOMPRESS_PYTHON", str(HOME / "RiderProjects/models-conversion/.venv-kompress/bin/python"))
KOMPRESS_MODELS = os.environ.get("KOMPRESS_MODELS", str(HOME / "RiderProjects/models-conversion/models/kompress"))
RULES_DLL = HERE / "rules_runner/bin/Release/net10.0/RulesRunner.dll"
HAIKU = ("haiku_digest", "haiku_digest_checked")
RTK_READER = ("rtk_ccr_reader", "rtk_ccr_retrieval")
JUDGE_OUTPUTS = "judge.ndjson"


def _digests(out: Path, candidate: str) -> Path:
    return out / f"{candidate}.digests.ndjson"


def _fields(candidate: str, row: Row, prereg: dict, relaxed_row: Row | None = None) -> dict:
    """A row's report fields; the relaxed score comes from relaxed_row, the same row graded by fact."""
    budgets = sorted({prereg["budget_s"], *prereg["budget_sensitivity_s"]})
    fields = {"candidate": candidate, "not_applicable": row.not_applicable, "stand_in": candidate in STAND_IN}
    m = row.measurement
    if m is None:
        return fields
    floor = prereg["retention_floor"]
    r = (relaxed_row or row).measurement
    relaxed = score(r, prereg["budget_s"], prereg["relaxed_retention_floor"]) * retention(r)
    return fields | {
        "original_tokens": m.original_tokens, "reduced_tokens": m.reduced_tokens,
        "compression": round(compression(m), 4), "latency_s": round(m.latency_s, 6),
        "latency_score": round(latency_score(m.latency_s, prereg["budget_s"]), 4),
        "retention": round(retention(m), 4), "answers": f"{m.answers_kept}/{m.answers_total}",
        "score": round(score(m, prereg["budget_s"], floor), 4),
        "score_at": {str(b): round(score(m, b, floor), 4) for b in budgets},
        "answers_relaxed": f"{r.answers_kept}/{r.answers_total}", "score_relaxed": round(relaxed, 4),
    }


JUDGE_DECOY_LIMIT = 0.1


def _judge(prereg: dict, records: dict[str, list[dict]], judge) -> tuple[dict, dict]:
    """The judged answers to use, and the judge's own check: decoys accepted, and how much of the summarizer it saved."""
    if judge is None:
        return {}, {"void": ["the judge did not run"]}
    judged, (decoys_accepted, decoys) = judge
    lost_summary = sum(len(q) for k, q in lost(prereg, records).items() if k[0] == "summarizer_control")
    saved_summary = sum(len(a) for k, a in judged.items() if k[0] == "summarizer_control")
    void = []
    if decoys and decoys_accepted / decoys > JUDGE_DECOY_LIMIT:
        void.append(f"the judge accepted {decoys_accepted} of {decoys} decoys, over {JUDGE_DECOY_LIMIT:.0%}")
    check = {"decoys_accepted": decoys_accepted, "decoys": decoys,
             "summarizer_accepted": saved_summary, "summarizer_lost": lost_summary, "void": void}
    return ({} if void else judged), check


def build_report(prereg: dict, records: dict[str, list[dict]], text_of=payload_text, judge=None) -> dict:
    """Per payload and per kind rows for every candidate that has records, plus the void verdict.

    judge is (accepted answers per candidate run, (decoys accepted, decoys asked)) from judge_runner.accepted;
    its answers count in the relaxed view only.
    """
    extractive = set(prereg["gates"]["extractive"]["candidates"])
    judged, judge_check = _judge(prereg, records, judge)
    payload_rows, kind_rows, counted_rows, by_candidate = [], [], [], {}
    for candidate, lines in records.items():
        rows, counted, by_fact, counted_by_fact = {}, {}, {}, {}
        for name, payload in prereg["payloads"].items():
            mine = sorted((r for r in lines if r["payload"] == name), key=lambda r: r["run"])
            if not mine:
                continue
            original = text_of(name)
            if candidate in extractive:
                def reducer_runs(judging):
                    return [ReducerRun(r["output"], r["latency_s"], _added_tokens(r),
                                       judged.get((candidate, name, r["run"]), frozenset()) if judging else frozenset())
                            for r in mine]
                for view, match in ((rows, None), (by_fact, same_fact)):
                    view[name] = extractive_row(original, payload["questions"], reducer_runs(match), **_by(match))
                for view, match in ((counted, None), (counted_by_fact, same_fact)):
                    view[name] = extractive_row(original, payload["questions"], reducer_runs(match),
                                                count_untouched=True, **_by(match))
            else:
                def charge(r):
                    return {"added_tokens": _added_tokens(r), "fetched_tokens": _fetched_tokens(r, original)}
                qa_runs = [QaRun(r["output"], r["latency_s"], r["replies"], **charge(r)) for r in mine]
                judged_runs = [QaRun(r["output"], r["latency_s"], r["replies"],
                                     judged.get((candidate, name, r["run"]), frozenset()), **charge(r)) for r in mine]
                rows[name] = counted[name] = qa_row(original, payload["questions"], qa_runs)
                by_fact[name] = counted_by_fact[name] = qa_row(original, payload["questions"], judged_runs, same_fact)
            payload_rows.append(_fields(candidate, rows[name], prereg, by_fact[name])
                                | {"payload": name, "kind": payload["kind"]}
                                | _retrieval_fields(mine) | _tool_fields(mine, original))
        by_candidate[candidate] = rows
        kinds = dict.fromkeys(prereg["payloads"][n]["kind"] for n in rows)
        for kind in kinds:
            def pool(view):
                return kind_row([row for n, row in view.items() if prereg["payloads"][n]["kind"] == kind])
            for view, fact_view, target in ((rows, by_fact, kind_rows), (counted, counted_by_fact, counted_rows)):
                of_kind = [r for r in lines if prereg["payloads"][r["payload"]]["kind"] == kind]
                target.append(_fields(candidate, pool(view), prereg, pool(fact_view)) | {"kind": kind}
                              | _retrieval_fields(of_kind))
    void = _incomplete(prereg, records)
    for control in ("identity", "summarizer_control"):
        if control not in by_candidate:
            void.append(f"{control} did not run, so the controls cannot clear the run")
    if not void:
        void = void_reasons(by_candidate["identity"], by_candidate["summarizer_control"])
    return {"payloads": payload_rows, "kinds": kind_rows, "kinds_counted": counted_rows, "void": void,
            "judge": judge_check, "token_method": tokens.METHOD}


def _added_tokens(record: dict) -> int:
    """Tokens a run added outside its output; older records hold characters, converted by the same estimate."""
    return record.get("added_tokens", tokens.from_chars(record.get("added_chars", 0)))


def _fetched_tokens(record: dict, original: str) -> int:
    """The original's tokens when the reader retrieved it on any call of the run; a fetched original is not a saving."""
    return tokens.estimate(original) if any(record.get("retrievals", ())) else 0


def _retrieval_fields(lines: list[dict]) -> dict:
    """How often the reader retrieved, for candidates whose records say; nothing for the rest."""
    if not lines or "retrievals" not in lines[0]:
        return {}
    retrieved, calls = retrieval_counts(lines)
    return {"retrievals": f"{retrieved}/{calls}", "retrieval_rate": round(retrieved / calls, 4)}


def _tool_fields(lines: list[dict], original: str) -> dict:
    """The injected tool definition against the saving it buys, for candidates that add tokens outside their output."""
    if not any("added_tokens" in r or "added_chars" in r for r in lines):
        return {}
    shown = statistics.median_low(tokens.estimate(r["output"]) for r in lines)
    return {"tool_definition_tokens": statistics.median_low(_added_tokens(r) for r in lines),
            "saving_tokens": tokens.estimate(original) - shown}


def _by(match) -> dict:
    return {} if match is None else {"match": match}


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
    if candidate in RTK_READER:
        return [sys.executable, str(HERE / "claude_reader_runner.py"), candidate, str(runs),
                str(out / "rtk_ccr.jsonl"), *names], None
    if candidate in ("identity", "digest_gpt_stand_in", "summarizer_control"):
        return [sys.executable, str(HERE / "pi_runner.py"), candidate, str(runs), *names], None
    if candidate in HAIKU:
        return [sys.executable, str(HERE / "pi_runner.py"), candidate, str(runs),
                "--outputs", str(_digests(out, candidate)), *names], None
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
        if candidate in HAIKU:
            digests = _digests(out, candidate)
            with digests.open("w", encoding="utf-8") as sink:
                made = subprocess.run([sys.executable, str(HERE / "claude_digest_runner.py"), candidate, str(runs),
                                       *names], cwd=HERE, stdout=sink)
            if made.returncode != 0:
                raise SystemExit(f"haiku digests failed (exit {made.returncode}); partial output kept in {digests}")
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
    head = f"| candidate | {label} | score | compression | latency s | retention | answers | score @0.5 s | score @2 s | relaxed |\n"
    head += "|---|---|---|---|---|---|---|---|---|---|\n"
    lines = []
    for r in rows:
        name = r["candidate"] + (" (stand-in)" if r["stand_in"] else "")
        where = r.get("payload", r["kind"])
        if r["not_applicable"]:
            lines.append(f"| {name} | {where} | n/a | | | | | | | |")
            continue
        lines.append(f"| {name} | {where} | {r['score']} | {r['compression']} | {r['latency_s']:.4f} | "
                     f"{r['retention']} | {r['answers']} | {r['score_at']['0.5']} | {r['score_at']['2.0']} | "
                     f"{r['score_relaxed']} |")
    return head + "\n".join(lines) + "\n"


def _markdown(built: dict) -> str:
    verdict = "VOID: " + "; ".join(built["void"]) if built["void"] else "controls clear the run"
    j = built["judge"]
    judge_line = ("judge: " + ("; ".join(j["void"]) + ", so the relaxed view is graded by fact only" if j["void"] else
                  f"{j['decoys_accepted']}/{j['decoys']} decoys accepted, "
                  f"{j['summarizer_accepted']}/{j['summarizer_lost']} summarizer losses accepted"))
    return f"{verdict}\n\n{judge_line}\n\nsizes: {built['token_method']}\n\n{json.dumps(built['environment'])}" \
           f"\n\n## Per kind\n\n{_table(built['kinds'], 'kind')}" \
           f"\n## Per kind, untouched payloads counted as nothing removed\n\n{_table(built['kinds_counted'], 'kind')}" \
           f"\n## Per payload\n\n{_table(built['payloads'], 'payload')}"


def report(out: Path) -> None:
    records = {p.stem: [json.loads(line) for line in p.open(encoding="utf-8")] for p in sorted(out.glob("*.jsonl"))}
    recorded = out / "environment.json"
    environments = json.loads(recorded.read_text(encoding="utf-8")) if recorded.exists() else {}
    judge_path = out / JUDGE_OUTPUTS
    judge = accepted([json.loads(line) for line in judge_path.open(encoding="utf-8")]) if judge_path.exists() else None
    built = build_report(load(), records, judge=judge) | {"environment": {c: environments.get(c, "not recorded") for c in records}}
    (out / "report.json").write_text(json.dumps(built, indent=1) + "\n", encoding="utf-8")
    md = _markdown(built)
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
