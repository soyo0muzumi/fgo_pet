"""Validate experiment assets without model calls, running the app or accessing user data."""
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
V01 = ROOT / "docs" / "experiments" / "mash-dialogue-agency"
V02 = ROOT / "docs" / "experiments" / "mash-dialogue-agency-v02"


def load(path):
    return json.loads(path.read_text(encoding="utf-8"))


def test_prompt_is_short_and_preserves_boundaries():
    new = (V02 / "candidate-prompt.md").read_text(encoding="utf-8")
    old = (V01 / "candidate-prompt.md").read_text(encoding="utf-8")
    assert len(new) < len(old)
    assert "具体且可修正" in new
    assert "过去读过" in new
    assert "没有宿主状态不能断言" in new
    assert "不授予后台运行" in new


def test_common_host_facts_are_reproducible_and_scoped():
    old = load(V01 / "fixtures.json")
    new = load(V02 / "grounding-cases.json")
    cases = {c["id"] for c in old["cases"]}
    assert new["schema_version"] == 1
    assert set(new["case_contexts"]).issubset(cases)
    assert {"T05_capability_direct", "T06_unobserved_reading",
            "T08_share_progress", "T11_resume_topic",
            "T12_explicit_analysis"}.issubset(new["case_contexts"])
    for case in new["case_contexts"].values():
        assert case["role"] == "trusted_test_host_facts"
        assert len(case["facts"]) >= 1
        assert all(isinstance(f, str) and f.strip() for f in case["facts"])


def test_continuity_does_not_bypass_review_or_prewrite_model_output():
    e = load(V02 / "continuity-cases.json")
    assert e["schema_version"] == 1
    assert len(e["sessions"]) == 4
    assert {a["id"] for a in e["experimental_conditions"]} == {
        "context_only", "stored_self"}
    assert all(s["user"] and s["after_turn"] for s in e["sessions"])
    gates = " ".join(e["gating"])
    assert "不得捏造条目" in gates
    assert "不能从隐藏历史" in gates
    assert "不得为实验直接打开访问权限" in gates


def test_offline_plan_keeps_pairs_identical_except_instruction():
    import importlib.util
    path = ROOT / "tools" / "spikes" / "mash-dialogue-agency-v02" / "prepare.py"
    spec = importlib.util.spec_from_file_location("mash_agency_v02_prepare", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    plan = module.prepare()
    fixtures = load(V01 / "fixtures.json")
    assert plan["status"] == "input_plan_only_no_model_calls"
    assert plan["request_plan_count"] == len(fixtures["cases"]) * 2
    for index in range(0, len(plan["inputs"]), 2):
        a, b = plan["inputs"][index:index + 2]
        assert a["arm"] == "A_v01" and b["arm"] == "B_v02"
        assert a["shared"] == b["shared"]
        assert a["shared_input_sha256"] == b["shared_input_sha256"]
        assert a["candidate_system_instruction"] != b["candidate_system_instruction"]
