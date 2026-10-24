import json
import tempfile
import unittest
from pathlib import Path

import tokens

CAL = {"ratio": 2.5, "rows": 80, "models": {"claude-sonnet-5": 60, "claude-haiku-5": 20},
       "first": "2026-10-23T18:00:00+00:00", "last": "2026-10-24T15:00:00+00:00",
       "holdout_median_abs_error": 0.123, "method": "x"}


class TokensTests(unittest.TestCase):
    def setUp(self):
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        path = Path(directory.name) / "cal.json"
        path.write_text(json.dumps(CAL))
        self.cal = tokens.load(path)

    def test_estimate_uses_the_recorded_ratio(self):
        self.assertEqual(tokens.estimate("x" * 100, self.cal), 40)

    def test_from_chars_rounds_and_floors_at_zero(self):
        self.assertEqual(tokens.from_chars(7, self.cal), 3)
        self.assertEqual(tokens.from_chars(0, self.cal), 0)
        self.assertEqual(tokens.from_chars(-5, self.cal), 0)

    def test_method_names_ratio_rows_models_dates_and_error(self):
        m = tokens.method(self.cal)
        for part in ("estimated", "2.5 chars/token", "80", "claude-sonnet-5", "claude-haiku-5",
                     "2026-10-23", "2026-10-24", "12.3%"):
            self.assertIn(part, m)

    def test_committed_calibration_loads(self):
        self.assertGreater(tokens.load().ratio, 0)
        self.assertEqual(tokens.METHOD, tokens.method(tokens.load()))


if __name__ == "__main__":
    unittest.main()
