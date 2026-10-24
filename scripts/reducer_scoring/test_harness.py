import contextlib
import subprocess
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from harness import _fields, _markdown, _table, build_report, run
from compression_score import Measurement  # noqa: E402  (harness puts scripts/ on the path)
from scoring import Row
import tokens  # noqa: E402

FACTS = "Dana Okonkwo merged PR 8842 after the incident. "
TEXTS = {"a": FACTS * 10, "b": FACTS * 20, "c": "build: " + FACTS * 8}
QUESTIONS = [{"q": "Who?", "a": "Dana Okonkwo"}, {"q": "PR?", "a": "PR 8842"}]
PREREG = {
    "budget_s": 1.0, "budget_sensitivity_s": [0.5, 2.0],
    "size_normalized_budget": {"budget_per_ktoken_s": 0.056, "budget_per_ktoken_sensitivity_s": [0.0064, 0.015, 0.056]}, "retention_floor": 1.0, "relaxed_retention_floor": 0.7, "runs": 3,
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


def runs(payload, outputs, latency=0.1, replies=None, under_minimum=None):
    if isinstance(outputs, str):
        outputs = [outputs] * 3
    return [{"payload": payload, "run": r, "output": o, "latency_s": latency,
             **({"replies": replies} if replies is not None else {}),
             **({"under_minimum": under_minimum} if under_minimum is not None else {})} for r, o in enumerate(outputs)]


def records():
    return {
        # Run 0 loses an answer on payload a; the median run keeps both.
        "shipped_rules": runs("a", ["Dana Okonkwo", KEPT, KEPT]) + runs("b", TEXTS["b"], under_minimum=True) + runs("c", KEPT, 0.25),
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

    def test_an_unchanged_payload_the_runner_did_not_mark_under_minimum_is_measured(self):
        recs = records()
        recs["shipped_rules"] = [r for r in recs["shipped_rules"] if r["payload"] != "b"] + runs("b", TEXTS["b"])
        row = next(p for p in report(recs)["payloads"] if p["candidate"] == "shipped_rules" and p["payload"] == "b")
        self.assertFalse(row["not_applicable"])
        self.assertEqual(row["compression"], 0.0)

    def test_a_kinds_size_score_is_the_equal_weight_mean_of_its_payloads(self):
        recs = records()
        recs["shipped_rules"] = runs("a", KEPT, 0.01) + runs("b", KEPT, 3.0) + runs("c", KEPT, 0.25)
        built = report(recs)
        scores = [p["size_score"] for p in built["payloads"] if p["candidate"] == "shipped_rules" and p["kind"] == "prose"]
        self.assertEqual(len(scores), 2)
        self.assertAlmostEqual(kind(built, "shipped_rules", "prose")["size_score"], sum(scores) / 2, places=4)

    def test_the_size_budget_comes_from_the_preregistration(self):
        tight = {**PREREG, "size_normalized_budget": {"budget_per_ktoken_s": 0.0001, "budget_per_ktoken_sensitivity_s": [0.0001]}}
        loose = build_report(PREREG, records(), TEXTS.__getitem__)
        strict = build_report(tight, records(), TEXTS.__getitem__)
        pick = lambda b: next(p for p in b["payloads"] if p["candidate"] == "shipped_rules" and p["payload"] == "c")
        self.assertLess(pick(strict)["size_score"], pick(loose)["size_score"])

    def test_an_untouched_payload_is_reported_as_not_applicable(self):
        untouched = next(p for p in report()["payloads"] if p["candidate"] == "shipped_rules" and p["payload"] == "b")
        self.assertTrue(untouched["not_applicable"])

    def test_identity_keeps_everything_and_scores_zero(self):
        identity = kind(report(), "identity", "prose")
        self.assertEqual((identity["retention"], identity["score"]), (1.0, 0.0))


class TokenSizeTests(unittest.TestCase):
    def test_rows_carry_token_sizes_and_no_character_sizes(self):
        built = report()
        for row in built["payloads"] + built["kinds"] + built["kinds_counted"]:
            if not row["not_applicable"]:
                with self.subTest(candidate=row["candidate"]):
                    self.assertIn("original_tokens", row)
                    self.assertIn("reduced_tokens", row)
                    self.assertFalse([k for k in row if k.endswith("_chars")])

    def test_the_report_names_how_tokens_were_estimated(self):
        self.assertEqual(report()["token_method"], tokens.METHOD)

    def test_the_markdown_report_names_how_tokens_were_estimated(self):
        self.assertIn(f"sizes: {tokens.METHOD}", _markdown(report() | {"environment": {}}))

    def test_saved_added_chars_are_converted_to_tokens(self):
        recs = {"shipped_rules": [r | {"added_chars": 300} for r in runs("c", KEPT)]}
        counted = next(r for r in report(recs)["payloads"] if r["payload"] == "c")
        self.assertEqual(counted["reduced_tokens"], tokens.estimate(KEPT) + tokens.from_chars(300))

    def test_added_tokens_from_a_new_run_are_counted_as_recorded(self):
        recs = {"shipped_rules": [r | {"added_tokens": 100} for r in runs("c", KEPT)]}
        counted = next(r for r in report(recs)["payloads"] if r["payload"] == "c")
        self.assertEqual(counted["reduced_tokens"], tokens.estimate(KEPT) + 100)


class KindAndScoreTests(unittest.TestCase):
    def test_each_kind_pools_only_its_own_payloads(self):
        built = report()
        self.assertEqual(kind(built, "identity", "prose")["original_tokens"], tokens.estimate(TEXTS["a"]) + tokens.estimate(TEXTS["b"]))
        self.assertEqual(kind(built, "identity", "build")["original_tokens"], tokens.estimate(TEXTS["c"]))

    def test_the_headline_score_is_at_the_pre_registered_budget(self):
        build = kind(report(), "shipped_rules", "build")
        c = 1 - tokens.estimate(KEPT) / tokens.estimate(TEXTS["c"])
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
        self.assertEqual(counted["original_tokens"], tokens.estimate(TEXTS["a"]) + tokens.estimate(TEXTS["b"]))
        self.assertEqual(counted["reduced_tokens"], tokens.estimate(KEPT) + tokens.estimate(TEXTS["b"]))

    def test_the_pre_registered_view_still_leaves_it_out(self):
        self.assertEqual(kind(report(), "shipped_rules", "prose")["original_tokens"], tokens.estimate(TEXTS["a"]))

    def test_a_kind_with_only_untouched_payloads_scores_zero_in_the_counted_view(self):
        recs = records()
        recs["shipped_rules"] = [r | {"output": TEXTS[r["payload"]]} if r["payload"] == "c" else r
                                 for r in recs["shipped_rules"]]
        counted = next(r for r in report(recs)["kinds_counted"]
                       if r["candidate"] == "shipped_rules" and r["kind"] == "build")
        self.assertEqual((counted["not_applicable"], counted["score"]), (False, 0.0))


class RelaxedViewTests(unittest.TestCase):
    def fields(self, kept, total=10):
        return _fields("haiku_digest", Row(Measurement(1000, 600, 1.0, kept, total), False), PREREG)

    def test_a_small_loss_scores_its_harmonic_mean_scaled_by_retention(self):
        c, lat = 0.4, 0.5
        row = self.fields(8)
        self.assertEqual(row["score"], 0.0)
        self.assertEqual(row["score_relaxed"], round(2 * c * lat / (c + lat) * 0.8, 4))

    def test_a_loss_at_the_relaxed_floor_still_scores(self):
        self.assertGreater(self.fields(7)["score_relaxed"], 0.0)

    def test_a_loss_under_the_relaxed_floor_scores_zero(self):
        self.assertEqual(self.fields(6)["score_relaxed"], 0.0)

    def test_without_loss_the_relaxed_score_is_the_headline_score(self):
        row = self.fields(10)
        self.assertEqual(row["score_relaxed"], row["score"])

    def test_the_relaxed_view_grades_the_fact_not_the_wording(self):
        questions = [{"q": "p99?", "a": "4.2 seconds"}, {"q": "failed?", "a": "failed: 7"},
                     {"q": "Who?", "a": "Dana Okonkwo"}, {"q": "PR?", "a": "PR 8842"}]
        prereg = PREREG | {"payloads": {"a": {"kind": "prose", "questions": questions}}}
        replies = [["4.2s"] * 3, ["7 failed"] * 3, ["Dana Okonkwo"] * 3, ["PR 8842"] * 3]
        recs = {"digest_gpt_stand_in": runs("a", "short", 0.0, replies)}
        row = next(r for r in build_report(prereg, recs, TEXTS.__getitem__)["kinds"])
        self.assertEqual((row["answers"], row["answers_relaxed"]), ("2/4", "4/4"))
        self.assertEqual(row["score"], 0.0)
        self.assertGreater(row["score_relaxed"], 0.0)

    def judged_report(self, decoys_accepted):
        recs = records()
        recs["haiku_digest"] = [r for p in TEXTS for r in runs(p, "short", 0.0, [["Dana Okonkwo"] * 3, ["?"] * 3])]
        judged = {("haiku_digest", p, r): frozenset({"PR 8842"}) for p in TEXTS for r in range(3)}
        return build_report(PREREG, recs, TEXTS.__getitem__, (judged, (decoys_accepted, 20)))

    def test_a_judged_answer_counts_in_the_relaxed_view_only(self):
        row = kind(self.judged_report(0), "haiku_digest", "prose")
        self.assertEqual((row["answers"], row["answers_relaxed"]), ("2/4", "4/4"))

    def test_a_judge_that_accepts_more_than_a_tenth_of_its_decoys_is_not_used(self):
        built = self.judged_report(3)
        self.assertEqual(kind(built, "haiku_digest", "prose")["answers_relaxed"], "2/4")
        self.assertIn("decoy", " ".join(built["judge"]["void"]))

    def test_a_judge_at_a_tenth_of_its_decoys_is_used(self):
        built = self.judged_report(2)
        self.assertEqual((built["judge"]["void"], kind(built, "haiku_digest", "prose")["answers_relaxed"]), ([], "4/4"))

    def test_the_table_shows_the_relaxed_score_last(self):
        built = report()
        rules = next(line for line in _table(built["kinds"], "kind").splitlines()
                     if line.startswith("| shipped_rules | build"))
        self.assertEqual(rules.strip("|").split("|")[-1].strip(),
                         str(kind(built, "shipped_rules", "build")["score_relaxed"]))


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
        self.assertEqual([cells[2], cells[4], cells[6], cells[8]],
                         [str(row["size_score"]), str(row["compression"]), str(row["retention"]), str(row["score"])])
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

    def test_each_haiku_candidate_names_itself_to_the_digest_runner_and_keeps_its_own_digests(self):
        with tempfile.TemporaryDirectory() as tmp, contextlib.chdir(tmp):
            out = Path("results")
            seen = []

            def runner(command, **kwargs):
                if Path(command[1]).name == "claude_digest_runner.py":
                    seen.append(command[2])
                    kwargs["stdout"].write('{"output": "d", "latency_s": 1}\n')
                else:
                    self.assertEqual(Path(command[command.index("--outputs") + 1]).name,
                                     "haiku_digest_checked.digests.ndjson")
                return subprocess.CompletedProcess(command, 0)

            with patch("harness._environment", return_value={}), patch("harness.load", return_value=PREREG), \
                    patch("harness.subprocess.run", side_effect=runner):
                run(["haiku_digest_checked"], out, 3)

            self.assertEqual(seen, ["haiku_digest_checked"])
            self.assertTrue((out / "haiku_digest_checked.digests.ndjson").exists())


if __name__ == "__main__":
    unittest.main()
