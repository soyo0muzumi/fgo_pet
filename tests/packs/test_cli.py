from __future__ import annotations

import json
from pathlib import Path

from PIL import Image, ImageDraw
from typer.testing import CliRunner

from fgo_pet_content.cli import app


def _ambiguous_sheet(path: Path) -> None:
    image = Image.new("RGBA", (40, 120), (255, 255, 255, 0))
    draw = ImageDraw.Draw(image)
    for row in range(8):
        top = row * 15 + 2
        draw.rectangle((5, top, 34, top + 5), fill=(60, 80, 120, 255))
    image.save(path, format="PNG")


def test_propose_layout_requires_confirmation_for_ambiguous_sheet(tmp_path: Path) -> None:
    source = tmp_path / "sheet.png"
    proposal = tmp_path / "layout.json"
    _ambiguous_sheet(source)

    result = CliRunner().invoke(
        app,
        ["art", "propose-layout", "--source", str(source), "--output", str(proposal)],
    )

    assert result.exit_code == 2, result.output
    assert proposal.exists()
    assert "confirmation.json" in result.output


def test_confirm_layout_writes_a_frozen_layout_spec(tmp_path: Path) -> None:
    source = tmp_path / "sheet.png"
    proposal = tmp_path / "layout.json"
    confirmation = tmp_path / "layout.confirmation.json"
    output = tmp_path / "confirmed.json"
    _ambiguous_sheet(source)
    proposed = CliRunner().invoke(
        app,
        ["art", "propose-layout", "--source", str(source), "--output", str(proposal)],
    )
    assert proposed.exit_code == 2, proposed.output
    confirmation.write_text(
        json.dumps({"schema_version": 1, "rows": 7, "columns": 2, "confirmed_by": "test"}),
        encoding="utf-8",
    )

    result = CliRunner().invoke(
        app,
        [
            "art",
            "confirm-layout",
            "--proposal",
            str(proposal),
            "--confirmation",
            str(confirmation),
            "--output",
            str(output),
        ],
    )

    assert result.exit_code == 0, result.output
    assert json.loads(output.read_text(encoding="utf-8"))["provenance"]["approval"] == "human_confirmation"


def test_pack_validate_rejects_a_failed_qa_report(pack_project: Path) -> None:
    failed_qa = pack_project / "qa-report.json"
    failed_qa.write_text(json.dumps({"status": "FAIL"}), encoding="utf-8")
    package = json.loads((pack_project / "package.json").read_text(encoding="utf-8"))
    package["files"].append("qa-report.json")
    (pack_project / "package.json").write_text(json.dumps(package), encoding="utf-8")

    result = CliRunner().invoke(app, ["pack", "validate", str(pack_project)])

    assert result.exit_code == 1
    assert "qa.failed" in result.output


def test_pack_build_prints_all_release_artifact_paths(pack_project: Path, tmp_path: Path) -> None:
    output = tmp_path / "release"

    result = CliRunner().invoke(
        app,
        ["pack", "build", str(pack_project), "--output", str(output)],
    )

    assert result.exit_code == 0, result.output
    payload = json.loads(result.output)
    assert all(Path(payload[key]).exists() for key in ("archive", "checksum", "qa_report", "release_notes"))
    assert payload["archive"].endswith(".fgopetpack")


def test_pack_build_dry_run_does_not_write_archive(pack_project: Path, tmp_path: Path) -> None:
    output = tmp_path / "dry-run"

    result = CliRunner().invoke(
        app,
        ["pack", "build", str(pack_project), "--output", str(output), "--dry-run"],
    )

    assert result.exit_code == 0, result.output
    assert json.loads(result.output)["dry_run"] is True
    assert not output.exists()
