"""Read-only local worktree hygiene report. No cleanup, prune, fetch or mutations."""
import argparse
from datetime import date, datetime, timezone
import json
import os
from pathlib import Path
import subprocess
import sys


CACHES = {"bin", "obj", "__pycache__", ".pytest_cache"}


def git(root, *args):
    env = dict(os.environ, GIT_OPTIONAL_LOCKS="0", LC_ALL="C")
    result = subprocess.run(["git", "-C", str(root), *args], capture_output=True,
                            encoding="utf-8", errors="replace", env=env)
    return result.returncode, result.stdout, result.stderr


def required_git(root, *args):
    code, out, _ = git(root, *args)
    if code:
        raise RuntimeError("Git inspection failed: " + args[0])
    return out


def read_worktrees(root):
    records = []
    for block in required_git(root, "worktree", "list", "--porcelain", "-z").split("\0\0"):
        record = {}
        for line in block.split("\0"):
            if line:
                key, _, value = line.partition(" ")
                record[key] = value
        if "worktree" in record:
            records.append(record)
    return records


def key(path):
    return os.path.normcase(str(Path(path).resolve()))


def validate_registry(data):
    if data.get("schemaVersion") != 1 or not isinstance(data.get("worktrees"), list):
        raise ValueError("Registry requires schemaVersion=1 and worktrees array")
    seen = set()
    for entry in data["worktrees"]:
        if not all(entry.get(field) for field in
                   ("path", "purpose", "task", "state", "reviewedAt", "exitCondition")):
            raise ValueError("Each worktree requires path/purpose/task/state/reviewedAt/exitCondition")
        if entry["state"] not in {"active", "retained", "archived"}:
            raise ValueError("Unknown lifecycle state")
        date.fromisoformat(entry["reviewedAt"])
        if key(entry["path"]) in seen:
            raise ValueError("Duplicate registry path")
        seen.add(key(entry["path"]))
    if not data.get("activePath") or not data.get("baselineRef"):
        raise ValueError("Registry requires activePath and baselineRef")


def inspect(root, registry, today, review_days):
    validate_registry(registry)
    baseline = registry["baselineRef"]
    required_git(root, "rev-parse", "--verify", baseline + "^{commit}")
    entries = {key(e["path"]): e for e in registry["worktrees"]}
    records, findings, result = read_worktrees(root), [], []
    if key(root) != key(registry["activePath"]):
        findings.append({"code": "ACTIVE_WORKTREE_MISMATCH", "path": str(root)})
    actual = {key(record["worktree"]) for record in records}
    for entry in entries.values():
        if key(entry["path"]) not in actual:
            findings.append({"code": "REGISTRY_PATH_NOT_REGISTERED", "path": entry["path"]})
    for record in records:
        path = Path(record["worktree"])
        entry = entries.get(key(path))
        row = {"path": str(path), "head": record.get("HEAD"), "branch": record.get("branch"),
               "state": entry["state"] if entry else "unclassified", "preserveReasons": [],
               "artifacts": [], "errors": []}
        if not entry:
            findings.append({"code": "WORKTREE_UNCLASSIFIED", "path": str(path)})
        else:
            age = (today - date.fromisoformat(entry["reviewedAt"])).days
            if age < 0:
                findings.append({"code": "FUTURE_REVIEW_DATE", "path": str(path)})
            elif age > review_days:
                findings.append({"code": "REVIEW_OVERDUE", "path": str(path), "days": age})
        if "locked" in record:
            row["preserveReasons"].append("LOCKED")
        if "prunable" in record or not path.is_dir():
            row["preserveReasons"].append("UNAVAILABLE")
            row["errors"].append("WORKTREE_UNAVAILABLE")
            findings.append({"code": "WORKTREE_UNAVAILABLE", "path": str(path)})
            result.append(row)
            continue
        code, status, warning = git(path, "status", "--porcelain", "-z", "--untracked-files=all")
        if code or warning.strip():
            row["errors"].append("STATUS_INCOMPLETE")
            row["preserveReasons"].append("INSPECTION_INCOMPLETE")
        row["hasLocalChanges"] = bool(status)
        if status:
            row["preserveReasons"].append("LOCAL_CHANGES")
        ancestor, _, _ = git(root, "merge-base", "--is-ancestor", record["HEAD"], baseline)
        row["ancestorOfBaseline"] = ancestor == 0
        if ancestor == 1:
            row["preserveReasons"].append("HISTORY_NOT_IN_BASELINE")
        elif ancestor != 0:
            row["errors"].append("ANCESTRY_UNKNOWN")
        code, ignored, warning = git(path, "ls-files", "--others", "--ignored",
                                    "--exclude-standard", "--directory", "-z")
        if code or warning.strip():
            row["errors"].append("IGNORED_SCAN_INCOMPLETE")
        tracked = required_git(path, "ls-files", "-z").split("\0")
        for relative in ignored.split("\0"):
            if not relative:
                continue
            target = path / relative
            leaf = Path(relative.rstrip("/")).name
            category = "regenerable_candidate" if leaf in CACHES else "unclassified_preserve"
            if leaf == "TestResults" or "artifacts" in Path(relative).parts:
                category = "evidence_or_output_review"
            if any(t.startswith(relative.rstrip("/") + "/") for t in tracked if t):
                category = "tracked_preserve"
            if target.is_symlink() or getattr(target, "is_junction", lambda: False)():
                category = "reparse_preserve"
            row["artifacts"].append({"path": relative, "category": category})
        if row["errors"]:
            findings.append({"code": "INSPECTION_INCOMPLETE", "path": str(path)})
        result.append(row)
    # Scan only maintained instructions/guides/scripts in the active checkout.
    # Emit locations, never file contents (which may contain private data).
    old_paths = [p for e in entries.values() for p in e.get("previousPaths", [])]
    candidates = [root / "AGENTS.md", root / "README.md", root / "README.zh-CN.md"]
    for folder in [root / "docs/guides", root / "tools/scripts", root / ".codex"]:
        if folder.exists():
            candidates.extend(p for p in folder.rglob("*") if p.suffix in {".md", ".py", ".ps1", ".toml"})
    for file in candidates:
        if not file.is_file() or file.is_symlink():
            continue
        try:
            for number, line in enumerate(file.read_text(encoding="utf-8-sig").splitlines(), 1):
                normalized = line.replace("\\", "/").lower()
                if any(old.replace("\\", "/").lower() in normalized for old in old_paths):
                    findings.append({"code": "OLD_PATH_REFERENCE", "file": str(file.relative_to(root)), "line": number})
        except (OSError, UnicodeError):
            findings.append({"code": "REFERENCE_SCAN_INCOMPLETE", "file": str(file.relative_to(root))})
    return {"generatedAt": datetime.now(timezone.utc).isoformat(), "mode": "read_only",
            "baselineRef": baseline, "worktrees": result, "findings": findings,
            "limits": ["Review age is not proof of inactivity", "Ancestry is not semantic equivalence",
                       "Cache candidates are not deletion authorization", "No recursive size or process ownership scan",
                       "Ignored unknown files are preserved", "Old-path scan covers selected active-checkout text only"]}


