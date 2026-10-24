import unittest

from scoring import (
    QaRun,
    ReducerRun,
    extractive_row,
    kind_row,
    qa_kept,
    qa_row,
    void_reasons,
)

QUESTIONS = [{"q": "Who?", "a": "Dana Okonkwo"}, {"q": "Which PR?", "a": "PR 8842"}]
ORIGINAL = "Dana Okonkwo merged PR 8842 after the incident." * 4


class QaKeptTests(unittest.TestCase):
    def test_one_miss_in_three_keeps_the_question(self):
        replies = [["Dana Okonkwo", "UNKNOWN", "dana okonkwo"], ["PR 8842"] * 3]
        self.assertEqual(qa_kept(replies, QUESTIONS), 2)

    def test_two_misses_in_three_lose_the_question(self):
        replies = [["Dana Okonkwo", "UNKNOWN", "Bob"], ["PR 8842"] * 3]
        self.assertEqual(qa_kept(replies, QUESTIONS), 1)

    def test_replies_must_cover_every_question(self):
        with self.assertRaises(ValueError):
            qa_kept([["Dana Okonkwo"] * 3], QUESTIONS)


class ExtractiveRowTests(unittest.TestCase):
    def test_an_answer_cut_from_the_output_is_lost(self):
        runs = [ReducerRun("Dana Okonkwo merged it.", 0.1)] * 3
        row = extractive_row(ORIGINAL, QUESTIONS, runs)
        self.assertEqual((row.measurement.answers_kept, row.measurement.answers_total), (1, 2))

    def test_output_equal_to_input_is_not_applicable(self):
        row = extractive_row(ORIGINAL, QUESTIONS, [ReducerRun(ORIGINAL, 0.01)] * 3)
        self.assertTrue(row.not_applicable)

    def test_one_untouched_run_among_reduced_ones_is_still_measured(self):
        runs = [ReducerRun(ORIGINAL, 0.1), ReducerRun("Dana Okonkwo PR 8842", 0.1),
                ReducerRun("Dana Okonkwo PR 8842", 0.1)]
        self.assertFalse(extractive_row(ORIGINAL, QUESTIONS, runs).not_applicable)

    def test_latency_and_length_are_medians_over_runs(self):
        runs = [ReducerRun("Dana Okonkwo PR 8842", 0.9),
                ReducerRun("Dana Okonkwo PR 8842 xx", 0.1),
                ReducerRun("Dana Okonkwo PR 8842 xxxxxx", 0.2)]
        row = extractive_row(ORIGINAL, QUESTIONS, runs)
        self.assertEqual(row.measurement.latency_s, 0.2)
        self.assertEqual(row.measurement.reduced_chars, len("Dana Okonkwo PR 8842 xx"))

    def test_runs_that_disagree_on_retention_take_the_median_run(self):
        runs = [ReducerRun("Dana Okonkwo PR 8842", 0.1),
                ReducerRun("Dana Okonkwo", 0.1),
                ReducerRun("Dana Okonkwo", 0.1)]
        self.assertEqual(extractive_row(ORIGINAL, QUESTIONS, runs).measurement.answers_kept, 1)


class QaRowTests(unittest.TestCase):
    def test_retention_comes_from_the_reader_not_from_the_text(self):
        # The digest paraphrased the name away, but the reader still answered from it.
        run = QaRun("Okonkwo (Dana) merged PR 8842.", 2.0,
                    [["Dana Okonkwo"] * 3, ["PR 8842"] * 3])
        row = qa_row(ORIGINAL, QUESTIONS, [run] * 3)
        self.assertEqual(row.measurement.answers_kept, 2)
        self.assertEqual(row.measurement.latency_s, 2.0)


class KindRowTests(unittest.TestCase):
    def test_one_failing_payload_fails_the_kind(self):
        good = extractive_row(ORIGINAL, QUESTIONS, [ReducerRun("Dana Okonkwo PR 8842", 0.1)] * 3)
        bad = extractive_row(ORIGINAL, QUESTIONS, [ReducerRun("Dana Okonkwo", 0.1)] * 3)
        pooled = kind_row([good, bad])
        self.assertLess(pooled.measurement.answers_kept, pooled.measurement.answers_total)

    def test_not_applicable_payloads_are_left_out(self):
        good = extractive_row(ORIGINAL, QUESTIONS, [ReducerRun("Dana Okonkwo PR 8842", 0.1)] * 3)
        untouched = extractive_row(ORIGINAL, QUESTIONS, [ReducerRun(ORIGINAL, 0.0)] * 3)
        self.assertEqual(kind_row([good, untouched]).measurement, good.measurement)

    def test_a_kind_with_only_untouched_payloads_is_not_applicable(self):
        untouched = extractive_row(ORIGINAL, QUESTIONS, [ReducerRun(ORIGINAL, 0.0)] * 3)
        self.assertTrue(kind_row([untouched, untouched]).not_applicable)


class VoidTests(unittest.TestCase):
    def setUp(self):
        self.full = qa_row(ORIGINAL, QUESTIONS, [QaRun(ORIGINAL, 0.0, [["Dana Okonkwo"] * 3, ["PR 8842"] * 3])])
        self.lossy = qa_row(ORIGINAL, QUESTIONS, [QaRun("x", 1.0, [["UNKNOWN"] * 3, ["PR 8842"] * 3])])

    def test_clean_controls_do_not_void_the_run(self):
        self.assertEqual(void_reasons({"a": self.full, "b": self.full}, {"a": self.lossy, "b": self.lossy}), [])

    def test_identity_losing_an_answer_voids_the_run(self):
        reasons = void_reasons({"a": self.full, "b": self.lossy}, {"a": self.lossy, "b": self.lossy})
        self.assertEqual(len(reasons), 1)
        self.assertIn("b", reasons[0])

    def test_summarizer_passing_on_more_than_half_voids_the_run(self):
        reasons = void_reasons({"a": self.full, "b": self.full, "c": self.full},
                               {"a": self.full, "b": self.full, "c": self.lossy})
        self.assertEqual(len(reasons), 1)

    def test_summarizer_passing_on_exactly_half_does_not_void(self):
        self.assertEqual(void_reasons({"a": self.full, "b": self.full},
                                      {"a": self.full, "b": self.lossy}), [])


if __name__ == "__main__":
    unittest.main()
