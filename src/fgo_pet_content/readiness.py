from __future__ import annotations

import hashlib
import json
from pathlib import Path
from typing import Literal

from pydantic import BaseModel, ConfigDict

from .profile import MashProfile


class ReadinessInputs(BaseModel):
    model_config = ConfigDict(extra="forbid")

    data_root: Path
    visual_qa: Literal["approved", "pending", "rejected"] = "pending"


class ReadinessCheck(BaseModel):
    model_config = ConfigDict(extra="forbid")

    id: str
    status: Literal["PASS", "BLOCKED"]
    evidence_path: str
    detail: str


class ReadinessResult(BaseModel):
    model_config = ConfigDict(extra="forbid")

    schema_version: Literal[1] = 1
    character_id: Literal[800100] = 800100
    status: Literal["PASS", "BLOCKED"]
    checks: list[ReadinessCheck]
    artifact_hashes: dict[str, str]

    @property
    def failed_checks(self) -> list[str]:
        return [item.id for item in self.checks if item.status == "BLOCKED"]


def _hash(path: Path) -> str:
    return f"sha256:{hashlib.sha256(path.read_bytes()).hexdigest()}"


def _resolve_inside(root: Path, relative: str) -> Path:
    resolved_root = root.resolve()
    candidate = (resolved_root / relative).resolve()
    if not candidate.is_relative_to(resolved_root):
        raise ValueError("artifact path escapes bundle root")
    return candidate


def _check(
    check_id: str, passed: bool, path: Path, pass_detail: str, fail_detail: str
) -> ReadinessCheck:
    return ReadinessCheck(
        id=check_id,
        status="PASS" if passed else "BLOCKED",
        evidence_path=str(path),
        detail=pass_detail if passed else fail_detail,
    )


