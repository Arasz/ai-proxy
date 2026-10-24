import contextlib
import subprocess
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from harness import _table, build_report, run

FACTS = "Dana Okonkwo merged PR 8842 after the incident. "
TEXTS = {"a": FACTS * 10, "b": FACTS * 20, "c": "build: " + FACTS * 8}
QUESTIONS = [{"q": "Who?", "a": "Dana Okonkwo"}, {"q": "PR?", "a": "PR 8842"}]
PREREG = {
    "budget_s": 1.0, "budget_sensitivity_s": [0.5, 2.0], "retention_floor": 1.0, "runs": 3,
    "gates": {"extractive": {"candidates": ["shipped_rules"]},
              "abstractive": {"candidates": ["haiku_digest", "digest_gpt_stand_in", "summarizer_control", "identity"]}},
    "payloads": {
        "a": {"kind": "prose", "questions": QUESTIONS},
        "b": {"kind": "prose", "questions": QUESTIONS},
        "c": {"kind": "build", "questions": QUESTIONS},
    },
}
ALL = [["Dana Okonkwo"] * 3, ["PR 8842"] * 3]
NONE = [["UNKNOWN"] * 3, ["UNKNOWN"] * 3]
KEPT = "Dana Okonkwo PR 8842"


def runs(payload, outputs, latency=0.1, replies=None):
    if isinstance(outputs, str):
        outputs = [outputs] * 3
    return [{"payload": payload, "run": r, "output": o, "latency_s": latency,
             **({"replies": replies} if replies is not None else {})} for r, o in enumerate(outputs)]


def records():
    return {
        # Run 0 loses an answer on payload a; the median run keeps both.
        "shipped_rules": runs("a", ["Dana Okonkwo", KEPT, KEPT]) + runs("b", TEXTS["b"]) + runs("c", KEPT, 0.25),
        "identity": [r for p in TEXTS for r in runs(p, TEXTS[p], 0.0, ALL)],
        # The summary still contains both answers word for word, but the reader misses them.
        "summarizer_control": [r for p in TEXTS for r in runs(p, KEPT, 2.0, NONE)],
        # The digest paraphrases both answers away, but the reader still finds them.
        "digest_gpt_stand_in": [r for p in TEXTS for r in runs(p, "Okonkwo (Dana), pull request 8842", 3.0, ALL)],
    }


def report(recs=None):
    return build_report(PREREG, records() if recs is None else recs, TEXTS.__getitem__)


def kind(built, candidate, kind_name):
    return next(r for r in built["kinds"] if r["candidate"] == candidate and r["kind"] == kind_name)


class GradingTests(unittest.TestCase):
    def test_extractive_candidates_are_judged_on_the_median_of_all_runs(self):
        self.assertEqual(kind(report(), "shipped_rules", "prose")["retention"], 1.0)

    def test_the_digest_is_judged_by_the_reader_not_by_its_text(self):
        self.assertEqual(kind(report(), "digest_gpt_stand_in", "prose")["retention"], 1.0)

    def test_the_summarizer_is_judged_by_the_reader_not_by_its_text(self):
        self.assertEqual(kind(report(), "summarizer_control", "prose")["retention"], 0.0)

    def test_an_untouched_payload_is_reported_as_not_applicable(self):
        untouched = next(p for p in report()["payloads"] if p["candidate"] == "shipped_rules" and p["payload"] == "b")
        self.assertTrue(untouched["not_applicable"])

    def test_identity_keeps_everything_and_scores_zero(self):
        identity = kind(report(), "identity", "prose")
        self.assertEqual((identity["retention"], identity["score"]), (1.0, 0.0))


class KindAndScoreTests(unittest.TestCase):
    def test_each_kind_pools_only_its_own_payloads(self):
        built = report()
        self.assertEqual(kind(built, "identity", "prose")["original_chars"], len(TEXTS["a"]) + len(TEXTS["b"]))
        self.assertEqual(kind(built, "identity", "build")["original_chars"], len(TEXTS["c"]))

    def test_the_headline_score_is_at_the_pre_registered_budget(self):
        build = kind(report(), "shipped_rules", "build")
        c = 1 - len(KEPT) / len(TEXTS["c"])
        lat = 1.0 / (1.0 + 0.25)
        self.assertAlmostEqual(build["score"], round(2 * c * lat / (c + lat), 4))
        self.assertEqual(build["score"], build["score_at"]["1.0"])

    def test_scores_are_reported_at_every_budget(self):
        build = kind(report(), "shipped_rules", "build")
        self.assertEqual(sorted(build["score_at"]), ["0.5", "1.0", "2.0"])
        self.assertGreater(build["score_at"]["2.0"], build["score_at"]["0.5"])


