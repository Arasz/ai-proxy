import unittest

from compression_score import Measurement, latency_score, score


def measured(reduced=500, latency_s=1.0, kept=4, total=4):
    return Measurement(original_chars=1000, reduced_chars=reduced, latency_s=latency_s,
                       answers_kept=kept, answers_total=total)


class LatencyScoreTests(unittest.TestCase):
    def test_free_call_scores_one(self):
        self.assertEqual(latency_score(0.0, 1.0), 1.0)

    def test_call_at_budget_scores_half(self):
        self.assertEqual(latency_score(2.0, 2.0), 0.5)

    def test_call_over_budget_stays_above_zero(self):
        self.assertGreater(latency_score(100.0, 1.0), 0.0)


class ScoreTests(unittest.TestCase):
    def test_one_lost_answer_zeroes_the_score(self):
        self.assertEqual(score(measured(reduced=10, latency_s=0.0, kept=3), budget_s=1.0), 0.0)

    def test_no_compression_zeroes_the_score(self):
        self.assertEqual(score(measured(reduced=1000), budget_s=1.0), 0.0)

    def test_full_retention_is_harmonic_mean_of_compression_and_latency(self):
        # compression 0.5, latency 1/(1+1) = 0.5 -> harmonic mean 0.5
        self.assertAlmostEqual(score(measured(), budget_s=1.0), 0.5)

    def test_imbalance_scores_below_balance(self):
        lopsided = score(measured(reduced=10, latency_s=9.0), budget_s=1.0)   # C 0.99, L 0.1
        balanced = score(measured(reduced=500, latency_s=1.0), budget_s=1.0)  # C 0.5,  L 0.5
        self.assertLess(lopsided, balanced)

    def test_lower_floor_lets_partial_retention_score(self):
        self.assertGreater(score(measured(kept=3), budget_s=1.0, retention_floor=0.75), 0.0)


if __name__ == "__main__":
    unittest.main()
