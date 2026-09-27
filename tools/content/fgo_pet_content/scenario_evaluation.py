from __future__ import annotations

import json
from pathlib import Path

from .cache import atomic_write
from .profile import MashProfile
from .retrieval import compose_context


def evaluate_scenarios(
    cases_path: Path,
    profile_path: Path,
    database: Path,
    destination: Path,
) -> dict:
    cases = json.loads(cases_path.read_text(encoding="utf-8"))
    profile = MashProfile.model_validate_json(profile_path.read_text(encoding="utf-8"))
    results = []
    for case in cases:
        context = compose_context(case["input"], profile, database)
        results.append(
            {
                "id": case["id"],
                "category": case["category"],
                "route": context.route,
                "route_reasons": list(context.route_reasons),
                "window_count": len(context.story_windows),
                "scene_ids": [hit.scene_id for hit in context.story_windows],
                "estimated_tokens": context.estimated_tokens,
                "coverage_gap": context.coverage_gap,
                "default_sentence_limit": case["max_sentences"],
                "supported": context.route != "story" or not context.coverage_gap,
            }
        )
    report = {
        "schema_version": 1,
        "scenario_count": len(results),
        "results": results,
    }
    atomic_write(
        destination,
        json.dumps(report, ensure_ascii=False, indent=2).encode("utf-8"),
    )
    return report
