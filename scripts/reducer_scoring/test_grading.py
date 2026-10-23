import unittest

from grading import answered, normalized


class NormalizedTests(unittest.TestCase):
    def test_case_and_runs_of_whitespace_do_not_matter(self):
        self.assertEqual(normalized("  Dana \n Okonkwo "), "dana okonkwo")


class AnsweredTests(unittest.TestCase):
    def test_reply_containing_the_expected_answer_counts(self):
        self.assertTrue(answered("The pool lives in /srv/app/db/pool.py.", "/srv/app/db/pool.py"))

    def test_case_and_spacing_differences_still_count(self):
        self.assertTrue(answered("dana   OKONKWO", "Dana Okonkwo"))

    def test_unknown_does_not_count(self):
        self.assertFalse(answered("UNKNOWN", "PR 8842"))

    def test_a_different_value_does_not_count(self):
        self.assertFalse(answered("311 waiters pending", "402 waiters pending"))

    def test_a_partial_number_does_not_count(self):
        self.assertFalse(answered("12,48", "12,480"))

    def test_the_answer_inside_a_longer_number_does_not_count(self):
        self.assertFalse(answered("about 21,2045 users", "1,204"))

    def test_the_answer_next_to_punctuation_counts(self):
        self.assertTrue(answered("(1,204).", "1,204"))

    def test_an_answer_starting_with_punctuation_may_follow_a_word(self):
        self.assertTrue(answered("LogRules.cs(33,16): error", "(33,16)"))

    def test_an_empty_expected_answer_is_refused(self):
        with self.assertRaises(ValueError):
            answered("anything", "  ")


if __name__ == "__main__":
    unittest.main()
