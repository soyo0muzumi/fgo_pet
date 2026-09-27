from __future__ import annotations

from dataclasses import dataclass
import json
from pathlib import Path
from typing import Literal

from pydantic import BaseModel, ConfigDict, Field

from .models import Rect, Size


class SheetLayoutError(ValueError):
    pass


class LayoutExpectation(BaseModel):
    model_config = ConfigDict(extra="forbid", frozen=True)

    rows: int | None = Field(default=None, gt=0)
    columns: int | None = Field(default=None, gt=0)


class ExpressionRectangle(BaseModel):
    model_config = ConfigDict(extra="forbid", frozen=True)

    stable_id: str = Field(pattern=r"^r[0-9]{2}c[0-9]{2}$")
    rect: Rect


class LayoutProvenance(BaseModel):
    model_config = ConfigDict(extra="forbid", frozen=True)

    detector: Literal["content_intervals"] = "content_intervals"
    approval: Literal["explicit_expectation", "human_confirmation"]
    confirmed_by: str | None = None


class LayoutSpec(BaseModel):
    model_config = ConfigDict(extra="forbid", frozen=True)

    schema_version: Literal[1] = 1
    source_size: Size
    full_body: Rect
    expressions: tuple[ExpressionRectangle, ...] = Field(min_length=1)
    provenance: LayoutProvenance


class LayoutConfirmation(BaseModel):
    model_config = ConfigDict(extra="forbid", frozen=True)

    schema_version: Literal[1]
    rows: int = Field(gt=0)
    columns: int = Field(gt=0)
    confirmed_by: str = Field(min_length=1)


@dataclass(frozen=True, slots=True)
class LayoutProposal:
    source_size: Size
    full_body: Rect
    expression_intervals: tuple[tuple[int, int], ...]
    columns: int | None
    status: Literal["ready", "confirmation_required"]

    def to_layout_spec(self) -> LayoutSpec:
        if self.status != "ready" or self.columns is None:
            raise SheetLayoutError("layout requires human confirmation")
        return _build_layout(
            self,
            columns=self.columns,
            provenance=LayoutProvenance(approval="explicit_expectation"),
        )


def confirm_layout(
    proposal: LayoutProposal,
    confirmation_file: str | Path,
) -> LayoutSpec:
    path = Path(confirmation_file)
    confirmation = LayoutConfirmation.model_validate(
        json.loads(path.read_text(encoding="utf-8"))
    )
    detected_rows = len(proposal.expression_intervals)
    if confirmation.rows != detected_rows:
        raise SheetLayoutError(
            f"confirmation declares {confirmation.rows} rows but detector found "
            f"{detected_rows} detected expression rows"
        )
    return _build_layout(
        proposal,
        columns=confirmation.columns,
        provenance=LayoutProvenance(
            approval="human_confirmation",
            confirmed_by=confirmation.confirmed_by,
        ),
    )


def _build_layout(
    proposal: LayoutProposal,
    *,
    columns: int,
    provenance: LayoutProvenance,
) -> LayoutSpec:
    width = proposal.source_size.width
    expressions = tuple(
        ExpressionRectangle(
            stable_id=f"r{row:02d}c{column:02d}",
            rect=Rect(
                left=(column - 1) * width // columns,
                top=top,
                right=column * width // columns,
                bottom=bottom,
            ),
        )
        for row, (top, bottom) in enumerate(proposal.expression_intervals, start=1)
        for column in range(1, columns + 1)
    )
    return LayoutSpec(
        source_size=proposal.source_size,
        full_body=proposal.full_body,
        expressions=expressions,
        provenance=provenance,
    )
