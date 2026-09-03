from pathlib import Path

from PIL import Image, ImageDraw
import pytest

from fgo_pet_content.art.layout_spec import (
    LayoutExpectation,
    confirm_layout,
)
from fgo_pet_content.art.sheet import SheetLayoutError, analyze_sheet


FIXTURES = Path("tests/fixtures/art/layouts")


def _sheet(rows: int, columns: int) -> Image.Image:
    width = columns * 30
    image = Image.new("RGBA", (width, 20 + rows * 22), (255, 255, 255, 255))
    draw = ImageDraw.Draw(image)
    draw.rectangle((width // 2 - 15, 2, width // 2 + 14, 17), fill=(200, 120, 160, 255))
    for row in range(rows):
        top = 22 + row * 22
        draw.rectangle((0, top, width - 1, top + 17), fill=(45, 40, 50, 255))
    return image


def test_ambiguous_layout_requires_confirmation() -> None:
    proposal = analyze_sheet(
        _sheet(2, 3),
        LayoutExpectation(rows=None, columns=None),
    )

    assert proposal.status == "confirmation_required"
    with pytest.raises(SheetLayoutError, match="human confirmation"):
        proposal.to_layout_spec()


def test_explicit_grid_produces_confirmed_2x3_layout() -> None:
    proposal = analyze_sheet(
        _sheet(2, 3),
        LayoutExpectation(rows=2, columns=3),
    )

    layout = proposal.to_layout_spec()

    assert proposal.status == "ready"
    assert [item.stable_id for item in layout.expressions] == [
        "r01c01",
        "r01c02",
        "r01c03",
        "r02c01",
        "r02c02",
        "r02c03",
    ]
    assert layout.provenance.approval == "explicit_expectation"


def test_confirmation_file_promotes_ambiguous_proposal() -> None:
    proposal = analyze_sheet(
        _sheet(2, 3),
        LayoutExpectation(rows=None, columns=None),
    )

    layout = confirm_layout(proposal, FIXTURES / "alternate-2x3.json")

    assert len(layout.expressions) == 6
    assert layout.provenance.approval == "human_confirmation"
    assert layout.provenance.confirmed_by == "test-reviewer"


def test_confirmation_rejects_row_count_that_does_not_match_detection() -> None:
    proposal = analyze_sheet(
        _sheet(2, 3),
        LayoutExpectation(rows=None, columns=None),
    )

    with pytest.raises(SheetLayoutError, match="detected expression rows"):
        confirm_layout(proposal, FIXTURES / "mash-7x4.json")
