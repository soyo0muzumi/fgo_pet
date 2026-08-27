from fgo_pet_content.corpus import (
    DEFAULT_STORY_ARCS,
    WarScript,
    enumerate_war_scripts,
    load_regional_scripts,
    merge_region_scripts,
)
from fgo_pet_content.models.source import Region


def test_default_story_arcs_match_user_selected_main_story() -> None:
    assert [arc.war_id for arc in DEFAULT_STORY_ARCS] == [
        100,
        107,
        108,
        300,
        308,
        311,
        405,
        407,
    ]


def test_war_scripts_include_opening_and_all_quest_phase_scripts() -> None:
    payload = {
        "id": 107,
        "name": "第七特異点",
        "longName": "第七特異点\n绝对魔兽战线",
        "scriptId": "WarOpening107",
        "script": "https://example.test/WarOpening107.txt",
        "spots": [
            {
                "id": 10701,
                "name": "序节",
                "quests": [
                    {
                        "id": 1070001,
                        "name": "第1节",
                        "chapterId": 1,
                        "chapterSubId": 0,
                        "chapterSubStr": "",
                        "phaseScripts": [
                            {
                                "phase": 1,
                                "scripts": [
                                    {
                                        "scriptId": "0100070010",
                                        "script": "https://example.test/0100070010.txt",
                                    }
                                ],
                            },
                            {
                                "phase": 2,
                                "scripts": [
                                    {
                                        "scriptId": "0100070010",
                                        "script": "https://example.test/0100070010.txt",
                                    },
                                    {
                                        "scriptId": "0100070011",
                                        "script": "https://example.test/0100070011.txt",
                                    },
                                ],
                            },
                        ],
                    }
                ],
            }
        ],
    }

    scripts = enumerate_war_scripts(payload)

    assert [item.script_id for item in scripts] == [
        "WarOpening107",
        "0100070010",
        "0100070011",
    ]
    assert scripts[1].quest_name == "第1节"
    assert scripts[1].phases == (1, 2)


def test_placeholder_opening_script_is_ignored() -> None:
    scripts = enumerate_war_scripts(
        {
            "id": 100,
            "name": "特异点F",
            "scriptId": "0",
            "script": "https://example.test/0.txt",
            "spots": [],
        }
    )

    assert scripts == []


def test_region_merge_prefers_cn_and_keeps_jp_only_scripts() -> None:
    cn = [
        WarScript(407, "终章", "shared", "https://cn/shared", quest_id=1)
    ]
    jp = [
        WarScript(407, "終章", "shared", "https://jp/shared", quest_id=1),
        WarScript(407, "終章", "jp-only", "https://jp/only", quest_id=2),
    ]

    merged = merge_region_scripts(cn, jp)

    assert [(item.script.script_id, item.region) for item in merged] == [
        ("shared", Region.CN),
        ("jp-only", Region.JP),
    ]
    assert merged[0].script.script_url == "https://cn/shared"


def test_load_regional_scripts_accepts_missing_cn_war() -> None:
    class JPOnlyAtlas:
        def fetch_war(self, region: Region, war_id: int):
            if region is Region.CN:
                return None
            return {
                "id": war_id,
                "name": "終章",
                "scriptId": "WarOpening407",
                "script": "https://jp/opening",
                "spots": [],
            }

    scripts = load_regional_scripts(JPOnlyAtlas(), 407)

    assert len(scripts) == 1
    assert scripts[0].region is Region.JP
