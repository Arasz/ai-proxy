import gzip
import json
import os
import sqlite3
import tempfile
import unittest
from unittest.mock import patch

from capture_regions import read_bodies, regions, render, summarize


def body(**fields):
    return json.dumps(fields).encode()


def kinds(request):
    return [(r.kind, r.chars) for r in regions(request)]


class RegionTests(unittest.TestCase):
    def test_string_and_block_system_prompts_are_both_system(self):
        self.assertEqual(kinds({"system": "abcd"}), [("system", 4)])
        self.assertEqual(kinds({"system": [{"type": "text", "text": "ab"}, {"type": "text", "text": "cde"}]}),
                         [("system", 2), ("system", 3)])

    def test_each_tool_definition_is_one_region(self):
        tool = {"name": "Read", "description": "reads", "input_schema": {"type": "object"}}
        self.assertEqual(kinds({"tools": [tool, tool]}), [("tools", len(json.dumps(tool, sort_keys=True)))] * 2)

    def test_blocks_are_classified_by_role_and_type(self):
        request = {"messages": [
            {"role": "user", "content": "hi"},
            {"role": "assistant", "content": [
                {"type": "thinking", "thinking": "hmm", "signature": "sig"},
                {"type": "redacted_thinking", "data": "xxxx"},
                {"type": "text", "text": "ok!"},
                {"type": "tool_use", "id": "t1", "name": "Bash", "input": {"c": 1}},
            ]},
            {"role": "user", "content": [
                {"type": "tool_result", "tool_use_id": "t1", "content": [{"type": "text", "text": "out"}]},
                {"type": "tool_result", "tool_use_id": "t2", "content": "12345"},
                {"type": "image", "source": {"type": "base64", "data": "AAAA"}},
            ]},
        ]}
        self.assertEqual(kinds(request), [
            ("user.text", 2),
            ("assistant.thinking", 3), ("assistant.thinking", 4),
            ("assistant.text", 3),
            ("assistant.tool_use", len(json.dumps({"c": 1}, sort_keys=True))),
            ("user.tool_result", 3), ("user.tool_result", 5),
            ("user.image", 4),
        ])

    def test_an_unknown_block_type_keeps_its_name(self):
        block = {"type": "tool_addition", "tool": {"name": "X"}}
        self.assertEqual(kinds({"messages": [{"role": "system", "content": [block]}]}),
                         [("system.tool_addition", len(json.dumps(block, sort_keys=True)))])

    def test_a_moved_cache_breakpoint_keeps_the_region_identity(self):
        plain = regions({"system": [{"type": "text", "text": "x"}]})
        marked = regions({"system": [{"type": "text", "text": "x", "cache_control": {"type": "ephemeral"}}]})
        self.assertEqual(plain[0].digest, marked[0].digest)

    def test_same_text_in_different_kinds_is_a_different_region(self):
        a = regions({"system": "same"})[0]
        b = regions({"messages": [{"role": "user", "content": "same"}]})[0]
        self.assertNotEqual(a.digest, b.digest)


