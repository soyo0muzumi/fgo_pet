from __future__ import annotations

import json
from pathlib import Path


class MasterTableError(ValueError):
    pass


class MasterTableReader:
    def __init__(self, root: Path) -> None:
        self.root = root.resolve()

    def read(self, name: str) -> list[dict]:
        if Path(name).name != name:
            raise MasterTableError("master table name must not contain a path")
        path = self.root / name
        raw = path.read_text(encoding="utf-8-sig")
        first = raw.lstrip()[:1]
        if first not in {"[", "{"}:
            raise MasterTableError(f"{name} does not contain JSON")
        try:
            data = json.loads(raw)
        except json.JSONDecodeError as error:
            raise MasterTableError(f"{name} contains invalid JSON") from error
        if not isinstance(data, list) or not all(isinstance(row, dict) for row in data):
            raise MasterTableError(f"{name} must contain an array of objects")
        return data

    def read_optional(self, name: str) -> list[dict]:
        if not (self.root / name).is_file():
            return []
        return self.read(name)
