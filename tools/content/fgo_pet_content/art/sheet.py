from __future__ import annotations

from dataclasses import dataclass

from PIL import Image

from .layout_spec import LayoutExpectation, LayoutProposal, SheetLayoutError
from .models import Rect, Size


@dataclass(frozen=True, slots=True)
class SheetLayout:
    full_body: Rect
    expressions: dict[str, Rect]


def _is_background(pixel: tuple[int, int, int, int]) -> bool:
    red, green, blue, alpha = pixel
    return alpha == 0 or (red >= 245 and green >= 245 and blue >= 245)


def _content_intervals(image: Image.Image) -> list[tuple[int, int]]:
    rgba = image.convert("RGBA")
    width, height = rgba.size
    content_rows = []
    for y in range(height):
        foreground = sum(
            not _is_background(rgba.getpixel((x, y))) for x in range(width)
        )
        content_rows.append(foreground / width > 0.02)

    intervals: list[tuple[int, int]] = []
    start: int | None = None
    for y, has_content in enumerate(content_rows + [False]):
        if has_content and start is None:
            start = y
        elif not has_content and start is not None:
            intervals.append((start, y))
            start = None
    return intervals


def analyze_sheet(
    image: Image.Image,
    expected: LayoutExpectation | None = None,
) -> SheetLayout | LayoutProposal:
    rgba = image.convert("RGBA")
    width, height = rgba.size
    intervals = _content_intervals(rgba)
    if expected is None and len(intervals) != 8:
        raise SheetLayoutError(
            f"expected one full-body area and seven expression rows; found {len(intervals)} areas"
        )
    if not intervals:
        raise SheetLayoutError("sheet has no detected content areas")
    expression_intervals = intervals[1:]
    if expected is not None and expected.rows is not None and len(expression_intervals) != expected.rows:
        raise SheetLayoutError(
            f"expected {expected.rows} expression rows; found {len(expression_intervals)}"
        )

    full_top, full_bottom = intervals[0]
    pixels = rgba.load()
    points = [
        (x, y)
        for y in range(full_top, full_bottom)
        for x in range(width)
        if not _is_background(pixels[x, y])
    ]
    if not points:
        raise SheetLayoutError("full-body area is empty")
    full_body = Rect(
        left=min(x for x, _ in points),
        top=min(y for _, y in points),
        right=max(x for x, _ in points) + 1,
        bottom=max(y for _, y in points) + 1,
    )

    if expected is not None:
        ready = expected.rows is not None and expected.columns is not None
        return LayoutProposal(
            source_size=Size(width=width, height=height),
            full_body=full_body,
            expression_intervals=tuple(expression_intervals),
            columns=expected.columns,
            status="ready" if ready else "confirmation_required",
        )

    expressions: dict[str, Rect] = {}
    for row, (top, bottom) in enumerate(expression_intervals, start=1):
        for column in range(1, 5):
            left = (column - 1) * width // 4
            right = column * width // 4
            expressions[f"r{row:02d}c{column:02d}"] = Rect(
                left=left,
                top=top,
                right=right,
                bottom=bottom,
            )
    return SheetLayout(full_body=full_body, expressions=expressions)
