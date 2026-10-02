import json
import re
from pathlib import Path


def load_expression_labels(path: Path) -> dict[str, str]:
    payload = json.loads(path.read_text(encoding="utf-8"))
    if payload.get("schema_version") != 1:
        raise ValueError("unsupported expression label schema")
    expressions = payload.get("expressions")
    if not isinstance(expressions, dict):
        raise ValueError("expressions must be an object")
    expected = {
        f"r{row:02d}c{column:02d}"
        for row in range(1, 8)
        for column in range(1, 5)
    }
    if set(expressions) != expected or any(
        not re.fullmatch(r"r0[1-7]c0[1-4]", stable_id)
        for stable_id in expressions
    ):
        raise ValueError("expression labels must cover the exact 7x4 grid")
    labels = {
        stable_id: str(item.get("label", "")).strip()
        if isinstance(item, dict)
        else ""
        for stable_id, item in expressions.items()
    }
    if any(not label for label in labels.values()) or len(set(labels.values())) != 28:
        raise ValueError("expression labels must be non-empty and unique")
    return labels
