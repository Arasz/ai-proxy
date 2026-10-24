import json
import unittest

from claude_digest_runner import DIGESTS, child_env, command, parse_result


class CommandTests(unittest.TestCase):
    def test_the_call_is_isolated_and_on_haiku(self):
        args = command("the system prompt")
        self.assertEqual(args[:2], ["claude", "-p"])
        for flag in ("--no-session-persistence", "--strict-mcp-config"):
            self.assertIn(flag, args)
        self.assertEqual(args[args.index("--model") + 1], "claude-haiku-5-5")
        self.assertEqual(args[args.index("--tools") + 1], "")
        self.assertEqual(args[args.index("--setting-sources") + 1], "")
        self.assertEqual(args[args.index("--system-prompt") + 1], "the system prompt")
        self.assertEqual(args[args.index("--output-format") + 1], "json")

    def test_an_effort_is_passed_when_the_candidate_pins_one(self):
        args = command("p", "low")
        self.assertEqual(args[args.index("--effort") + 1], "low")

    def test_the_model_can_be_chosen_for_the_reader(self):
        args = command("p", "low", model="claude-opus-5-5")
        self.assertEqual(args[args.index("--model") + 1], "claude-opus-5-5")
        self.assertEqual(args[args.index("--tools") + 1], "")

    def test_no_effort_flag_when_none_is_pinned(self):
        self.assertNotIn("--effort", command("p"))


class DigestsTests(unittest.TestCase):
    def test_the_checked_digest_runs_at_low_effort_and_asks_for_a_fact_check(self):
        system, effort = DIGESTS["haiku_digest_checked"]
        self.assertEqual(effort, "low")
        self.assertIn("First, find all {facts}", system)
        self.assertIn("check compresed output for {facts}", system)

    def test_the_first_haiku_digest_keeps_its_recorded_settings(self):
        from pi_runner import DIGEST_SYSTEM
        self.assertEqual(DIGESTS["haiku_digest"], (DIGEST_SYSTEM, None))


class ChildEnvTests(unittest.TestCase):
    def test_the_session_effort_and_proxy_do_not_reach_the_call(self):
        env = child_env({"CLAUDE_EFFORT": "medium", "HTTPS_PROXY": "http://x", "PATH": "/bin"})
        self.assertEqual(env, {"PATH": "/bin", "NO_PROXY": "*"})


class ParseResultTests(unittest.TestCase):
    def test_the_reply_and_the_model_time_are_read(self):
        raw = json.dumps({"type": "result", "is_error": False, "result": "digest text",
                          "duration_ms": 5200, "duration_api_ms": 3100})
        self.assertEqual(parse_result(raw), ("digest text", 3.1))

    def test_an_error_result_is_refused(self):
        raw = json.dumps({"type": "result", "is_error": True, "result": "Credit balance is too low",
                          "duration_ms": 10, "duration_api_ms": 0})
        with self.assertRaises(RuntimeError):
            parse_result(raw)

    def test_a_reply_from_another_model_is_refused(self):
        raw = json.dumps({"type": "result", "is_error": False, "result": "digest", "duration_api_ms": 5,
                          "modelUsage": {"claude-sonnet-5-5": {}}})
        with self.assertRaises(RuntimeError):
            parse_result(raw)

    def test_an_empty_reply_is_refused(self):
        raw = json.dumps({"type": "result", "is_error": False, "result": "  ",
                          "duration_ms": 10, "duration_api_ms": 5})
        with self.assertRaises(RuntimeError):
            parse_result(raw)


if __name__ == "__main__":
    unittest.main()
