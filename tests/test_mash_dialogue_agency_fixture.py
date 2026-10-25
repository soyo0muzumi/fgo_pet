import json
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
FIXTURE = ROOT / "docs" / "experiments" / "mash-dialogue-agency" / "fixtures.json"
PROMPT = ROOT / "docs" / "experiments" / "mash-dialogue-agency" / "candidate-prompt.md"

def test_dialogue_agency_fixture_integrity():
    doc = json.loads(FIXTURE.read_text(encoding="utf-8"))
    assert doc["schema_version"] == 1
    cases = doc["cases"]
    assert len(cases) >= 12
    assert len({case["id"] for case in cases}) == len(cases)
    assert {"agency", "honesty", "continuity", "adaptation", "utility"}.issubset(
        {case["category"] for case in cases}
    )
    for case in cases:
        assert case["user"].strip() and case["intent"].strip() and case["notes"].strip()
        assert all(turn["role"] in {"user", "assistant"} and turn["content"].strip()
                   for turn in case["history"])
        assert isinstance(case["checks"]["require_concrete_view"], bool)
        assert isinstance(case["checks"]["allow_ability_explanation"], bool)
        assert 0 <= case["checks"]["max_forced_questions"] <= 1
    prompt = PROMPT.read_text(encoding="utf-8")
    assert "不得声称自己在会话间持续思考" in prompt
    assert "不强制每轮结尾发问" in prompt
