import pytest

from fgo_pet_content.models.source import Region
from fgo_pet_content.profile import ProfileUnavailable, build_profile


CN_LORE = {
    "id": 800100,
    "collectionNo": 1,
    "name": "  玛修·基列莱特  ",
    "className": "Shielder",
    "profile": {
        "character": "<b>迦勒底的亚从者。</b>\n认真而温柔。",
        "likes": "",
        "dislikes": "伤害前辈的人",
        "comments": [{"comment": "  会把御主称为前辈。 "}],
    },
}

JP_LORE = {
    "id": 800100,
    "collectionNo": 1,
    "name": "マシュ・キリエライト",
    "className": "シールダー",
    "profile": {
        "character": "カルデアのデミ・サーヴァント。",
        "likes": "読書",
        "dislikes": "先輩を傷つける者",
    },
}


def test_build_profile_prefers_cn_and_marks_jp_field_fallback() -> None:
    profile = build_profile(CN_LORE, JP_LORE, servant_id=800100)

    assert profile.name == "玛修·基列莱特"
    assert profile.facts["character"].value == "迦勒底的亚从者。 认真而温柔。"
    assert profile.facts["character"].source_region is Region.CN
    assert profile.facts["likes"].value == "読書"
    assert profile.facts["likes"].source_region is Region.JP
    assert profile.facts["likes"].jp_fallback is True
    assert profile.facts["likes"].source_path == "profile.likes"


def test_build_profile_flattens_comments_and_records_hashes() -> None:
    profile = build_profile(CN_LORE, JP_LORE, servant_id=800100)

    assert profile.facts["comments"].value == "会把御主称为前辈。"
    assert set(profile.source_hashes) == {"CN", "JP"}
    assert all(value.startswith("sha256:") for value in profile.source_hashes.values())


def test_build_profile_summary_ends_on_sentence_boundary_within_budget() -> None:
    cn = {
        **CN_LORE,
        "profile": {
            **CN_LORE["profile"],
            "character": "。".join(["认真而温柔" * 30] * 30) + "。",
        },
    }

    profile = build_profile(cn, JP_LORE, servant_id=800100)

    assert len(profile.summary) <= 1200
    assert profile.summary.endswith("。")


def test_build_profile_rejects_missing_profile_data() -> None:
    with pytest.raises(ProfileUnavailable):
        build_profile(
            {"id": 800100, "collectionNo": 1, "name": "玛修"},
            {"id": 800100, "collectionNo": 1, "name": "マシュ"},
            servant_id=800100,
        )
