# Mash Casual Art Processing Implementation Plan

> **For Codex:** REQUIRED SUB-SKILL: Use superpowers:test-driven-development for each task and superpowers:verification-before-completion before claiming completion.

**Goal:** Convert `98001000_merged.png` into one full-body casual asset and all 28 expressions, retaining raw crops and producing transparent runtime crops with auditable geometry and visual QA.

**Architecture:** A deterministic analyzer detects the top full-body area and 7×4 expression grid. It writes immutable raw crops, then removes only edge-connected dark background for runtime crops. A manifest binds outputs to source hash, rectangle, coordinate ID, semantic label, bounding box, and anchor; automated checks and a contact sheet gate acceptance.

**Tech Stack:** Python 3.11, Pillow, Pydantic 2, Typer, pytest

**Source:** `D:\fgo_unpack\fgo_assets\servant\000001\98001000_merged.png`

**Output:** `D:\fgo_unpack\fgo_assets\pet\mash\casual\`

---

## Task 1: Add image dependency and manifest models

**Files:**
- Modify: `pyproject.toml`
- Create: `src/fgo_pet_content/art/__init__.py`
- Create: `src/fgo_pet_content/art/models.py`
- Create: `tests/art/test_models.py`

**Step 1: Write failing tests**

Assert stable IDs accept only `full_body` or `r01c01`–`r07c04`, rectangles remain in bounds, labels are unique, and a manifest requires one full body plus 28 expressions.

```python
def test_manifest_requires_complete_expression_grid():
    with pytest.raises(ValidationError):
        ArtManifest(source=SOURCE, assets=[full_body(), expression("r01c01")])
```

**Step 2: Verify failure**

Run: `D:\environments\anaconda\python.exe -m pytest tests/art/test_models.py -q`
Expected: import failure.

**Step 3: Implement**

Add `Pillow>=10,<12` and models `Rect`, `Anchor`, `ArtAsset`, `SourceImage`, and `ArtManifest`. Each expression has a persistent coordinate ID and editable semantic label.

**Step 4: Install, verify, commit**

```powershell
D:\environments\anaconda\python.exe -m pip install -e .[dev]
D:\environments\anaconda\python.exe -m pytest tests/art/test_models.py -q
git add pyproject.toml src/fgo_pet_content/art tests/art/test_models.py
git commit -m "feat: define Mash art asset manifest"
```

Expected: PASS.

## Task 2: Detect full-body and 7×4 grid geometry

**Files:**
- Create: `src/fgo_pet_content/art/sheet.py`
- Create: `tests/art/test_sheet.py`

**Step 1: Write failing synthetic-sheet tests**

Generate an RGBA sheet in memory with a centered top figure, white separator bands, seven rows, and four cells. Assert 29 non-overlapping rectangles, row-major IDs, bounds safety, and `SheetLayoutError` for a wrong row count.

```python
def test_detects_row_major_expression_grid(synthetic_sheet):
    layout = analyze_sheet(synthetic_sheet)
    assert list(layout.expressions) == [
        f"r{row:02d}c{col:02d}" for row in range(1, 8) for col in range(1, 5)
    ]
```

**Step 2: Verify failure**

Run: `D:\environments\anaconda\python.exe -m pytest tests/art/test_sheet.py -q`
Expected: import failure.

**Step 3: Implement geometry detection**

Rows with at least 98% near-white pixels (`r,g,b >= 245`) form separator bands. The seven content intervals below the top section are expression rows; split each into four equal-width cells, assigning remainder to the final column. Detect the full-body rectangle from the non-white bounding box above the first expression row. Reject layouts other than seven usable rows and four cells.

Analyze the original 1024×2560 file, never the resized chat preview.

**Step 4: Verify and commit**

```powershell
D:\environments\anaconda\python.exe -m pytest tests/art/test_sheet.py -q
git add src/fgo_pet_content/art/sheet.py tests/art/test_sheet.py
git commit -m "feat: detect Mash sprite sheet layout"
```

Expected: PASS.

## Task 3: Preserve raw crops and remove edge-connected background

**Files:**
- Create: `src/fgo_pet_content/art/background.py`
- Create: `src/fgo_pet_content/art/export.py`
- Create: `tests/art/test_background.py`
- Create: `tests/art/test_export.py`

**Step 1: Write failing alpha tests**

Use a synthetic dark-background portrait with a similarly dark enclosed detail. Assert edge-connected background becomes transparent, the enclosed detail stays opaque, raw RGB is unchanged, runtime output is RGBA, and source hash is unchanged.

```python
def test_only_edge_connected_dark_pixels_are_removed(portrait):
    cleaned = remove_edge_background(portrait, tolerance=32, feather=2)
    assert cleaned.getpixel((0, 0))[3] == 0
    assert cleaned.getpixel((8, 8))[3] == 255
