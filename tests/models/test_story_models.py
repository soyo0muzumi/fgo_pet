from fgo_pet_content.models.source import Region, SourceRef
from fgo_pet_content.models.story import StoryDocument, StoryScene, Utterance


def test_story_document_round_trips_source_and_utterance() -> None:
    source = SourceRef(
        region=Region.CN,
        script_id="0200040010",
        container_type="war_opening",
        container_id=204,
        content_hash="sha256:abc",
    )
    document = StoryDocument(
        source=source,
        scenes=[
            StoryScene(
                scene_index=1,
                background_id="60300",
                utterances=[
                    Utterance(
                        order=1,
                        speaker="玛修",
                        actor_slot="B",
                        servant_id=800100,
                        figure_id="98001000",
                        face_id=13,
                        text="早上好。",
                        raw_start_line=10,
                        raw_end_line=12,
                    )
                ],
            )
        ],
    )

    restored = StoryDocument.model_validate_json(document.model_dump_json())

    assert restored.source.region is Region.CN
    assert restored.scenes[0].utterances[0].servant_id == 800100


def test_story_document_uses_independent_list_defaults() -> None:
    source = SourceRef(
        region=Region.JP,
        script_id="x",
        container_type="unknown",
        content_hash="sha256:def",
    )
    first = StoryDocument(source=source)
    second = StoryDocument(source=source)

    first.scenes.append(StoryScene(scene_index=1))

    assert second.scenes == []
