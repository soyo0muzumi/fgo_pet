from __future__ import annotations

from .models.story import StoryDocument


def render_story_markdown(
    document: StoryDocument, *, chapter_title: str
) -> str:
    source = document.source
    section_title = source.container_name or source.script_id
    lines = [
        f"# {chapter_title} — {section_title}",
        "",
        f"- 脚本 ID：`{source.script_id}`",
        f"- 语言：`{source.region.value}`",
        "",
    ]
    for scene in document.scenes:
        lines.extend((f"## 场景 {scene.scene_index}", ""))
        for utterance in scene.utterances:
            speaker = utterance.speaker or "旁白"
            text = utterance.text.replace("\n", "  \n")
            lines.extend((f"**{speaker}**：{text}", ""))
    return "\n".join(lines).rstrip() + "\n"