class CountedViewTests(unittest.TestCase):
    def test_an_untouched_payload_counts_as_nothing_removed_in_the_counted_view(self):
        counted = next(r for r in report()["kinds_counted"] if r["candidate"] == "shipped_rules" and r["kind"] == "prose")
        self.assertFalse(counted["not_applicable"])
        self.assertEqual(counted["original_chars"], len(TEXTS["a"]) + len(TEXTS["b"]))
        self.assertEqual(counted["reduced_chars"], len(KEPT) + len(TEXTS["b"]))

    def test_the_pre_registered_view_still_leaves_it_out(self):
        self.assertEqual(kind(report(), "shipped_rules", "prose")["original_chars"], len(TEXTS["a"]))

    def test_a_kind_with_only_untouched_payloads_scores_zero_in_the_counted_view(self):
        recs = records()
        recs["shipped_rules"] = [r | {"output": TEXTS[r["payload"]]} if r["payload"] == "c" else r
                                 for r in recs["shipped_rules"]]
        counted = next(r for r in report(recs)["kinds_counted"]
                       if r["candidate"] == "shipped_rules" and r["kind"] == "build")
        self.assertEqual((counted["not_applicable"], counted["score"]), (False, 0.0))


class VoidTests(unittest.TestCase):
    def test_a_clean_run_stands(self):
        self.assertEqual(report()["void"], [])

    def test_identity_losing_an_answer_voids_the_run(self):
        recs = records()
        recs["identity"] = [r | {"replies": NONE} if r["payload"] == "a" else r for r in recs["identity"]]
        self.assertTrue(report(recs)["void"])

    def test_a_control_that_did_not_run_voids_the_run(self):
        recs = records()
        del recs["identity"]
        self.assertIn("identity", " ".join(report(recs)["void"]))

    def test_a_missing_run_voids_the_run(self):
        recs = records()
        recs["shipped_rules"] = [r for r in recs["shipped_rules"] if not (r["payload"] == "c" and r["run"] == 2)]
        self.assertIn("shipped_rules", " ".join(report(recs)["void"]))

    def test_a_reducer_that_threw_voids_the_run_instead_of_reading_as_untouched(self):
        recs = records()
        recs["shipped_rules"] = [r | {"failures": 1} if r["payload"] == "b" else r for r in recs["shipped_rules"]]
        self.assertIn("threw", " ".join(report(recs)["void"]))


class TableTests(unittest.TestCase):
    def test_the_digest_row_is_labelled_a_stand_in_and_columns_are_in_order(self):
        built = report()
        table = _table(built["kinds"], "kind")
        digest = next(line for line in table.splitlines() if line.startswith("| digest_gpt_stand_in"))
        rules = next(line for line in table.splitlines() if line.startswith("| shipped_rules | build"))
        self.assertIn("(stand-in)", digest)
        cells = [c.strip() for c in rules.strip("|").split("|")]
        row = kind(built, "shipped_rules", "build")
        self.assertEqual(cells[2:4] + [cells[5]], [str(row["score"]), str(row["compression"]), str(row["retention"])])
        self.assertTrue(kind(built, "digest_gpt_stand_in", "prose")["stand_in"])
        self.assertFalse(row["stand_in"])


class RunTests(unittest.TestCase):
    def test_haiku_reader_consumes_fresh_digests_from_the_selected_output_directory(self):
        with tempfile.TemporaryDirectory() as tmp, contextlib.chdir(tmp):
            out = Path("custom-results")
            fresh = '{"output": "fresh digest", "latency_s": 0.25}\n'

            def runner(command, **kwargs):
                if Path(command[1]).name == "claude_digest_runner.py":
                    kwargs["stdout"].write(fresh)
                else:
                    source = Path(command[command.index("--outputs") + 1])
                    if not source.is_absolute():
                        source = kwargs["cwd"] / source
                    kwargs["stdout"].write(source.read_text(encoding="utf-8"))
                return subprocess.CompletedProcess(command, 0)

            with patch("harness._environment", return_value={}), patch("harness.load", return_value=PREREG), \
                    patch("harness.subprocess.run", side_effect=runner):
                run(["haiku_digest"], out, 3)

            self.assertEqual((out / "haiku_digest.jsonl").read_text(encoding="utf-8"), fresh)
            self.assertEqual((out / "haiku_digest.digests.ndjson").read_text(encoding="utf-8"), fresh)


if __name__ == "__main__":
    unittest.main()
