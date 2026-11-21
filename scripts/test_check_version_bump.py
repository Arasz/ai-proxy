import importlib.util
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
spec = importlib.util.spec_from_file_location("check_version_bump", ROOT / "scripts" / "check-version-bump.py")
check_version_bump = importlib.util.module_from_spec(spec)
spec.loader.exec_module(check_version_bump)
check = check_version_bump.check


class VersionBumpTests(unittest.TestCase):
    def test_passes_when_version_is_unchanged(self):
        ok, _ = check("0.34.0", "v0.34.0", ["src/AiProxy/Program.cs"])
        self.assertTrue(ok)

    def test_passes_when_bumped_with_entry_and_greater_than_tag(self):
        ok, reason = check("0.35.0", "v0.34.0", ["VERSION", "docs/changelog/0.35.0-p8-ci.md"])
        self.assertTrue(ok, reason)

    def test_fails_when_bumped_without_changelog_entry(self):
        ok, reason = check("0.35.0", "v0.34.0", ["VERSION"])
        self.assertFalse(ok)
        self.assertIn("docs/changelog/0.35.0-", reason)

    def test_fails_when_entry_is_for_another_version(self):
        ok, _ = check("0.35.0", "v0.34.0", ["VERSION", "docs/changelog/0.34.0-old.md"])
        self.assertFalse(ok)

    def test_fails_when_bumped_version_equals_latest_tag(self):
        ok, reason = check("0.34.0", "v0.34.0", ["VERSION", "docs/changelog/0.34.0-x.md"])
        self.assertFalse(ok)
        self.assertIn("v0.34.0", reason)

    def test_fails_when_bumped_version_is_below_latest_tag(self):
        ok, _ = check("0.9.0", "v0.34.0", ["VERSION", "docs/changelog/0.9.0-x.md"])
        self.assertFalse(ok)

    def test_compares_numerically_not_as_text(self):
        ok, reason = check("0.10.0", "v0.9.0", ["VERSION", "docs/changelog/0.10.0-x.md"])
        self.assertTrue(ok, reason)

    def test_passes_with_no_tag_yet(self):
        ok, reason = check("0.1.0", None, ["VERSION", "docs/changelog/0.1.0-x.md"])
        self.assertTrue(ok, reason)

    def test_fails_on_unparseable_version(self):
        ok, _ = check("banana", "v0.34.0", ["VERSION", "docs/changelog/banana-x.md"])
        self.assertFalse(ok)

    def test_latest_tag_picks_the_highest_version(self):
        self.assertEqual(check_version_bump.latest_tag(["v0.9.0", "v0.10.0", "v0.2.0", "other"]), "v0.10.0")


class WorkflowTests(unittest.TestCase):
    def read(self, name):
        return (ROOT / ".github" / "workflows" / name).read_text(encoding="utf-8")

    def test_lifecycle_cancels_only_off_main(self):
        self.assertIn("cancel-in-progress: ${{ github.ref != 'refs/heads/main' }}", self.read("lifecycle.yml"))

    def test_publish_checks_out_the_version_tag(self):
        text = self.read("publish.yml")
        self.assertIn("refs/tags/v", text)
        self.assertIn("is missing", text)

    def test_build_does_not_trigger_on_labeled(self):
        self.assertNotIn("labeled", self.read("build.yml").split("jobs:")[0].replace("# ", ""))

    def test_build_calls_the_version_script(self):
        self.assertIn("scripts/check-version-bump.py", self.read("build.yml"))


if __name__ == "__main__":
    unittest.main()
