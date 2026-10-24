import json
import os
import sqlite3
import tempfile
import unittest

from thread_usage import Turn, link, read, report, system_changes


def turn(at, read, creation, key=("s", "m"), new=2):
    return Turn(at=at, key=key, input=new, read=read, creation=creation, output=10)


class LinkTests(unittest.TestCase):
    def test_a_turn_whose_reads_equal_the_last_turns_cached_total_continues_that_thread(self):
        threads = link([turn("1", 0, 100), turn("2", 100, 20), turn("3", 120, 5)])
        self.assertEqual([[t.at for t in th] for th in threads], [["1", "2", "3"]])

    def test_interleaved_threads_are_told_apart_by_their_totals(self):
        threads = link([turn("1", 0, 100), turn("2", 0, 50), turn("3", 100, 1), turn("4", 50, 2), turn("5", 101, 1)])
        self.assertEqual([[t.at for t in th] for th in threads], [["1", "3", "5"], ["2", "4"]])

    def test_a_turn_that_matches_no_thread_starts_a_new_one(self):
        threads = link([turn("1", 0, 100), turn("2", 99, 5)])
        self.assertEqual(len(threads), 2)

    def test_threads_of_another_session_or_model_are_never_joined(self):
        threads = link([turn("1", 0, 100), turn("2", 100, 5, key=("other", "m"))])
        self.assertEqual(len(threads), 2)


class SystemChangeTests(unittest.TestCase):
    def test_only_the_block_that_changed_between_two_requests_is_counted(self):
        changes = system_changes([("s", ["a", "b", "c"]), ("s", ["a", "B", "c"]), ("s", ["a", "B", "c"])])
        self.assertEqual(changes, {"pairs": 2, "by_block": {1: 1}, "count_changed": 0})

    def test_a_request_with_a_different_number_of_blocks_counts_as_a_count_change(self):
        changes = system_changes([("s", ["a"]), ("s", ["a", "b"]), ("t", ["x"])])
        self.assertEqual(changes, {"pairs": 1, "by_block": {}, "count_changed": 1})


def store(rows):
    """A capture store with the given (captured_at, path, body, usage) rows; usage None means no usage row."""
    path = os.path.join(tempfile.mkdtemp(), "capture.db")
    with sqlite3.connect(path) as db:
        db.execute("create table capture_request (request_id text, captured_at text, provider text, host text, path text, body blob)")
        db.execute("create table capture_response (request_id text, input_tokens int, cache_read_input_tokens int, "
                   "cache_creation_input_tokens int, output_tokens int)")
        for index, (at, url, body, usage) in enumerate(rows):
            db.execute("insert into capture_request values (?, ?, 'anthropic', 'h', ?, ?)", (str(index), at, url, json.dumps(body).encode()))
            if usage is not None:
                db.execute("insert into capture_response values (?, ?, ?, ?, ?)", (str(index), *usage))
    return path


def request(header, *blocks):
    return {"model": "m", "metadata": {"user_id": json.dumps({"session_id": "s1"})}, "thread": {"type": "continue"},
            "system": [{"type": "text", "text": header}, *({"type": "text", "text": b} for b in blocks)]}


class ReportTests(unittest.TestCase):
    def test_a_changing_header_is_left_out_of_the_after_header_churn(self):
        path = store([("2026-10-24T10:00:00", "/v1/messages", request("h1", "same"), (2, 0, 100, 5)),
                      ("2026-10-24T10:01:00", "/v1/messages", request("h2", "same"), (2, 100, 10, 5))])
        built = report(*read(path), min_turns=1)
        self.assertEqual(built["system"]["by_block"], {0: 1})
        self.assertEqual(built["system_after_header"]["by_block"], {})

    def test_the_cutoff_is_inclusive_and_other_paths_and_rows_without_usage_are_skipped(self):
        path = store([("2026-10-24T10:00:00", "/v1/messages", request("h"), (2, 0, 100, 5)),
                      ("2026-10-24T10:01:00", "/v1/messages", request("h"), (2, 100, 10, 5)),
                      ("2026-10-24T10:02:00", "/v1/other", request("h"), (2, 110, 10, 5)),
                      ("2026-10-24T10:03:00", "/v1/messages", request("h"), None),
                      ("2026-10-24T10:04:00", "/v1/messages", request("h"), (2, 110, 1, 5))])
        turns, _, _ = read(path, "2026-10-24T10:01:00")
        self.assertEqual([t.at for t in turns], ["2026-10-24T10:00:00", "2026-10-24T10:01:00"])
        self.assertEqual(len(read(path)[0]), 3)


if __name__ == "__main__":
    unittest.main()
