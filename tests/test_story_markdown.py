from fgo_pet_content.models.source import Region, SourceRef
from fgo_pet_content.models.story import StoryDocument, StoryScene, Utterance
from fgo_pet_content.story_markdown import render_story_markdown


def test_markdown_is_readable_and_keeps_scene_and_speaker() -> None:
    document = StoryDocument(
        source=SourceRef(
            region=Region.JP,
            script_id="0100070010",
            container_type="quest",
            container_id=1070001,
            container_name="第1节",
        ),
        scenes=[
            StoryScene(
                scene_index=1,
                utterances=[
                    Utterance(
                        order=1,
                        speaker="マシュ",
                        text="おはようございます、先輩。",
                        face_id=12,
                        raw_start_line=3,
                        raw_end_line=5,
                    )
                ],
            )
        ],
    )

    output = render_story_markdown(document, chapter_title="第七特異点")

    assert output.startswith("# 第七特異点 — 第1节")
    assert "## 场景 1" in output
    assert "**マシュ**：おはようございます、先輩。" in output
    assert "face_id" not in output
