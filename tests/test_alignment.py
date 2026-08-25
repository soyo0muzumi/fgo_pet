from fgo_pet_content.alignment import align_documents
from fgo_pet_content.models.source import Region, SourceRef, TranslationStatus
from fgo_pet_content.models.story import StoryDocument, StoryScene, Utterance


def make_document(region: Region, speakers: list[str]) -> StoryDocument:
    source = SourceRef(
        region=region,
        script_id="same-script",
        container_type="quest",
        container_id=10,
        content_hash=f"sha256:{region.value}",
    )
    return StoryDocument(
        source=source,
        scenes=[
            StoryScene(
                scene_index=1,
                background_id="100",
                utterances=[
                    Utterance(
                        order=index,
                        speaker=speaker,
                        servant_id=800100 if "Mash" in speaker else None,
                        actor_slot="B" if "Mash" in speaker else "A",
                        text=f"line-{index}",
                        raw_start_line=index,
                        raw_end_line=index,
                    )
                    for index, speaker in enumerate(speakers, start=1)
                ],
            )
        ],
    )


def test_same_script_actor_sequence_aligns_utterances() -> None:
    cn_document = make_document(Region.CN, ["Mash-CN", "Other"])
    jp_document = make_document(Region.JP, ["Mash-JP", "Other"])

    result = align_documents(cn_document, jp_document)

    assert result.pairs[0].cn_order == 1
    assert result.pairs[0].jp_order == 1
    assert result.pairs[0].status is TranslationStatus.OFFICIAL_CN
    assert result.status is TranslationStatus.OFFICIAL_CN


def test_divergent_branch_is_flagged_instead_of_forced() -> None:
    cn_document = make_document(Region.CN, ["Mash-CN", "Other", "Third"])
    jp_document = make_document(Region.JP, ["Mash-JP", "Other"])

    result = align_documents(cn_document, jp_document)

    assert result.unmatched
    assert result.status in {
        TranslationStatus.ALIGNMENT_UNCERTAIN,
        TranslationStatus.CN_JP_DIVERGENCE,
    }


def test_different_script_ids_are_rejected() -> None:
    cn_document = make_document(Region.CN, ["Mash-CN"])
    jp_document = make_document(Region.JP, ["Mash-JP"])
    jp_document.source.script_id = "other-script"

    try:
        align_documents(cn_document, jp_document)
    except ValueError as error:
        assert "same script" in str(error)
    else:
        raise AssertionError("different script IDs must not align")
