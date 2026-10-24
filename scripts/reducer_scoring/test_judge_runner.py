import unittest

from judge_runner import accepted, decoy, judge_prompt, lost, verdicts

QUESTIONS = [{"q": "p99?", "a": "4.2 seconds"}, {"q": "Who?", "a": "Dana Okonkwo"}]
PREREG = {"gates": {"extractive": {"candidates": ["shipped_rules"]}},
          "payloads": {"a": {"kind": "prose", "questions": QUESTIONS}}}


class DecoyTests(unittest.TestCase):
    def test_the_last_number_is_changed(self):
        self.assertEqual(decoy("(18,83)"), "(18,84)")
        self.assertEqual(decoy("failed: 7"), "failed: 8")
        self.assertEqual(decoy("4.2 seconds"), "4.3 seconds")

    def test_leading_zeros_are_kept(self):
        self.assertEqual(decoy("00:00:00.42"), "00:00:00.43")
        self.assertEqual(decoy("line=09"), "line=10")
        self.assertEqual(decoy("build 007"), "build 008")

    def test_an_answer_without_a_number_has_no_decoy(self):
        self.assertIsNone(decoy("Dana Okonkwo"))


class LostTests(unittest.TestCase):
    def test_an_extractive_answer_missing_from_the_output_is_judged(self):
        records = {"shipped_rules": [{"payload": "a", "run": 0, "output": "p99 4.2s, nobody named"}]}
        self.assertEqual(lost(PREREG, records), {("shipped_rules", "a", 0): [QUESTIONS[1]]})

    def test_a_reader_answer_missed_twice_in_three_is_judged(self):
        replies = [["4.2s", "4.2s", "4.2s"], ["Dana Okonkwo", "UNKNOWN", "UNKNOWN"]]
        records = {"haiku_digest": [{"payload": "a", "run": 1, "output": "d", "replies": replies}]}
        self.assertEqual(lost(PREREG, records), {("haiku_digest", "a", 1): [QUESTIONS[1]]})

    def test_nothing_lost_needs_no_judge(self):
        replies = [["4.2 s"] * 3, ["Dana Okonkwo"] * 3]
        records = {"identity": [{"payload": "a", "run": 0, "output": "d", "replies": replies}]}
        self.assertEqual(lost(PREREG, records), {})


class PromptTests(unittest.TestCase):
    def test_the_prompt_gives_the_text_each_question_and_its_proposed_answer_but_not_which_is_a_decoy(self):
        items = [{"q": "p99?", "a": "4.3 seconds", "decoy": True}, {"q": "Who?", "a": "Dana Okonkwo", "decoy": False}]
        prompt = judge_prompt("the text", items)
        for part in ("the text", "p99?", "4.3 seconds", "Who?", "Dana Okonkwo"):
            self.assertIn(part, prompt)
        self.assertNotIn("decoy", prompt.lower())


class VerdictTests(unittest.TestCase):
    def test_yes_and_no_are_read_from_a_json_array(self):
        self.assertEqual(verdicts('["yes", "No"]', 2), [True, False])

    def test_an_unreadable_reply_accepts_nothing(self):
        self.assertEqual(verdicts("yes, yes", 2), [False, False])


class AcceptedTests(unittest.TestCase):
    def line(self, votes, is_decoy=False):
        return {"candidate": "c", "payload": "a", "run": 0,
                "items": [{"q": "p99?", "a": "4.2 seconds", "decoy": is_decoy}], "verdicts": [votes]}

    def test_two_yes_in_three_accept_the_answer(self):
        judged, _ = accepted([self.line([True, False, True])])
        self.assertEqual(judged, {("c", "a", 0): frozenset({"4.2 seconds"})})

    def test_one_yes_in_three_does_not(self):
        judged, _ = accepted([self.line([True, False, False])])
        self.assertEqual(judged, {})

    def test_an_accepted_decoy_is_counted_and_never_kept(self):
        judged, decoys = accepted([self.line([True, True, True], is_decoy=True), self.line([False] * 3, True)])
        self.assertEqual((judged, decoys), ({}, (1, 2)))


if __name__ == "__main__":
    unittest.main()