class SummaryTests(unittest.TestCase):
    def test_a_region_resent_on_a_later_turn_is_new_only_once(self):
        first = body(system="abcd", messages=[{"role": "user", "content": "q1"}])
        second = body(system="abcd", messages=[{"role": "user", "content": "q1"},
                                               {"role": "assistant", "content": "a1"},
                                               {"role": "user", "content": "q2"}])
        s = summarize([first, second])
        self.assertEqual(s.requests, 2)
        self.assertEqual((s.kinds["system"].sent_chars, s.kinds["system"].new_chars), (8, 4))
        self.assertEqual((s.kinds["user.text"].sent_chars, s.kinds["user.text"].new_chars), (6, 4))
        self.assertEqual(s.kinds["assistant.text"].new_chars, 2)

    def test_identical_tool_results_from_different_invocations_are_new_only_once(self):
        for content in ("hello world", [{"type": "text", "text": "hello world"}]):
            with self.subTest(content=content):
                requests = [body(messages=[{"role": "user", "content": [
                    {"type": "tool_result", "tool_use_id": invocation, "content": content,
                     "is_error": is_error}]}])
                    for invocation, is_error in (("t1", False), ("t2", True))]
                requests.append(body(messages=[{"role": "user", "content": [
                    {"type": "tool_result", "tool_use_id": "t3", "content": "different"}]}]))
                s = summarize(requests)
                totals = s.kinds["user.tool_result"]
                self.assertEqual((totals.count, totals.sent_chars, totals.new_chars), (3, 31, 20))

    def test_cache_breakpoints_are_counted(self):
        s = summarize([body(system=[{"type": "text", "text": "x", "cache_control": {"type": "ephemeral"}}],
                            messages=[{"role": "user", "content": [
                                {"type": "text", "text": "y", "cache_control": {"type": "ephemeral"}}]}])])
        self.assertEqual(s.cache_breakpoints, 2)

    def test_requests_are_counted_by_model_and_thread_mode(self):
        s = summarize([
            body(model="m1", messages=[]),
            body(model="m2", thread={"type": "create"}, messages=[]),
            body(model="m2", thread={"type": "continue", "previous_message_id": "msg_1"}, messages=[]),
        ])
        self.assertEqual(s.models, {"m1": 1, "m2": 2})
        self.assertEqual(s.modes, {"stateless": 1, "thread.create": 1, "thread.continue": 1})

    def test_a_gzip_body_is_read(self):
        s = summarize([gzip.compress(body(system="abcd"))])
        self.assertEqual((s.requests, s.unreadable), (1, 0))

    def test_truncated_gzip_is_unreadable_and_reporting_continues(self):
        truncated = gzip.compress(body(system="abcd"))[:-1]
        s = summarize([truncated, body(system="ab")])
        self.assertEqual((s.requests, s.unreadable), (1, 1))
        self.assertEqual(s.kinds["system"].sent_chars, 2)

    def test_corrupt_deflate_is_unreadable_and_reporting_continues(self):
        corrupt = b"\x1f\x8b\x08\x00\x00\x00\x00\x00\x00\xff\x07"
        s = summarize([corrupt, body(system="ab")])
        self.assertEqual((s.requests, s.unreadable), (1, 1))
        self.assertEqual(s.kinds["system"].sent_chars, 2)

    def test_an_unreadable_body_is_counted_not_raised(self):
        s = summarize([b"\x00not json", body(system="ab")])
        self.assertEqual((s.requests, s.unreadable), (1, 1))


class ReadTests(unittest.TestCase):
    def test_first_body_is_yielded_before_the_remaining_bodies_are_loaded(self):
        connection = sqlite3.connect(":memory:")
        loaded = []
        def observe(raw):
            loaded.append(raw)
            return raw
        connection.create_function("observe", 1, observe)
        connection.executescript("""
            create table captures(captured_at integer, path text, body blob);
            create index capture_order on captures(captured_at);
            create view capture_request as
                select captured_at, path, observe(body) as body from captures;
        """)
        connection.executemany("insert into captures values (?, '/v1/messages', ?)",
                               [(i, str(i).encode()) for i in range(10)])
        with patch("capture_regions.sqlite3.connect", return_value=connection):
            bodies = read_bodies("unused.db")
            try:
                self.assertEqual(next(bodies), b"0")
                self.assertLess(len(loaded), 10)
            finally:
                bodies.close()
        with self.assertRaises(sqlite3.ProgrammingError):
            connection.execute("select 1")

    def test_only_message_requests_are_read(self):
        with tempfile.TemporaryDirectory() as tmp:
            db = os.path.join(tmp, "capture.db")
            with sqlite3.connect(db) as c:
                c.execute("create table capture_request(request_id text primary key, captured_at text,"
                          " provider text, host text, path text, body blob)")
                c.executemany("insert into capture_request values (?, '', 'Anthropic', 'h', ?, ?)", [
                    ("1", "/v1/messages", b"m"),
                    ("2", "/v1/messages/count_tokens", b"c"),
                    ("3", "/v1/code/sessions/s/worker/events", b"e"),
                ])
            c.close()
            self.assertEqual(list(read_bodies(db)), [b"m"])


class RenderTests(unittest.TestCase):
    def test_the_report_carries_counts_never_content(self):
        secret = "PRIVATE-MARKER-7f3a"
        s = summarize([body(system=secret, tools=[{"name": secret}],
                            messages=[{"role": "user", "content": secret}])])
        text = render(s)
        self.assertNotIn(secret, text)
        self.assertNotIn(secret, json.dumps(s.as_dict()))
        self.assertIn("system", text)


if __name__ == "__main__":
    unittest.main()
