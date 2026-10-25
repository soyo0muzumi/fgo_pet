#!/usr/bin/env python3
"""Prepare deterministic A(v0.1) / B(v0.2) replay plans; NEVER calls a model."""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path


ROOT = Path(__file__).resolve().parents[3]
V01 = ROOT / "docs/experiments/mash-dialogue-agency"
V02 = ROOT / "docs/experiments/mash-dialogue-agency-v02"


def load(path: Path):
    return json.loads(path.read_text(encoding="utf-8"))


def stable_hash(value: object) -> str:
    body = json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":"))
    return hashlib.sha256(body.encode("utf-8")).hexdigest()


def prepare() -> dict:
    cases = load(V01 / "fixtures.json")["cases"]
    data = load(V02 / "grounding-cases.json")
    prompts = {
        "A_v01": (V01 / "candidate-prompt.md").read_text(encoding="utf-8"),
        "B_v02": (V02 / "candidate-prompt.md").read_text(encoding="utf-8"),
    }
    entries = []
    for case in cases:
        shared = {
            "history": case["history"],
            "user": case["user"],
            "host_facts": data["case_contexts"].get(case["id"]),
        }
        for arm, prompt in prompts.items():
            entries.append({
                "case_id": case["id"],
                "arm": arm,
                "shared_input_sha256": stable_hash(shared),
                "same_context_as_other_arm": True,
                "shared": shared,
                "candidate_system_instruction": prompt,
            })
    return {
        "schema_version": 1,
        "status": "input_plan_only_no_model_calls",
        "warning": "Not a production host envelope; inject shared host facts via trusted test host to both arms.",
        "case_count": len(cases),
        "request_plan_count": len(entries),
        "inputs": entries,
    }


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    plan = prepare()
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(plan, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"Prepared {plan['request_plan_count']} offline entries from {plan['case_count']} cases. No model calls.")


if __name__ == "__main__":
    main()
