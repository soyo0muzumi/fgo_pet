from __future__ import annotations

from collections import defaultdict

from .master_tables import MasterTableReader
from .models.source import Region, SourceRef


class SourceCatalog:
    def __init__(self, reader: MasterTableReader, region: Region) -> None:
        self._region = region
        self._by_script_id: dict[str, list[SourceRef]] = defaultdict(list)
        self.unresolved_script_ids: list[str] = []
        self._index_direct_links(reader, "mstWar", "war_opening")
        self._index_direct_links(reader, "mstQuest", "quest")
        self._index_direct_links(reader, "mstEvent", "event")
        self._index_direct_links(reader, "mstSvtScript", "interlude")

    @classmethod
    def from_master_root(
        cls, root, region: Region = Region.JP
    ) -> SourceCatalog:
        return cls(MasterTableReader(root), region)

    def resolve(self, script_id: str) -> list[SourceRef]:
        refs = list(self._by_script_id.get(script_id, []))
        if not refs and script_id not in self.unresolved_script_ids:
            self.unresolved_script_ids.append(script_id)
        return refs

    def _index_direct_links(
        self, reader: MasterTableReader, table_name: str, container_type: str
    ) -> None:
        for row in reader.read_optional(table_name):
            script_id = row.get("scriptId")
            if not script_id:
                continue
            container_id = row.get("id")
            self._by_script_id[str(script_id)].append(
                SourceRef(
                    region=self._region,
                    script_id=str(script_id),
                    container_type=container_type,
                    container_id=int(container_id) if container_id is not None else None,
                    container_name=row.get("name") or row.get("longName"),
                )
            )