def evaluate_readiness(inputs: ReadinessInputs) -> ReadinessResult:
    root = inputs.data_root
    knowledge = root / "story_cache" / "persona" / "mash"
    art = root / "pet" / "mash" / "casual"
    checks: list[ReadinessCheck] = []
    hashes: dict[str, str] = {}

    profile_path = knowledge / "profile.json"
    try:
        profile = MashProfile.model_validate_json(profile_path.read_text(encoding="utf-8"))
        profile_ok = bool(profile.facts) and len(profile.summary) <= 400 and all(
            fact.source_path and fact.source_region for fact in profile.facts.values()
        )
        hashes["profile"] = _hash(profile_path)
    # Readiness is deliberately fail-closed for missing, stale, or malformed artifacts.
    except Exception:
        profile_ok = False
    checks.append(
        _check(
            "knowledge.profile",
            profile_ok,
            profile_path,
            "compact sourced profile is valid",
            "profile is missing, malformed, empty, oversized, or lacks provenance",
        )
    )

    knowledge_manifest_path = knowledge / "knowledge-manifest.json"
    try:
        manifest = json.loads(knowledge_manifest_path.read_text(encoding="utf-8"))
        index_ok = (
            manifest.get("story_index_schema_version") == 1
            and int(manifest.get("scene_count", 0)) > 0
            and (knowledge / "story.sqlite3").is_file()
        )
        hashes["knowledge_manifest"] = _hash(knowledge_manifest_path)
    except Exception:
        index_ok = False
    checks.append(
        _check(
            "knowledge.story_index",
            index_ok,
            knowledge_manifest_path,
            "FTS index is populated and schema-compatible",
            "story index is missing, empty, or incompatible",
        )
    )

    scenario_path = knowledge / "scenario-report.json"
    try:
        scenario = json.loads(scenario_path.read_text(encoding="utf-8"))
        results = scenario["results"]
        scenario_ok = scenario.get("scenario_count") == 11 and len(results) == 11
        scenario_ok = scenario_ok and all(
            int(item["estimated_tokens"]) <= 900
            and (
                item["route"] != "story"
                or (
                    2 <= int(item["window_count"]) <= 4
                    and not item["coverage_gap"]
                )
            )
            for item in results
        )
        hashes["scenario_report"] = _hash(scenario_path)
    except Exception:
        scenario_ok = False
    checks.append(
        _check(
            "knowledge.scenarios",
            scenario_ok,
            scenario_path,
            "eleven scenarios meet routing and context budgets",
            "scenario coverage, window count, or token budget failed",
        )
    )

    evidence_path = knowledge / "evidence.jsonl"
    try:
        evidence = [
            json.loads(line)
            for line in evidence_path.read_text(encoding="utf-8").splitlines()
            if line.strip()
        ]
        statuses = [item.get("review", {}).get("status") for item in evidence]
        evidence_ok = bool(statuses) and "approved" in statuses and all(
            status in {"approved", "rejected"} for status in statuses
        )
        hashes["evidence"] = _hash(evidence_path)
    except Exception:
        evidence_ok = False
    checks.append(
        _check(
            "persona.reviewed_evidence",
            evidence_ok,
            evidence_path,
            "all persona evidence has an explicit review decision",
            "persona evidence is missing or contains pending/invalid decisions",
        )
    )

    art_manifest_path = art / "manifest.json"
    art_hashes_ok = False
    art_count_ok = False
    try:
        art_manifest = json.loads(art_manifest_path.read_text(encoding="utf-8"))
        assets = art_manifest["assets"]
        expected_ids = {"full_body"} | {
            f"r{row:02d}c{column:02d}"
            for row in range(1, 8)
            for column in range(1, 5)
        }
        art_count_ok = (
            art_manifest.get("outfit_id") == "mash_casual_98001000"
            and {item["stable_id"] for item in assets} == expected_ids
            and len(assets) == 29
        )
        resolved_assets = [
            (
                item,
                _resolve_inside(art, item["raw_path"]),
                _resolve_inside(art, item["runtime_path"]),
            )
            for item in assets
        ]
        art_hashes_ok = art_count_ok and all(
            raw.is_file()
            and runtime.is_file()
            and _hash(raw) == item["raw_sha256"]
            and _hash(runtime) == item["runtime_sha256"]
            for item, raw, runtime in resolved_assets
        )
        source_path = Path(art_manifest["source"]["path"])
        art_hashes_ok = (
            art_hashes_ok
            and source_path.is_file()
            and source_path.resolve().is_relative_to(root.resolve())
            and _hash(source_path) == art_manifest["source"]["sha256"]
        )
        hashes["art_manifest"] = _hash(art_manifest_path)
    except Exception:
        pass
    checks.append(
        _check(
            "art.complete_bundle",
            art_count_ok,
            art_manifest_path,
            "default outfit contains full body and all 28 expressions",
            "art manifest is missing or does not contain the complete stable-ID set",
        )
    )
    checks.append(
        _check(
            "art.hashes",
            art_hashes_ok,
            art_manifest_path,
            "source, raw, and runtime hashes match",
            "one or more art files are missing or stale",
        )
    )

    qa_path = art / "qa-report.json"
    try:
        qa = json.loads(qa_path.read_text(encoding="utf-8"))
        qa_ok = qa.get("status") == "PASS" and not qa.get("errors")
        hashes["art_qa"] = _hash(qa_path)
    except Exception:
        qa_ok = False
    checks.append(
        _check(
            "art.automated_qa",
            qa_ok,
            qa_path,
            "automated art QA has zero errors",
            "automated art QA is missing or failed",
        )
    )
    checks.append(
        _check(
            "art.visual_qa",
            inputs.visual_qa == "approved",
            art / "contact-sheet.png",
            "contact sheet and representative crops were visually approved",
            "visual art review is pending or rejected",
        )
    )

    status = "PASS" if all(item.status == "PASS" for item in checks) else "BLOCKED"
    return ReadinessResult(status=status, checks=checks, artifact_hashes=hashes)
