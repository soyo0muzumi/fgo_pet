from __future__ import annotations

import json

from .context import EvidenceWindow


SYSTEM_PROMPT = """你是 FGO 角色人设证据整理器。你只能依据本次提供的台词和元数据推断，不得补充外部知识。输出抽象的人格、关系、行为或语言习惯，不复述剧情。每项结论必须引用准确的 utterance_order；证据不足时返回空 cards。"""


def render_window(window: EvidenceWindow) -> str:
    payload = {
        "region": window.source.region.value,
        "script_id": window.source.script_id,
        "scene_index": window.scene_index,
        "target_orders": list(window.target_orders),
        "utterances": [
            {
                "utterance_order": item.order,
                "speaker": item.speaker,
                "servant_id": item.servant_id,
                "face_id": item.face_id,
                "text": item.text,
            }
            for item in window.utterances
        ],
    }
    return json.dumps(payload, ensure_ascii=False, indent=2)
