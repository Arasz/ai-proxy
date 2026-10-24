import unittest

from kompress_runner import chunk_spans


class ChunkSpanTests(unittest.TestCase):
    def test_every_token_is_scored_past_the_first_chunk(self):
        spans = chunk_spans(20000, 8192)
        self.assertEqual(spans, [(0, 8192), (8192, 16384), (16384, 20000)])

    def test_a_short_input_is_one_chunk(self):
        self.assertEqual(chunk_spans(10, 8192), [(0, 10)])


if __name__ == "__main__":
    unittest.main()
