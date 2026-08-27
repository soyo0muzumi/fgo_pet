import json
import runpy
from pathlib import Path

from typer.testing import CliRunner

from fgo_pet_content.cli import app


_valid_inputs = runpy.run_path("tests/test_readiness.py")["_valid_inputs"]


def test_readiness_cli_passes_then_detects_stale_art(tmp_path: Path) -> None:
    inputs = _valid_inputs(tmp_path)
    report = tmp_path / "readiness.json"
    runner = CliRunner()
    args = [
        "readiness",
        "check-mash",
        "--data-root",
        str(inputs.data_root),
        "--report",
        str(report),
        "--visual-qa",
        "approved",
    ]

    passing = runner.invoke(app, args)
    changed = inputs.data_root / "pet" / "mash" / "casual" / "runtime" / "r01c01.png"
    changed.write_bytes(b"changed")
    blocked = runner.invoke(app, args)

    assert passing.exit_code == 0, passing.output
    assert json.loads(report.read_text(encoding="utf-8"))["status"] == "BLOCKED"
    assert blocked.exit_code == 1
    assert "art.hashes" in blocked.output
