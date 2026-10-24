import json
import unittest

from claude_reader_runner import QuotaError, check_markers, is_quota_error, parse_reader_result, read_once
from retrieval import RETRIEVAL_SYSTEM
from pi_runner import READER_SYSTEM

PREVIEW = 'head\n[rtk-proxy: compressed 90 tokens — call rtk_retrieve("abcdef0123456789") to get the full content]'
QUESTIONS = [{"q": "Who?", "a": "Dana"}]


def fake(*replies):
    """An ask that answers in order and records each (system, prompt)."""
    calls, queue = [], list(replies)

    def ask(system, prompt):
        calls.append((system, prompt))
        return queue.pop(0), 0.5

    return ask, calls


class ParseResultTests(unittest.TestCase):
    def ok(self, **extra):
        return json.dumps({"result": '["a"]', "is_error": False, "duration_api_ms": 1500,
                           "modelUsage": {"claude-opus-5-5": {}}} | extra)

    def test_the_reply_and_the_models_own_time(self):
        self.assertEqual(parse_reader_result(self.ok(), "claude-opus-5-5"), ('["a"]', 1.5))

    def test_another_model_is_refused(self):
        with self.assertRaises(RuntimeError):
            parse_reader_result(self.ok(modelUsage={"claude-haiku-5-5": {}}), "claude-opus-5-5")

    def test_a_quota_error_is_told_apart_from_other_failures(self):
        raw = json.dumps({"result": "Credit balance is too low", "is_error": True})
        with self.assertRaises(QuotaError):
            parse_reader_result(raw, "claude-opus-5-5")
        raw = json.dumps({"result": "boom", "is_error": True})
        with self.assertRaises(RuntimeError) as caught:
            parse_reader_result(raw, "claude-opus-5-5")
        self.assertNotIsInstance(caught.exception, QuotaError)

    def test_quota_wording(self):
        for text in ("rate limit exceeded", "You've hit your usage limit", "429 Too Many Requests", "credit balance too low"):
            self.assertTrue(is_quota_error(text), text)
        self.assertFalse(is_quota_error("syntax error"))


class CheckMarkersTests(unittest.TestCase):
    def rec(self, payload, run, ident):
        return {"payload": payload, "run": run, "output": f'x rtk_retrieve("{ident}") y' if ident else "x"}

    def test_each_payload_has_one_stable_unique_id_or_none(self):
        ids = check_markers([self.rec("a", 0, "aa11"), self.rec("a", 1, "aa11"), self.rec("b", 0, None)])
        self.assertEqual(ids, {"a": "aa11", "b": None})

    def test_an_id_that_changes_between_runs_is_refused(self):
        with self.assertRaises(RuntimeError):
            check_markers([self.rec("a", 0, "aa11"), self.rec("a", 1, "bb22")])

    def test_an_id_shared_by_two_payloads_is_refused(self):
        with self.assertRaises(RuntimeError):
            check_markers([self.rec("a", 0, "aa11"), self.rec("b", 0, "aa11")])


class ReadOnceTests(unittest.TestCase):
    def test_without_retrieval_it_asks_once_with_the_plain_reader(self):
        ask, calls = fake('["Dana"]')
        result = read_once(ask, PREVIEW, "ORIGINAL", QUESTIONS, retrieval=False)
        self.assertEqual((result["raw"], result["retrieved"], len(calls)), ('["Dana"]', False, 1))
        self.assertEqual(calls[0][0], READER_SYSTEM)

    def test_an_answer_needs_no_retrieval(self):
        ask, calls = fake('["Dana"]')
        result = read_once(ask, PREVIEW, "ORIGINAL", QUESTIONS, retrieval=True)
        self.assertEqual((result["retrieved"], len(calls), calls[0][0]), (False, 1, RETRIEVAL_SYSTEM))

    def test_a_valid_request_gets_the_original_and_one_more_ask(self):
        ask, calls = fake("RETRIEVE abcdef0123456789", '["Dana"]')
        result = read_once(ask, PREVIEW, "ORIGINAL BODY", QUESTIONS, retrieval=True)
        self.assertEqual((result["raw"], result["retrieved"], result["id"]), ('["Dana"]', True, "abcdef0123456789"))
        self.assertIn("ORIGINAL BODY", calls[1][1])
        self.assertEqual(calls[1][0], READER_SYSTEM)
        self.assertEqual(result["first_wall_s"], 0.5)
        self.assertEqual(result["wall_s"], [0.5, 0.5])

    def test_a_second_request_is_not_served(self):
        ask, calls = fake("RETRIEVE abcdef0123456789", "RETRIEVE abcdef0123456789")
        result = read_once(ask, PREVIEW, "ORIGINAL", QUESTIONS, retrieval=True)
        self.assertEqual((len(calls), result["retrieved"], result["raw"]), (2, True, "RETRIEVE abcdef0123456789"))

    def test_an_id_not_in_the_text_is_no_answer_and_fetches_nothing(self):
        ask, calls = fake("RETRIEVE 0000000000000000")
        result = read_once(ask, PREVIEW, "ORIGINAL", QUESTIONS, retrieval=True)
        self.assertEqual((len(calls), result["retrieved"]), (1, False))


if __name__ == "__main__":
    unittest.main()
