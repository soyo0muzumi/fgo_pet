from __future__ import annotations

from dataclasses import dataclass

from ..models.source import SourceRef
from ..models.story import StoryDocument, Utterance


@dataclass(frozen=True, slots=True)
class EvidenceWindow:
    source: SourceRef
    scene_index: int
    target_orders: tuple[int, ...]
    utterances: tuple[Utterance, ...]


def build_evidence_windows(
    document: StoryDocument,
    servant_id: int,
    neighbor_lines: int = 3,
) -> list[EvidenceWindow]:
    windows: list[EvidenceWindow] = []
    for scene in document.scenes:
        for index, utterance in enumerate(scene.utterances):
            if utterance.servant_id != servant_id:
                continue
            start = max(0, index - neighbor_lines)
            end = min(len(scene.utterances), index + neighbor_lines + 1)
            windows.append(
                EvidenceWindow(
                    source=document.source,
                    scene_index=scene.scene_index,
                    target_orders=(utterance.order,),
                    utterances=tuple(scene.utterances[start:end]),
                )
            )
    return windows
