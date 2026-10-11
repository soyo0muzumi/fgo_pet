"""Exercise read-only hygiene decisions against an isolated synthetic Git repo."""
import importlib.util
import json
from pathlib import Path
import subprocess
import tempfile
import unittest
from datetime import date
from unittest.mock import patch


SCRIPT = Path(__file__).resolve().parents[1] / "tools/scripts/check-workspace-hygiene.py"
spec = importlib.util.spec_from_file_location("workspace_hygiene", SCRIPT)
hygiene = importlib.util.module_from_spec(spec)
spec.loader.exec_module(hygiene)


class WorkspaceHygieneTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.command("init", "-b", "baseline")
        (self.root / ".gitignore").write_text("bin/\nartifacts/\n", encoding="utf-8")
        self.command("add", ".")
        self.command("-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid",
                     "commit", "-m", "Synthetic fixture")
        self.registry = {"schemaVersion": 1, "activePath": str(self.root), "baselineRef": "baseline",
                         "worktrees": [{"path": str(self.root), "purpose": "fixture", "task": "test",
                                        "state": "active", "reviewedAt": "2026-10-04",
                                        "exitCondition": "end fixture"}]}

    def command(self, *args):
        return subprocess.check_output(["git", "-C", str(self.root), *args], stderr=subprocess.DEVNULL)

    def report(self):
        return hygiene.inspect(self.root, self.registry, date(2026, 10, 4), 14)

    def test_report_preserves_files_and_git_index(self):
        for relative in ["bin/cache.txt", "artifacts/evidence.txt", "user-work.txt"]:
            file = self.root / relative
            file.parent.mkdir(exist_ok=True)
            file.write_text(relative, encoding="utf-8")
        before = {str(p.relative_to(self.root)): p.read_bytes()
                  for p in self.root.rglob("*") if p.is_file()}
        report = self.report()
        after = {str(p.relative_to(self.root)): p.read_bytes()
                 for p in self.root.rglob("*") if p.is_file()}
        self.assertEqual(before, after)
        row = report["worktrees"][0]
        self.assertIn("LOCAL_CHANGES", row["preserveReasons"])
        self.assertEqual({a["path"]: a["category"] for a in row["artifacts"]},
                         {"artifacts/": "evidence_or_output_review", "bin/": "regenerable_candidate"})

    def test_review_age_and_old_path_are_advisory(self):
        entry = self.registry["worktrees"][0]
        entry["reviewedAt"] = "2026-09-01"
        entry["previousPaths"] = ["D:/synthetic/old-tree"]
        (self.root / "AGENTS.md").write_text("Use D:\\synthetic\\old-tree", encoding="utf-8")
        codes = {f["code"] for f in self.report()["findings"]}
        self.assertIn("REVIEW_OVERDUE", codes)
        self.assertIn("OLD_PATH_REFERENCE", codes)

    def test_unknown_tree_and_wrong_target_are_reported(self):
        self.registry["activePath"] = str(self.root / "other")
        self.registry["worktrees"] = []
        codes = {f["code"] for f in self.report()["findings"]}
        self.assertEqual(codes, {"ACTIVE_WORKTREE_MISMATCH", "WORKTREE_UNCLASSIFIED"})

    def test_git_warning_is_not_silent_success(self):
        real = hygiene.git
        def warning(root, *args):
            if args[0] == "status":
                return 0, "", "warning: synthetic permission denied"
            return real(root, *args)
        with patch.object(hygiene, "git", side_effect=warning):
            row = self.report()["worktrees"][0]
        self.assertIn("INSPECTION_INCOMPLETE", row["preserveReasons"])

    def test_duplicate_registry_path_rejected(self):
        self.registry["worktrees"].append(dict(self.registry["worktrees"][0]))
        with self.assertRaises(ValueError):
            self.report()

    def test_new_artifact_requires_record(self):
        (self.root / "artifacts").mkdir()
        report = hygiene.annotate_artifacts(self.report(), {"schemaVersion": 1, "artifacts": []})
        self.assertIn("ARTIFACT_UNREGISTERED", {f["code"] for f in report["findings"]})

    def test_new_child_invalidates_reviewed_structure(self):
        path = self.root / "artifacts"
        path.mkdir()
        record = {"path": str(path), "kind": "mixed_preserve", "evidence": "empty at review",
                  "retentionCondition": "review", "directChildren": [], "producingHead": None}
        (path / "new-evidence.txt").write_text("synthetic", encoding="utf-8")
        report = hygiene.annotate_artifacts(self.report(), {"schemaVersion": 1, "artifacts": [record]})
        self.assertIn("ARTIFACT_CONTENTS_CHANGED", {f["code"] for f in report["findings"]})
        self.assertIsNone(report["worktrees"][0]["artifacts"][0]["lifecycle"]["producingHead"])

    def test_missing_artifact_record_path_is_reported(self):
        record = {"path": str(self.root / "missing"), "kind": "unknown_preserve",
                  "evidence": "previous record", "retentionCondition": "review"}
        report = hygiene.annotate_artifacts(self.report(), {"schemaVersion": 1, "artifacts": [record]})
        self.assertIn("ARTIFACT_RECORD_PATH_MISSING", {f["code"] for f in report["findings"]})

    def test_confirmed_cleanup_only_reports_reappearance(self):
        path = self.root / "removed-output"
        record = {"path": str(path), "kind": "build_output_candidate",
                  "evidence": "verified removal", "retentionCondition": "cleanup record retained",
                  "state": "removed_after_user_cleanup"}
        registry = {"schemaVersion": 1, "artifacts": [record]}
        report = hygiene.annotate_artifacts(self.report(), registry)
        self.assertNotIn("ARTIFACT_RECORD_PATH_MISSING", {f["code"] for f in report["findings"]})
        path.mkdir()
        report = hygiene.annotate_artifacts(self.report(), registry)
        self.assertIn("ARTIFACT_REAPPEARED", {f["code"] for f in report["findings"]})


if __name__ == "__main__":
    unittest.main()
