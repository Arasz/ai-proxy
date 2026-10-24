import unittest

from retrieval import RETRIEVAL_SYSTEM, marker_ids, parse_retrieve, retrieval_counts, retrieval_prompt
from pi_runner import READER_SYSTEM

PREVIEW = 'head\n[… 5 lines elided …]\n[rtk-proxy: compressed 1555 tokens — call rtk_retrieve("e5ad32934d73a66c") to get the full content]'


class MarkerTests(unittest.TestCase):
    def test_the_id_comes_from_the_marker(self):
        self.assertEqual(marker_ids(PREVIEW), ["e5ad32934d73a66c"])

    def test_text_without_a_marker_has_no_ids(self):
        self.assertEqual(marker_ids("plain output, rtk_retrieve mentioned"), [])


class ParseRetrieveTests(unittest.TestCase):
    def test_an_id_in_the_text_is_a_valid_request(self):
        self.assertEqual(parse_retrieve("RETRIEVE e5ad32934d73a66c", PREVIEW), "e5ad32934d73a66c")

    def test_surrounding_whitespace_is_ignored(self):
        self.assertEqual(parse_retrieve("  RETRIEVE e5ad32934d73a66c\n", PREVIEW), "e5ad32934d73a66c")

    def test_an_id_not_in_the_text_is_no_request(self):
        self.assertIsNone(parse_retrieve("RETRIEVE 0000000000000000", PREVIEW))

    def test_any_other_reply_is_no_request(self):
        for reply in ('["a"]', "I would RETRIEVE e5ad32934d73a66c", "RETRIEVE e5ad32934d73a66c now", ""):
            self.assertIsNone(parse_retrieve(reply, PREVIEW), reply)

    def test_text_without_a_marker_allows_no_request(self):
        self.assertIsNone(parse_retrieve("RETRIEVE e5ad32934d73a66c", "no marker"))


class PromptTests(unittest.TestCase):
    def test_the_retrieval_system_keeps_the_reader_system_and_adds_the_reply(self):
        self.assertTrue(RETRIEVAL_SYSTEM.startswith(READER_SYSTEM))
        self.assertIn("RETRIEVE <id>", RETRIEVAL_SYSTEM)

    def test_the_second_prompt_has_text_original_and_questions_but_no_answers(self):
        prompt = retrieval_prompt(PREVIEW, "ORIGINAL BODY", [{"q": "Who?", "a": "Dana"}])
        for part in (PREVIEW, "ORIGINAL BODY", "1. Who?"):
            self.assertIn(part, prompt)
        self.assertNotIn("Dana", prompt)


class RetrievalCountsTests(unittest.TestCase):
    def test_counts_retrieving_calls_over_all_calls(self):
        records = [{"retrievals": [True, False, False]}, {"retrievals": [True, True, False]}]
        self.assertEqual(retrieval_counts(records), (3, 6))

    def test_no_records_count_nothing(self):
        self.assertEqual(retrieval_counts([]), (0, 0))


if __name__ == "__main__":
    unittest.main()
