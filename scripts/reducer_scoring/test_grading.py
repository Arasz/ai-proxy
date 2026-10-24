import unittest

from grading import answered, normalized, same_fact


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


class SameFactTests(unittest.TestCase):
    def test_whatever_counts_word_for_word_still_counts(self):
        for reply, expected in (("0 Warning(s) 2 Error(s)", "2 Error(s)"), ("failed: 7, succeeded: 224", "failed: 7"),
                                ("Total: 231 Failed: 7 Succeeded: 224", "failed: 7"),
                                ("Total: 231 Failed: 7 Succeeded: 224", "succeeded: 224"),
                                ("took 4.2 seconds", "4.2 seconds"), ("Foo.cs(18,83)", "(18,83)")):
            self.assertTrue(same_fact(reply, expected), reply)


    def test_a_unit_written_short_is_the_same_fact(self):
        for reply in ("4.2s", "4.2 s", "4.2 sec", "p99 was 4.2 seconds"):
            self.assertTrue(same_fact(reply, "4.2 seconds"), reply)

    def test_milliseconds_written_long_are_the_same_fact(self):
        self.assertTrue(same_fact("512 milliseconds", "512ms"))

    def test_a_count_written_label_first_or_last_is_the_same_fact(self):
        for reply in ("7 failed", "failed 7", "Failed: 7"):
            self.assertTrue(same_fact(reply, "failed: 7"), reply)

    def test_passed_and_succeeded_are_the_same_count(self):
        self.assertTrue(same_fact("224 passed", "succeeded: 224"))

    def test_error_s_and_errors_are_the_same_count(self):
        self.assertTrue(same_fact("2 errors", "2 Error(s)"))

    def test_a_count_already_number_first_is_not_taken_by_the_next_number(self):
        self.assertTrue(same_fact("0 Warning(s) 2 Error(s) Time Elapsed", "2 Error(s)"))

    def test_spacing_inside_a_position_does_not_matter(self):
        self.assertTrue(same_fact("(18, 83)", "(18,83)"))

    def test_a_different_number_is_a_different_fact(self):
        for reply, expected in (("4.3s", "4.2 seconds"), ("6 failed", "failed: 7"), ("(33,16)", "(18,83)"), ("(18, 84)", "(18,83)"),
                                ("4.2ms", "4.2 seconds"), ("224 failed", "succeeded: 224")):
            self.assertFalse(same_fact(reply, expected), reply)

    def test_a_shortened_name_is_not_the_same_fact(self):
        self.assertFalse(same_fact("Unknown", "Dialect.Unknown"))
        self.assertFalse(same_fact("MacProxy", "System.Net.Http.MacProxy"))

    def test_unknown_is_not_the_same_fact(self):
        self.assertFalse(same_fact("UNKNOWN", "2 Error(s)"))


if __name__ == "__main__":
    unittest.main()
