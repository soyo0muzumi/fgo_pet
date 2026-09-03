from PIL import Image, ImageDraw
import pytest

from fgo_pet_content.art.layout_spec import LayoutExpectation
from fgo_pet_content.art.sheet import SheetLayoutError, analyze_sheet


def _sheet(row_count: int = 7) -> Image.Image:
    image = Image.new("RGBA", (120, 20 + row_count * 22), (255, 255, 255, 255))
    draw = ImageDraw.Draw(image)
    draw.rectangle((45, 2, 74, 17), fill=(200, 120, 160, 255))
    for row in range(row_count):
        top = 22 + row * 22
        draw.rectangle((0, top, 119, top + 17), fill=(45, 40, 50, 255))
        for column in range(4):
            left = column * 30 + 8
            draw.rectangle((left, top + 2, left + 12, top + 15), fill=(220, 170, 190, 255))
    return image


def test_detects_full_body_and_row_major_expression_grid() -> None:
    layout = analyze_sheet(_sheet())

    assert layout.full_body.left == 45
    assert layout.full_body.right == 75
    assert list(layout.expressions) == [
        f"r{row:02d}c{column:02d}"
        for row in range(1, 8)
        for column in range(1, 5)
    ]
    assert layout.expressions["r01c01"].left == 0
    assert layout.expressions["r07c04"].right == 120


def test_rejects_sheet_without_exactly_seven_expression_rows() -> None:
    with pytest.raises(SheetLayoutError, match="seven expression rows"):
        analyze_sheet(_sheet(row_count=6))


def test_explicit_expectation_rejects_observed_row_mismatch() -> None:
    with pytest.raises(SheetLayoutError, match="expected 3 expression rows"):
        analyze_sheet(_sheet(row_count=2), LayoutExpectation(rows=3, columns=4))
