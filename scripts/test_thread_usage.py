import unittest

from thread_usage import Turn, link, system_changes


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


if __name__ == "__main__":
    unittest.main()
