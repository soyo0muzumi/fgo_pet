import json

from fgo_pet_content.models.source import ReviewStatus
from fgo_pet_content.reporting import build_review_report
from fgo_pet_content.review import review_card

from .test_review import make_card


def test_report_contains_claim_and_source_but_no_raw_dialogue() -> None:
    card = review_card(make_card(), ReviewStatus.APPROVED, notes="已核对")

    report = build_review_report([card], unknown_commands={"futureCommand": 2})
    serialized = json.dumps(report, ensure_ascii=False)

    assert card.claim in serialized
    assert "script-1" in serialized
    assert "这里是一整段剧情原文" not in serialized
    assert report["unknown_commands"] == {"futureCommand": 2}
