import unittest

from grading import answered
from preregistration import load, payload_text


class PreregistrationTests(unittest.TestCase):
    """Checks the committed preregistration.json, not a fixture."""

    @classmethod
    def setUpClass(cls):
        cls.prereg = load()

    def test_every_payload_has_its_text_file(self):
        for name in self.prereg["payloads"]:
            with self.subTest(payload=name):
                self.assertTrue(payload_text(name))

    def test_every_payload_has_at_least_four_questions(self):
        for name, payload in self.prereg["payloads"].items():
            with self.subTest(payload=name):
                self.assertGreaterEqual(len(payload["questions"]), 4)

    def test_every_expected_answer_occurs_in_its_payload(self):
        for name, payload in self.prereg["payloads"].items():
            text = payload_text(name)
            for question in payload["questions"]:
                with self.subTest(payload=name, answer=question["a"]):
                    self.assertTrue(answered(text, question["a"]))

    def test_no_question_gives_its_own_answer_away(self):
        for name, payload in self.prereg["payloads"].items():
            for question in payload["questions"]:
                with self.subTest(payload=name, question=question["q"]):
                    self.assertFalse(answered(question["q"], question["a"]))

    def test_every_kind_has_at_least_two_payloads_except_listing(self):
        kinds = {}
        for payload in self.prereg["payloads"].values():
            kinds[payload["kind"]] = kinds.get(payload["kind"], 0) + 1
        for kind, count in kinds.items():
            if kind != "listing":
                with self.subTest(kind=kind):
                    self.assertGreaterEqual(count, 2)

    def test_the_size_normalized_budget_is_per_thousand_tokens(self):
        budget = self.prereg["size_normalized_budget"]
        sensitivity = budget["budget_per_ktoken_sensitivity_s"]
        self.assertEqual(len(sensitivity), 3)
        self.assertEqual(sensitivity, sorted(sensitivity))
        self.assertEqual(budget["budget_per_ktoken_s"], sensitivity[1])
        self.assertEqual(budget["token_method"], "scripts/tokens_calibration.json")

    def test_no_budget_is_stated_per_thousand_characters(self):
        self.assertEqual([k for k in self.prereg["size_normalized_budget"] if "kchar" in k], [])

    def test_the_floor_is_the_owner_floor(self):
        self.assertEqual(self.prereg["retention_floor"], 1.0)


if __name__ == "__main__":
    unittest.main()
