import json
from pathlib import Path

from PIL import Image, ImageDraw
from typer.testing import CliRunner

from fgo_pet_content.art.export import export_art_bundle
from fgo_pet_content.art.labels import load_expression_labels
from fgo_pet_content.art.qa import validate_art_bundle
from fgo_pet_content.cli import app


def _sheet(path: Path) -> None:
    image = Image.new("RGBA", (120, 174), (255, 255, 255, 255))
    draw = ImageDraw.Draw(image)
    draw.rectangle((45, 2, 74, 17), fill=(40, 40, 45, 255))
    draw.rectangle((52, 4, 67, 16), fill=(220, 170, 190, 255))
    for row in range(7):
        top = 22 + row * 22
        draw.rectangle((0, top, 119, top + 17), fill=(40, 40, 45, 255))
        for column in range(4):
            left = column * 30 + 8
            draw.rectangle((left, top + 2, left + 12, top + 15), fill=(220, 170, 190, 255))
    image.save(path)


def test_validator_detects_hash_tampering(tmp_path: Path) -> None:
    source = tmp_path / "source.png"
    bundle = tmp_path / "bundle"
    _sheet(source)
    labels = load_expression_labels(
        Path("content/servants/mash/casual-expression-labels.json")
    )
    export_art_bundle(source, bundle, labels, feather=0)
    (bundle / "runtime" / "expressions" / "r01c01.png").write_bytes(b"broken")

    report = validate_art_bundle(bundle)

    assert report.status == "FAIL"
    assert any(error.check_id == "asset.runtime_hash" for error in report.errors)


def test_art_cli_processes_valid_bundle_and_writes_contact_sheet(
    tmp_path: Path,
) -> None:
    source = tmp_path / "source.png"
    bundle = tmp_path / "bundle"
    _sheet(source)
    runner = CliRunner()

    process = runner.invoke(
        app,
        [
            "art",
            "process-mash-casual",
            "--source",
            str(source),
            "--output",
            str(bundle),
            "--labels",
            "content/servants/mash/casual-expression-labels.json",
        ],
    )
    validate = runner.invoke(app, ["art", "validate", "--bundle", str(bundle)])

    assert process.exit_code == 0, process.output
    assert validate.exit_code == 0, validate.output
    assert json.loads(validate.output)["status"] == "PASS"
    assert (bundle / "contact-sheet.png").exists()
