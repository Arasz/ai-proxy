import json
import unittest

from rtk_runner import added_chars, ccr_latencies, request_body, tool_result_text


class ToolResultTextTests(unittest.TestCase):
    def test_string_content_comes_back_as_sent(self):
        body = json.loads(request_body("plain output"))
        self.assertEqual(tool_result_text(body), "plain output")

    def test_block_content_counts_every_character_the_model_sees(self):
        body = json.loads(request_body("x"))
        body["messages"][1]["content"][0]["content"] = [
            {"type": "text", "text": "head"}, {"type": "text", "text": "[rtk_retrieve abc]"}]
        self.assertEqual(tool_result_text(body), "head[rtk_retrieve abc]")


class AddedCharsTests(unittest.TestCase):
    def test_an_injected_tool_definition_counts_toward_what_the_model_sees(self):
        body = json.loads(request_body("x"))
        body["tools"] = [{"name": "rtk_retrieve", "description": "get it back"}]
        self.assertEqual(added_chars(body), len(json.dumps(body["tools"])))

    def test_a_body_without_tools_adds_nothing(self):
        self.assertEqual(added_chars(json.loads(request_body("x"))), 0)


class CcrLatencyTests(unittest.TestCase):
    def test_each_run_is_its_round_trip_minus_the_median_baseline(self):
        self.assertEqual(ccr_latencies([0.5, 0.7], [0.1, 0.2, 0.9]), [0.3, 0.5])

    def test_a_run_faster_than_the_baseline_counts_as_free(self):
        self.assertEqual(ccr_latencies([0.1], [0.2]), [0.0])


if __name__ == "__main__":
    unittest.main()