```

**Step 2: Verify failure**

Run: `D:\environments\anaconda\python.exe -m pytest tests/art/test_background.py tests/art/test_export.py -q`
Expected: import failure.

**Step 3: Implement conservative removal and atomic export**

Estimate background from border-pixel median. Flood-fill only border-connected pixels within the RGB-distance tolerance. Feather alpha by at most two pixels and decontaminate fringe RGB toward the nearest opaque neighbor. Do not delete an interior component merely because it is dark.

Write:

```text
raw/full_body.png
raw/expressions/ (all 28 row-major files keyed `r01c01` through `r07c04`)
runtime/full_body.png
runtime/expressions/ (all 28 row-major files keyed `r01c01` through `r07c04`)
manifest.json
```

Manifest includes source SHA-256/dimensions/mode, crop rectangles, output hashes, non-transparent bounding boxes, and bottom-center anchors. Open source read-only and write each completed file atomically.

**Step 4: Verify and commit**

```powershell
D:\environments\anaconda\python.exe -m pytest tests/art/test_background.py tests/art/test_export.py -q
git add src/fgo_pet_content/art tests/art/test_background.py tests/art/test_export.py
git commit -m "feat: export raw and transparent Mash art"
```

Expected: PASS.

## Task 4: Curate all 28 semantic labels

**Files:**
- Create: `content/servants/mash/casual-expression-labels.json`
- Create: `src/fgo_pet_content/art/labels.py`
- Create: `tests/art/test_labels.py`

**Step 1: Write failing tests**

Assert all 28 IDs appear exactly once, labels are non-empty and unique, unknown IDs fail, and label edits never alter stable paths or IDs.

**Step 2: Verify failure**

Run: `D:\environments\anaconda\python.exe -m pytest tests/art/test_labels.py -q`
Expected: FAIL because the curation contract is absent.

**Step 3: Add and fill the contract**

```json
{
  "schema_version": 1,
  "expressions": {
    "r01c01": {"label": "微笑交谈", "notes": ""}
  }
}
```

Inspect the generated contact sheet at original crop resolution and fill all 28 actual labels. Do not infer labels from grid position.

**Step 4: Verify and commit**

```powershell
D:\environments\anaconda\python.exe -m pytest tests/art/test_labels.py -q
git add content/servants/mash/casual-expression-labels.json src/fgo_pet_content/art/labels.py tests/art/test_labels.py
git commit -m "content: label all Mash casual expressions"
```

Expected: PASS with 28 entries.

## Task 5: Add QA, contact sheet, CLI, and process the real image

**Files:**
- Create: `src/fgo_pet_content/art/qa.py`
- Modify: `src/fgo_pet_content/cli.py`
- Create: `tests/art/test_qa.py`
- Create: `tests/test_art_cli.py`
- Modify: `README.md`
- Create: `docs/reports/2026-08-26-mash-art-readiness.md`
- External outputs: `D:\fgo_unpack\fgo_assets\pet\mash\casual\`

**Step 1: Write failing QA and CLI tests**

Catch missing/extra crops, hash mismatch, empty alpha, clipped foreground, invalid anchors, wrong dimensions, and incomplete labels. Exercise `art process-mash-casual` and `art validate` against a synthetic source.

**Step 2: Verify failure**

Run: `D:\environments\anaconda\python.exe -m pytest tests/art/test_qa.py tests/test_art_cli.py -q`
Expected: FAIL because QA and CLI are absent.

**Step 3: Implement QA and preview**

Create a checkerboard contact sheet with stable ID and semantic label below each crop. `validate_art_bundle()` returns structured errors and makes CLI exit non-zero for hard failures; warn when foreground touches a boundary or transparency is suspiciously large.

**Step 4: Verify code**

```powershell
D:\environments\anaconda\python.exe -m pytest tests/art tests/test_art_cli.py -q
D:\environments\anaconda\python.exe -m pytest -q
```

Expected: PASS.

**Step 5: Process and validate the real source**

```powershell
D:\environments\anaconda\python.exe -m fgo_pet_content.cli art process-mash-casual --source D:\fgo_unpack\fgo_assets\servant\000001\98001000_merged.png --output D:\fgo_unpack\fgo_assets\pet\mash\casual
D:\environments\anaconda\python.exe -m fgo_pet_content.cli art validate --bundle D:\fgo_unpack\fgo_assets\pet\mash\casual
```

Expected: 58 PNGs (29 raw + 29 runtime), manifest, contact sheet, and zero validation errors.

**Step 6: Perform visual QA and report**

Inspect the contact sheet, full body, and warning crops. Check hair/glasses edges, eye highlights, mouth interiors, jacket shadows, dark halos, clipping, and anchors. If pixels are damaged, add a regression fixture, adjust the algorithm/tolerance, rerun tests, and re-export.

Record hashes, counts, validator result, visual decision, limitations, and default outfit ID `mash_casual_98001000` in the report.

**Step 7: Commit**

```powershell
git add src/fgo_pet_content/art src/fgo_pet_content/cli.py tests/art tests/test_art_cli.py README.md docs/reports/2026-08-26-mash-art-readiness.md
git commit -m "feat: deliver Mash casual art readiness"
```
