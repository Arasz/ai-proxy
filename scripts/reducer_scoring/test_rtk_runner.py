import json
import tempfile
import unittest
from unittest.mock import Mock, patch

import rtk_runner

from rtk_runner import added_tokens, ccr_latencies, order_for, request_body, tool_result_text
import tokens  # noqa: E402


class ToolResultTextTests(unittest.TestCase):
    def test_a_payload_at_or_under_the_ccr_threshold_is_under_minimum(self):
        threshold = rtk_runner.ccr_threshold(rtk_runner.PIPELINE)
        self.assertEqual(threshold, 1000)
        self.assertTrue(rtk_runner.under_minimum("x " * 10, threshold))
        self.assertFalse(rtk_runner.under_minimum("word " * 2000, threshold))

    def test_string_content_comes_back_as_sent(self):
        body = json.loads(request_body("plain output"))
        self.assertEqual(tool_result_text(body), "plain output")

    def test_block_content_counts_every_character_the_model_sees(self):
        body = json.loads(request_body("x"))
        body["messages"][1]["content"][0]["content"] = [
            {"type": "text", "text": "head"}, {"type": "text", "text": "[rtk_retrieve abc]"}]
        self.assertEqual(tool_result_text(body), "head[rtk_retrieve abc]")


class AddedTokensTests(unittest.TestCase):
    def test_an_injected_tool_definition_counts_toward_what_the_model_sees(self):
        body = json.loads(request_body("x"))
        body["tools"] = [{"name": "rtk_retrieve", "description": "get it back"}]
        self.assertEqual(added_tokens(body), tokens.estimate(json.dumps(body["tools"])))

    def test_a_body_without_tools_adds_nothing(self):
        self.assertEqual(added_tokens(json.loads(request_body("x"))), 0)


class CcrLatencyTests(unittest.TestCase):
    def test_each_run_is_its_round_trip_minus_the_median_baseline(self):
        self.assertEqual(ccr_latencies([0.5, 0.7], [0.1, 0.2, 0.9]), [0.3, 0.5])

    def test_a_run_faster_than_the_baseline_counts_as_free(self):
        self.assertEqual(ccr_latencies([0.1], [0.2]), [0.0])


class RoundTripOrderTests(unittest.TestCase):
    def test_the_first_run_sends_the_ccr_request_first(self):
        self.assertEqual(order_for(0), "ccr_first")

    def test_the_order_alternates_between_the_two_proxies(self):
        self.assertEqual([order_for(run) for run in range(4)], ["ccr_first", "plain_first", "ccr_first", "plain_first"])


class StartupCleanupTests(unittest.TestCase):
    def test_readiness_failure_terminates_the_process_it_launched(self):
        proc = Mock()
        with tempfile.TemporaryDirectory() as home, \
                patch("rtk_runner.subprocess.Popen", return_value=proc), \
                patch("rtk_runner._wait_for", side_effect=RuntimeError("not ready")):
            with self.assertRaisesRegex(RuntimeError, "not ready"):
                rtk_runner._start_rtk(home, 18081, 18997, True)
        proc.terminate.assert_called_once_with()
        proc.wait.assert_called_once_with(timeout=10)

    def test_second_startup_failure_stops_first_proxy_and_both_upstreams(self):
        first = Mock()
        upstreams = [Mock(), Mock()]
        with patch("sys.argv", ["rtk_runner.py", "1", "payload"]), \
                patch("rtk_runner.Upstream", side_effect=upstreams), \
                patch("rtk_runner._start_rtk", side_effect=[first, RuntimeError("second failed")]):
            with self.assertRaisesRegex(RuntimeError, "second failed"):
                rtk_runner.main()
        first.terminate.assert_called_once_with()
        first.wait.assert_called_once_with(timeout=10)
        for upstream in upstreams:
            upstream.shutdown.assert_called_once_with()


if __name__ == "__main__":
    unittest.main()
