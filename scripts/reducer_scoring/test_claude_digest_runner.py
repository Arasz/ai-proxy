import json
import unittest

from claude_digest_runner import command, parse_result


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