def annotate_artifacts(report, registry):
    """Attach reviewed local records; missing provenance remains explicitly unknown."""
    if registry.get("schemaVersion") != 1 or not isinstance(registry.get("artifacts"), list):
        raise ValueError("Invalid artifact registry")
    entries = {}
    for entry in registry["artifacts"]:
        if not all(entry.get(field) for field in ("path", "kind", "evidence", "retentionCondition")):
            raise ValueError("Incomplete artifact record")
        canonical = key(entry["path"])
        if canonical in entries:
            raise ValueError("Duplicate artifact path")
        entries[canonical] = entry
    observed = set()
    for row in report["worktrees"]:
        for artifact in row["artifacts"]:
            if artifact["category"] != "evidence_or_output_review":
                continue
            path = Path(row["path"]) / artifact["path"]
            canonical = key(path)
            observed.add(canonical)
            entry = entries.get(canonical)
            if entry is None:
                report["findings"].append({"code": "ARTIFACT_UNREGISTERED", "path": str(path)})
                continue
            artifact["lifecycle"] = entry
            if entry.get("state") == "removed_after_user_cleanup":
                continue
            # Current direct children are cheap to compare and catch new mixed content.
            # This is a shallow fingerprint, not proof that nested content is unchanged.
            try:
                actual = sorted(p.name for p in path.iterdir()) if path.is_dir() else []
                if "directChildren" in entry and actual != entry["directChildren"]:
                    report["findings"].append({"code": "ARTIFACT_CONTENTS_CHANGED", "path": str(path)})
            except OSError:
                report["findings"].append({"code": "ARTIFACT_INSPECTION_INCOMPLETE", "path": str(path)})
    for canonical, entry in entries.items():
        if entry.get("state") == "removed_after_user_cleanup":
            if Path(entry["path"]).exists():
                report["findings"].append({"code": "ARTIFACT_REAPPEARED", "path": entry["path"]})
            continue
        if not Path(entry["path"]).exists():
            report["findings"].append({"code": "ARTIFACT_RECORD_PATH_MISSING", "path": entry["path"]})
    report["artifactRegistry"] = {"records": len(entries), "matchedRoots": len(observed & entries.keys()),
                                   "scope": "shallow local records; producing HEAD and task may be unknown"}
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo", type=Path, default=Path(__file__).resolve().parents[2])
    parser.add_argument("--registry", type=Path)
    parser.add_argument("--artifact-registry", type=Path)
    parser.add_argument("--review-days", type=int, default=14)
    parser.add_argument("--json", action="store_true")
    args = parser.parse_args()
    try:
        if args.review_days < 1:
            raise ValueError("review-days must be positive")
        root = args.repo.resolve()
        registry_path = args.registry or root / "docs/internal/workspace-lifecycle.json"
        registry = json.loads(registry_path.read_text(encoding="utf-8-sig"))
        report = inspect(root, registry, date.today(), args.review_days)
        artifact_path = args.artifact_registry or root / "docs/internal/artifact-lifecycle.json"
        if artifact_path.exists():
            report = annotate_artifacts(report, json.loads(artifact_path.read_text(encoding="utf-8-sig")))
        elif args.artifact_registry:
            raise ValueError("Explicit artifact registry missing")
        else:
            report["findings"].append({"code": "ARTIFACT_REGISTRY_MISSING"})
        if args.json:
            print(json.dumps(report, ensure_ascii=False, indent=2))
        else:
            print("Read-only workspace hygiene report")
            for row in report["worktrees"]:
                caches = sum(a["category"] == "regenerable_candidate" for a in row["artifacts"])
                print(f'{Path(row["path"]).name}: {row["state"]}; cache candidates={caches}; '
                      f'preserve={",".join(row["preserveReasons"]) or "review local content first"}')
            if "artifactRegistry" in report:
                print(json.dumps(report["artifactRegistry"], ensure_ascii=False))
            for finding in report["findings"]:
                print(json.dumps(finding, ensure_ascii=False))
            print(f'Findings={len(report["findings"])}; no deletion permission inferred.')
        # Findings are advisory. Exit 2 is reserved for invalid configuration/inspection failure.
        return 0
    except (OSError, ValueError, RuntimeError, KeyError) as error:
        print("Hygiene check unavailable: " + type(error).__name__, file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
