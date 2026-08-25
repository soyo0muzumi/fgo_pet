from __future__ import annotations

from collections.abc import Mapping

from ..models.source import SourceRef
from ..models.story import CastMember, StoryDocument, UnknownCommand, Utterance
from .commands import normalize_dialogue, parse_command
from .state import ParserState


IGNORED_COMMANDS = {
    "bgm",
    "bgmStop",
    "cameraMove",
    "charaFadein",
    "charaFadeout",
    "charaFilter",
    "charaMove",
    "charaMoveReturn",
    "charaShake",
    "effect",
    "end",
    "f",
    "fadein",
    "fadeout",
    "line",
    "messageOff",
    "se",
    "seStop",
    "seVolume",
    "soundStopAll",
    "wait",
    "wipeFilter",
    "wipeOff",
    "wipein",
    "wipeout",
    "wt",
}


def parse_story(
    text: str,
    source: SourceRef,
    figure_to_servant: Mapping[str, int],
) -> StoryDocument:
    state = ParserState()
    unknown_commands: list[UnknownCommand] = []
    speaker: str | None = None
    dialogue_parts: list[str] = []
    dialogue_start = 0
    utterance_order = 0

    for line_number, raw_line in enumerate(text.splitlines(), start=1):
        line = raw_line.strip()
        if line.startswith("＠"):
            speaker = line.removeprefix("＠").strip()
            dialogue_parts = []
            dialogue_start = line_number
            continue

        command = parse_command(line)
        if command is not None:
            if command.name == "k" and speaker is not None:
                utterance_order += 1
                slot = state.current_talk_slot
                cast_member = state.cast.get(slot) if slot is not None else None
                state.current_scene().utterances.append(
                    Utterance(
                        order=utterance_order,
                        speaker=speaker,
                        actor_slot=slot,
                        servant_id=cast_member.servant_id if cast_member else None,
                        figure_id=cast_member.figure_id if cast_member else None,
                        face_id=state.faces.get(slot) if slot else None,
                        text=normalize_dialogue(dialogue_parts),
                        branch_path=list(state.branch_path),
                        raw_start_line=dialogue_start,
                        raw_end_line=line_number,
                    )
                )
                speaker = None
                dialogue_parts = []
            elif command.name == "charaSet":
                parts = command.raw_arguments.split(maxsplit=3)
                if len(parts) >= 2:
                    slot, figure_id = parts[:2]
                    display_name = parts[3] if len(parts) == 4 else slot
                    state.cast[slot] = CastMember(
                        display_name=display_name,
                        figure_id=figure_id,
                        servant_id=figure_to_servant.get(figure_id),
                    )
            elif command.name == "charaTalk" and command.arguments:
                state.current_talk_slot = command.arguments[0]
            elif command.name == "charaFace" and len(command.arguments) >= 2:
                try:
                    state.faces[command.arguments[0]] = int(command.arguments[1])
                except ValueError:
                    _preserve_unknown(unknown_commands, command.name, command.arguments, line_number, raw_line)
            elif command.name == "scene":
                background_id = command.arguments[0] if command.arguments else None
                state.start_scene(background_id)
            elif command.name == "branchPath":
                state.branch_path = list(command.arguments)
            elif command.name not in IGNORED_COMMANDS:
                _preserve_unknown(
                    unknown_commands,
                    command.name,
                    command.arguments,
                    line_number,
                    raw_line,
                )
            continue

        if speaker is not None and line:
            dialogue_parts.append(line)

    return StoryDocument(
        source=source,
        cast=state.cast,
        scenes=state.scenes,
        unknown_commands=unknown_commands,
    )


def _preserve_unknown(
    target: list[UnknownCommand],
    name: str,
    arguments: tuple[str, ...],
    line_number: int,
    raw: str,
) -> None:
    target.append(
        UnknownCommand(
            name=name,
            arguments=list(arguments),
            line_number=line_number,
            raw=raw,
        )
    )
