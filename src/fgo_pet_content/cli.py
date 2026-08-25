import json
from collections import Counter
from pathlib import Path

import typer

from .atlas import AtlasClient
from .cache import atomic_write
from .catalog import SourceCatalog
from .compiler import compile_persona, write_persona_bundle
from .config import ContentPaths
from .discovery import MashIdentity, ScriptCandidate, discover_candidates
from .evidence import EvidenceExtractor, build_evidence_windows
from .llm import OpenAICompatibleStructuredClient
from .models.evidence import EvidenceCard
from .models.source import Region
from .models.source import ReviewStatus
from .models.story import StoryDocument
from .pipeline import StoryPipeline, write_parsed_artifact
from .reporting import build_review_report
from .review import review_card


app = typer.Typer(help="FGO Pet content pipeline")
story_app = typer.Typer(help="Discover and fetch FGO story scripts")
evidence_app = typer.Typer(help="Extract and review persona evidence")
persona_app = typer.Typer(help="Compile approved persona data")
app.add_typer(story_app, name="story")
app.add_typer(evidence_app, name="evidence")
app.add_typer(persona_app, name="persona")


@app.callback()
def main() -> None:
    """Extract and package local FGO Pet content."""


@story_app.command("discover")
def discover_story_scripts(
    data_root: Path = typer.Option(..., exists=False, file_okay=False),
    servant: int = typer.Option(800100),
) -> None:
    """Search Atlas for scripts related to a supported servant."""
    if servant != 800100:
        raise typer.BadParameter("only servant 800100 is configured")
    paths = ContentPaths.from_root(data_root, Path.cwd())
    candidates = discover_candidates(MashIdentity.default(), AtlasClient(paths))
    typer.echo(
        json.dumps(
            [
                {
                    "script_id": item.script_id,
                    "regions": sorted(region.value for region in item.matched_regions),
                    "match_reasons": sorted(item.match_reasons),
                    "best_score": item.best_score,
                }
                for item in candidates
            ],
            ensure_ascii=False,
            indent=2,
        )
    )


@story_app.command("fetch")
def fetch_story_scripts(
    data_root: Path = typer.Option(..., exists=False, file_okay=False),
    master_root: Path = typer.Option(..., exists=True, file_okay=False),
    script_id: list[str] | None = typer.Option(None),
    servant: int = typer.Option(800100),
) -> None:
    """Fetch, parse, and cache selected or discovered story scripts."""
    if servant != 800100:
        raise typer.BadParameter("only servant 800100 is configured")
    paths = ContentPaths.from_root(data_root, Path.cwd())
    atlas = AtlasClient(paths)
    identity = MashIdentity.default()
    catalog = SourceCatalog.from_master_root(master_root, Region.JP)
    pipeline = StoryPipeline(atlas, catalog, identity)
    candidates = (
        [ScriptCandidate(script_id=value) for value in script_id]
        if script_id
        else discover_candidates(identity, atlas)
    )
    outputs = [
        write_parsed_artifact(pipeline.fetch_and_parse(candidate), paths)
        for candidate in candidates
    ]
    typer.echo(json.dumps([str(path) for path in outputs], ensure_ascii=False, indent=2))


@story_app.command("fetch-script")
def fetch_single_story_script(
    region: Region = typer.Option(Region.CN),
    script_id: str = typer.Option(...),
    data_root: Path = typer.Option(..., exists=False, file_okay=False),
    master_root: Path = typer.Option(..., exists=True, file_okay=False),
) -> None:
    """Fetch one approved probe script and emit a redacted report."""
    paths = ContentPaths.from_root(data_root, Path.cwd())
    atlas = AtlasClient(paths)
    identity = MashIdentity.default()
    catalog = SourceCatalog.from_master_root(master_root, Region.JP)
    pipeline = StoryPipeline(atlas, catalog, identity)
    artifact = pipeline.fetch_and_parse(ScriptCandidate(script_id=script_id))
    if region is Region.JP and artifact.document.source.region is Region.CN:
        raise typer.BadParameter("CN was available; use the default CN-first policy")
    parsed_path = write_parsed_artifact(artifact, paths)
    counts = Counter(item.name for item in artifact.document.unknown_commands)
    report = build_review_report([], unknown_commands=counts)
    report.update(
        {
            "script_id": script_id,
            "region": artifact.document.source.region.value,
            "container_type": artifact.document.source.container_type,
            "container_id": artifact.document.source.container_id,
            "scene_count": len(artifact.document.scenes),
            "utterance_count": sum(
                len(scene.utterances) for scene in artifact.document.scenes
            ),
            "mash_utterance_count": sum(
                item.servant_id == identity.servant_id
                or item.speaker in {"玛修", "マシュ"}
                for scene in artifact.document.scenes
                for item in scene.utterances
            ),
            "parsed_path": str(parsed_path),
        }
    )
    report_path = paths.reports / f"{script_id}.json"
    atomic_write(
        report_path,
        json.dumps(report, ensure_ascii=False, indent=2).encode("utf-8"),
    )
    typer.echo(str(report_path))


@evidence_app.command("extract")
def extract_evidence(
    parsed_document: Path = typer.Option(..., exists=True, dir_okay=False),
    output: Path = typer.Option(..., dir_okay=False),
    base_url: str = typer.Option(...),
    api_key: str = typer.Option(..., envvar="FGO_LLM_API_KEY", hide_input=True),
    model: str = typer.Option(...),
) -> None:
    """Extract candidate cards from a parsed external story document."""
    payload = json.loads(parsed_document.read_text(encoding="utf-8"))
    document = StoryDocument.model_validate(payload["document"])
    extractor = EvidenceExtractor(
        OpenAICompatibleStructuredClient(
            base_url=base_url,
            api_key=api_key,
            model=model,
        )
    )
    cards = [
        card
        for window in build_evidence_windows(document, servant_id=800100)
        for card in extractor.extract(window)
    ]
    atomic_write(
        output,
        ("\n".join(card.model_dump_json() for card in cards) + "\n").encode("utf-8"),
    )
    typer.echo(f"wrote {len(cards)} pending evidence cards")


@evidence_app.command("review")
def review_evidence(
    evidence_file: Path = typer.Option(..., exists=True, dir_okay=False),
    evidence_id: str = typer.Option(...),
    decision: ReviewStatus = typer.Option(...),
    notes: str = typer.Option(""),
) -> None:
    """Apply one explicit human review decision to a candidate card."""
    cards = _load_cards(evidence_file)
    found = False
    reviewed: list[EvidenceCard] = []
    for card in cards:
        if card.evidence_id == evidence_id:
            reviewed.append(review_card(card, decision, notes=notes))
            found = True
        else:
            reviewed.append(card)
    if not found:
        raise typer.BadParameter(f"unknown evidence ID: {evidence_id}")
    atomic_write(
        evidence_file,
        ("\n".join(card.model_dump_json() for card in reviewed) + "\n").encode("utf-8"),
    )


@persona_app.command("compile")
def compile_persona_command(
    evidence_file: Path = typer.Option(..., exists=True, dir_okay=False),
    output_dir: Path = typer.Option(..., file_okay=False),
) -> None:
    """Compile approved evidence into separate runtime layers."""
    outputs = write_persona_bundle(
        compile_persona(_load_cards(evidence_file)), output_dir
    )
    typer.echo(json.dumps({key: str(path) for key, path in outputs.items()}, indent=2))


def _load_cards(path: Path) -> list[EvidenceCard]:
    return [
        EvidenceCard.model_validate_json(line)
        for line in path.read_text(encoding="utf-8").splitlines()
        if line.strip()
    ]


if __name__ == "__main__":
    app()
