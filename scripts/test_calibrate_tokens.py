import gzip
import json
import unittest

from calibrate_tokens import fit_ratio, holdout_error, select


def row(text_chars, ratio, **extra):
    """A capture row whose body has about text_chars characters and tokens = chars / ratio."""
    body = {"model": extra.pop("model", "m1"), "messages": [{"role": "user", "content": "x" * text_chars}]}
    body.update(extra.pop("body", {}))
    raw = json.dumps(body).encode()
    tokens = round(len(raw) / ratio)
    return {"body": raw, "captured_at": extra.pop("at", "2026-10-24T10:00:00"),
            "input": tokens - 10, "cache_read": 6, "cache_creation": 4}


class SelectTests(unittest.TestCase):
    def test_thread_rows_are_excluded(self):
        rows = [row(400, 3.0), row(400, 3.0, body={"thread": {"type": "create"}})]
        self.assertEqual(len(select(rows)), 1)

    def test_image_and_document_rows_are_excluded(self):
        for kind in ("image", "document"):
            media = {"messages": [{"role": "user", "content": [{"type": kind, "source": {"data": "AAAA"}}]}]}
            with self.subTest(kind=kind):
                self.assertEqual(select([row(400, 3.0, body=media)]), [])

    def test_gzip_body_is_decoded_and_rows_without_usage_are_skipped(self):
        good = row(400, 3.0)
        zipped = dict(good, body=gzip.compress(good["body"]))
        no_usage = dict(good, input=None)
        samples = select([zipped, no_usage])
        self.assertEqual(len(samples), 1)
        self.assertEqual(samples[0].chars, len(good["body"].decode()))
        self.assertEqual(samples[0].tokens, good["input"] + 10)

    def test_a_property_named_type_that_holds_an_object_is_not_media(self):
        schema = {"tools": [{"input_schema": {"properties": {"type": {"type": "string"}}}}]}
        self.assertEqual(len(select([row(400, 3.0, body=schema)])), 1)

    def test_unreadable_body_is_skipped(self):
        self.assertEqual(select([dict(row(10, 3.0), body=b"\x00nope")]), [])


class FitTests(unittest.TestCase):
    def setUp(self):
        self.samples = select([row(200 + 37 * i, 3.2) for i in range(40)])

    def test_fit_recovers_a_known_ratio_within_one_percent(self):
        self.assertAlmostEqual(fit_ratio(self.samples), 3.2, delta=0.032)

    def test_holdout_error_is_small_for_exact_data_and_large_for_noise(self):
        self.assertLess(holdout_error(self.samples), 0.02)
        noisy = select([row(1000, 1.0 + (i % 5)) for i in range(40)])
        self.assertGreater(holdout_error(noisy), 0.1)

    def test_fit_is_the_median_so_one_outlier_does_not_move_it(self):
        samples = select([row(5000, 3.2) for _ in range(9)] + [row(5000, 0.5)])
        self.assertAlmostEqual(fit_ratio(samples), 3.2, delta=0.032)

    def test_holdout_predicts_each_fold_from_the_other_folds_only(self):
        # One row per fold, ratios 2, 2, 3, 4, 4: holdout errors are 3/7, 3/7, 0, 0.6, 0.6, so the
        # median is 3/7; an in-sample fit (ratio 3) would give 1/3.
        samples = select([row(100_000, r) for r in (2.0, 2.0, 3.0, 4.0, 4.0)])
        self.assertAlmostEqual(holdout_error(samples, folds=5), 3 / 7, delta=0.005)


if __name__ == "__main__":
    unittest.main()
