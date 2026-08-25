from pathlib import Path

import pytest

from fgo_pet_content.models.source import Region, SourceRef
from fgo_pet_content.parser import parse_story


@pytest.fixture
def fixture_text() -> str:
    return Path(
        "tests/fixtures/scripts/CN/0200040010_excerpt.txt"
    ).read_text(encoding="utf-8")


@pytest.fixture
def source() -> SourceRef:
    return SourceRef(
        region=Region.CN,
        script_id="0200040010",
        container_type="war_opening",
        container_id=204,
        content_hash="sha256:test",
    )


def test_face_and_actor_state_are_attached_to_utterance(
    fixture_text: str, source: SourceRef
) -> None:
    document = parse_story(fixture_text, source, {"98001000": 800100})
    line = document.scenes[0].utterances[0]

    assert line.speaker == "玛修"
    assert line.actor_slot == "B"
    assert line.servant_id == 800100
    assert line.figure_id == "98001000"
    assert line.face_id == 13
    assert line.text == "早上好。\n前辈。"
    assert (line.raw_start_line, line.raw_end_line) == (6, 9)


def test_unknown_command_is_preserved_without_losing_dialogue(
    fixture_text: str, source: SourceRef
) -> None:
    document = parse_story(fixture_text, source, {})

    assert document.unknown_commands[0].name == "futureCommand"
    assert document.unknown_commands[0].line_number == 8
    assert document.scenes[0].utterances[0].text == "早上好。\n前辈。"


def test_branch_and_updated_face_apply_to_following_utterance(
    fixture_text: str, source: SourceRef
) -> None:
    document = parse_story(fixture_text, source, {})
    line = document.scenes[0].utterances[1]

    assert line.order == 2
    assert line.face_id == 7
    assert line.branch_path == ["choice-a"]


def test_script_without_scene_creates_implicit_scene(source: SourceRef) -> None:
    document = parse_story("＠旁白\n测试。\n[k]\n", source, {})

    assert document.scenes[0].scene_index == 1
    assert document.scenes[0].background_id is None
    assert document.scenes[0].utterances[0].speaker == "旁白"
