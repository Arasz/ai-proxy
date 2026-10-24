import unittest

from pi_runner import parse_answers, qa_prompt

QUESTIONS = [{"q": "Who was on call?", "a": "Dana Okonkwo"}, {"q": "Which PR?", "a": "PR 8842"}]


class QaPromptTests(unittest.TestCase):
    def test_every_question_is_asked_and_no_answer_is_given(self):
        prompt = qa_prompt("the text", QUESTIONS)
        self.assertIn("Who was on call?", prompt)
        self.assertIn("Which PR?", prompt)
        self.assertNotIn("Dana Okonkwo", prompt)
        self.assertNotIn("PR 8842", prompt)


class ParseAnswersTests(unittest.TestCase):
    def test_a_json_array_gives_one_answer_per_question(self):
        self.assertEqual(parse_answers('["Dana Okonkwo", "UNKNOWN"]', 2), ["Dana Okonkwo", "UNKNOWN"])

    def test_a_fenced_array_is_read(self):
        self.assertEqual(parse_answers('```json\n["a", "b"]\n```', 2), ["a", "b"])

    def test_a_reply_with_the_wrong_count_answers_nothing(self):
        self.assertEqual(parse_answers('["a"]', 2), ["", ""])

    def test_prose_answers_nothing_even_when_it_names_an_answer(self):
        self.assertEqual(parse_answers("Dana Okonkwo, PR 8842", 2), ["", ""])


if __name__ == "__main__":
    unittest.main()
