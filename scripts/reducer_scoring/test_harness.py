import unittest

from harness import build_report

TEXT = "Dana Okonkwo merged PR 8842 after the incident. " * 10
PREREG = {
    "budget_s": 1.0, "budget_sensitivity_s": [0.5, 2.0], "retention_floor": 1.0,
    "gates": {"extractive": {"candidates": ["shipped_rules"]},
              "abstractive": {"candidates": ["haiku_digest", "summarizer_control", "identity"]}},
    "payloads": {
        "a": {"kind": "prose", "questions": [{"q": "Who?", "a": "Dana Okonkwo"}, {"q": "PR?", "a": "PR 8842"}]},
        "b": {"kind": "prose", "questions": [{"q": "Who?", "a": "Dana Okonkwo"}, {"q": "PR?", "a": "PR 8842"}]},
    },
}
ALL = [["Dana Okonkwo"] * 3, ["PR 8842"] * 3]
NONE = [["UNKNOWN"] * 3, ["UNKNOWN"] * 3]


def runs(payload, output, latency=0.1, replies=None):
    return [{"payload": payload, "run": r, "output": output, "latency_s": latency,
             **({"replies": replies} if replies is not None else {})} for r in range(3)]


def records(identity=ALL, rules_output="Dana Okonkwo PR 8842"):
    return {
        "shipped_rules": runs("a", rules_output) + runs("b", TEXT),
        "identity": runs("a", TEXT, 0.0, identity) + runs("b", TEXT, 0.0, ALL),
        "summarizer_control": runs("a", "short", 2.0, NONE) + runs("b", "short", 2.0, NONE),
        "haiku_digest": runs("a", "Dana Okonkwo, PR 8842", 3.0, ALL) + runs("b", "Dana Okonkwo, PR 8842", 3.0, ALL),
    }


def row(report, candidate, kind):
    return next(r for r in report["kinds"] if r["candidate"] == candidate and r["kind"] == kind)


def load_text(name):
    return TEXT


class BuildReportTests(unittest.TestCase):
    def test_extractive_candidates_are_judged_on_their_text(self):
        report = build_report(PREREG, records(rules_output="Dana Okonkwo"), load_text)
        self.assertEqual(row(report, "shipped_rules", "prose")["retention"], 0.5)
        self.assertEqual(row(report, "shipped_rules", "prose")["score"], 0.0)

    def test_an_untouched_payload_is_reported_as_not_applicable(self):
        report = build_report(PREREG, records(), load_text)
        untouched = next(p for p in report["payloads"] if p["candidate"] == "shipped_rules" and p["payload"] == "b")
        self.assertTrue(untouched["not_applicable"])

    def test_identity_keeps_everything_and_scores_zero(self):
        identity = row(build_report(PREREG, records(), load_text), "identity", "prose")
        self.assertEqual((identity["retention"], identity["score"]), (1.0, 0.0))

    def test_scores_are_reported_at_every_budget(self):
        rules = row(build_report(PREREG, records(), load_text), "shipped_rules", "prose")
        self.assertEqual(sorted(rules["score_at"]), ["0.5", "1.0", "2.0"])
        self.assertGreater(rules["score_at"]["2.0"], rules["score_at"]["0.5"])

    def test_a_clean_run_stands(self):
        self.assertEqual(build_report(PREREG, records(), load_text)["void"], [])

    def test_identity_losing_an_answer_voids_the_run(self):
        self.assertTrue(build_report(PREREG, records(identity=NONE), load_text)["void"])

    def test_the_digest_is_labelled_a_stand_in(self):
        digest = row(build_report(PREREG, records(), load_text), "haiku_digest", "prose")
        self.assertTrue(digest["stand_in"])
        self.assertFalse(row(build_report(PREREG, records(), load_text), "shipped_rules", "prose")["stand_in"])


if __name__ == "__main__":
    unittest.main()
