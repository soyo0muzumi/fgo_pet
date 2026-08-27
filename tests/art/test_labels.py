import json
from pathlib import Path

import pytest

from fgo_pet_content.art.labels import load_expression_labels


def test_checked_in_labels_cover_the_complete_unique_grid() -> None:
    labels = load_expression_labels(
        Path("content/servants/mash/casual-expression-labels.json")
    )

    assert set(labels) == {
        f"r{row:02d}c{column:02d}"
        for row in range(1, 8)
        for column in range(1, 5)
    }
    assert len(set(labels.values())) == 28


def test_label_loader_rejects_unknown_or_duplicate_entries(tmp_path: Path) -> None:
    path = tmp_path / "labels.json"
    path.write_text(
        json.dumps(
            {
                "schema_version": 1,
                "expressions": {
                    "r01c01": {"label": "微笑"},
                    "face-2": {"label": "微笑"},
                },
            },
            ensure_ascii=False,
        ),
        encoding="utf-8",
    )

    with pytest.raises(ValueError):
        load_expression_labels(path)
