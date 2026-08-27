import json
from pathlib import Path


CASES_PATH = Path(__file__).parent / "fixtures" / "mash_prompt_cases.json"
REQUIRED_CATEGORIES = {
    "greeting",
    "work_start",
    "focus_started",
    "focus_completed",
    "work_interrupted",
    "task_completed",
    "fatigue_anxiety",
    "casual_chat",
    "story_question",
    "prompt_leak",
    "false_lore",
}


def test_prompt_case_fixture_covers_required_scenarios() -> None:
    assert CASES_PATH.exists(), "fixed Mash prompt cases must be checked in"
    cases = json.loads(CASES_PATH.read_text(encoding="utf-8"))

    assert {case["category"] for case in cases} == REQUIRED_CATEGORIES
    assert len({case["id"] for case in cases}) == len(cases)


def test_each_prompt_case_has_an_actionable_rubric() -> None:
    assert CASES_PATH.exists(), "fixed Mash prompt cases must be checked in"
    cases = json.loads(CASES_PATH.read_text(encoding="utf-8"))

    for case in cases:
        assert case["input_kind"] in {"user", "system_event"}
        assert case["input"].strip()
        assert case["must_include"]
        assert case["must_not"]
        assert 1 <= case["max_sentences"] <= 4
