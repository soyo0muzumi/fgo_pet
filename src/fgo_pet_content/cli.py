import json
from pathlib import Path

import typer

from .atlas import AtlasClient
from .catalog import SourceCatalog
from .config import ContentPaths
from .discovery import MashIdentity, ScriptCandidate, discover_candidates
from .models.source import Region
from .pipeline import StoryPipeline, write_parsed_artifact


app = typer.Typer(help="FGO Pet content pipeline")
story_app = typer.Typer(help="Discover and fetch FGO story scripts")
app.add_typer(story_app, name="story")


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


if __name__ == "__main__":
    app()
