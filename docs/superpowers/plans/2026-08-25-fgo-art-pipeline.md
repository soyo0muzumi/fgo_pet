# FGO Art Pipeline Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a tested, non-destructive pipeline that inventories Mash artwork, generates an efficient human review workspace, maps costume-specific faces to desktop-pet emotions, and packages at least one validated appearance for Phase 0.

**Architecture:** The art pipeline reuses the content project's configuration and model conventions. Pillow-based scanning produces immutable metadata and previews in the external workspace; reviewed YAML/JSON mappings drive deterministic package assembly, while story-parser face usage supplies context without making automatic emotion decisions.

**Tech Stack:** Python 3.11+, Pillow, Pydantic 2, Typer, pytest, JSON

**Spec:** `docs/superpowers/specs/2026-08-25-fgo-pet-content-pipeline-design.md`

## Global Constraints

- Never modify files under `fgo_assets/servant/800100`.
- The first target is Servant `800100`, but APIs and schemas must accept other Servant IDs.
- FGO `face_id` is appearance-specific and must not be treated as a universal emotion.
- Each appearance must have a default portrait and an acyclic fallback chain for all ten desktop-pet emotions.
- Derived previews and optimized images belong in the external art workspace until packaging is explicitly requested.
- Original copyrighted artwork is not committed to the public repository.
- Phase 0 begins only after one real Mash appearance passes package validation.

---

### Task 1: Image inventory and Atlas metadata association

**Files:**
- Modify: `pyproject.toml`
- Create: `src/fgo_pet_content/art/models.py`
- Create: `src/fgo_pet_content/art/inventory.py`
- Create: `src/fgo_pet_content/art/__init__.py`
- Create: `tests/art/test_inventory.py`
- Create: `tests/art/fixtures/transparent_portrait.png`
- Create: `tests/art/fixtures/opaque_icon.png`

**Interfaces:**
- Consumes: Servant asset directory and its `nice.json`.
- Produces: `scan_art_assets(servant_dir: Path, servant_id: int) -> ArtInventory`.

- [ ] **Step 1: Write failing inventory tests**

```python
def test_scan_records_dimensions_alpha_bbox_and_hash(art_fixture_dir):
    inventory = scan_art_assets(art_fixture_dir, servant_id=800100)
    portrait = inventory.by_name("transparent_portrait.png")
    assert portrait.width == 64 and portrait.height == 96
    assert portrait.alpha_bbox == (8, 4, 56, 96)
    assert portrait.sha256.startswith("sha256:")

def test_scan_does_not_modify_source_files(art_fixture_dir):
    before = {p.name: p.read_bytes() for p in art_fixture_dir.glob("*.png")}
    scan_art_assets(art_fixture_dir, servant_id=800100)
    after = {p.name: p.read_bytes() for p in art_fixture_dir.glob("*.png")}
    assert before == after
```

- [ ] **Step 2: Run inventory tests and verify failure**

Run: `python -m pytest tests/art/test_inventory.py -v`

Expected: FAIL because art modules are missing.

- [ ] **Step 3: Add Pillow and implement read-only scanning**

Add `"Pillow>=10.4,<12"` to project dependencies. Record path relative to the servant root, content hash, mode, size, Alpha bounding box, transparent ratio, and candidate Atlas group parsed from `nice.json`. Classify obvious icons/command cards by Atlas group and dimensions, but leave uncertain files as `unknown`.

- [ ] **Step 4: Run inventory tests**

Run: `python -m pytest tests/art/test_inventory.py -v`

Expected: all inventory tests pass.

- [ ] **Step 5: Commit inventory support**

```bash
git add pyproject.toml src/fgo_pet_content/art tests/art
git commit -m "feat: inventory servant artwork"
```

### Task 2: Grouping and contact-sheet review workspace

**Files:**
- Create: `src/fgo_pet_content/art/grouping.py`
- Create: `src/fgo_pet_content/art/previews.py`
- Create: `tests/art/test_grouping.py`
- Create: `tests/art/test_previews.py`
- Modify: `src/fgo_pet_content/cli.py`

**Interfaces:**
- Consumes: `ArtInventory`.
- Produces: `group_assets(inventory) -> list[ArtGroup]`; `render_contact_sheet(group, output_path) -> PreviewManifest`.

- [ ] **Step 1: Write failing deterministic-group and preview tests**

```python
def test_grouping_keeps_same_figure_faces_together(inventory):
    groups = group_assets(inventory)
    assert {a.face_id for a in groups[0].assets} == {0, 1, 7, 13}

def test_contact_sheet_labels_each_asset(group, tmp_path):
    manifest = render_contact_sheet(group, tmp_path / "sheet.png")
    assert manifest.columns == 4
    assert [cell.face_id for cell in manifest.cells] == [0, 1, 7, 13]
```

- [ ] **Step 2: Run grouping/preview tests and verify failure**

Run: `python -m pytest tests/art/test_grouping.py tests/art/test_previews.py -v`

Expected: FAIL because grouping and preview functions are absent.

- [ ] **Step 3: Implement metadata-first grouping and non-destructive previews**

Group first by Atlas asset family and figure ID, then use canvas size and Alpha-bounds similarity only as secondary hints. Render checkerboard-backed thumbnails with relative filename, figure ID, face ID, dimensions, and confidence. Write previews solely to `ContentPaths.art_workspace`.

Expose:

```text
fgo-content art inventory --servant-dir D:\fgo_unpack\fgo_assets\servant\800100 --data-root D:\fgo_unpack\fgo_assets
fgo-content art preview --servant 800100 --data-root D:\fgo_unpack\fgo_assets
```

- [ ] **Step 4: Verify previews and CLI**

Run: `python -m pytest tests/art/test_grouping.py tests/art/test_previews.py -v`

Expected: all tests pass.

Run: `python -m fgo_pet_content.cli art --help`

Expected: inventory and preview commands are listed.

- [ ] **Step 5: Commit review workspace generation**

```bash
git add src/fgo_pet_content/art src/fgo_pet_content/cli.py tests/art
git commit -m "feat: generate servant art review sheets"
```

### Task 3: Appearance curation and story face-usage context

**Files:**
- Create: `src/fgo_pet_content/art/curation.py`
- Create: `src/fgo_pet_content/art/face_usage.py`
- Create: `tests/art/test_curation.py`
- Create: `tests/art/test_face_usage.py`
- Create: `content/servants/mash/art-curation.json`

**Interfaces:**
- Consumes: `ArtInventory`, parsed `StoryDocument` files, and reviewed curation JSON.
- Produces: `summarize_face_usage(documents, servant_id) -> list[FaceUsage]`; `load_curation(path, inventory) -> ArtCuration`.

- [ ] **Step 1: Write failing face-frequency and mapping-validation tests**

```python
def test_face_usage_is_scoped_by_figure_id(story_documents):
    usage = summarize_face_usage(story_documents, servant_id=800100)
    assert (usage[0].figure_id, usage[0].face_id) == ("98001000", 13)

def test_curation_rejects_face_from_another_appearance(inventory, curation_path):
    with pytest.raises(ValueError, match="does not belong to appearance"):
        load_curation(curation_path, inventory)
```

- [ ] **Step 2: Run curation tests and verify failure**

Run: `python -m pytest tests/art/test_curation.py tests/art/test_face_usage.py -v`

Expected: FAIL because curation modules do not exist.

- [ ] **Step 3: Implement reviewed appearance schema and face context report**

Define the ten exact emotion keys: `default`, `smile`, `worry`, `surprise`, `angry`, `tired`, `serious`, `shy`, `celebrate`, `comfort`. `art-curation.json` stores source image references, semantic choices, crop, scale, foot anchor, bubble anchor, and fallbacks. Initialize Mash entries as `review_status: "pending"` with discovered source references; do not invent emotion labels automatically.

The face-usage report includes counts and source scene IDs but no full dialogue text.

- [ ] **Step 4: Run curation and face-usage tests**

Run: `python -m pytest tests/art/test_curation.py tests/art/test_face_usage.py -v`

Expected: all tests pass.

- [ ] **Step 5: Commit the review contract**

```bash
git add src/fgo_pet_content/art content/servants/mash/art-curation.json tests/art
git commit -m "feat: define Mash appearance curation"
```

### Task 4: Anchor normalization, overlays, and fallback validation

**Files:**
- Create: `src/fgo_pet_content/art/normalize.py`
- Create: `src/fgo_pet_content/art/validate.py`
- Create: `tests/art/test_normalize.py`
- Create: `tests/art/test_validate.py`

**Interfaces:**
- Consumes: approved `ArtCuration` and original images.
- Produces: `normalize_appearance(curation, workspace) -> NormalizedAppearance`; `validate_appearance(appearance) -> ValidationReport`.

- [ ] **Step 1: Write failing normalization and fallback-cycle tests**

```python
def test_normalized_faces_share_canvas_and_foot_anchor(approved_curation, tmp_path):
    result = normalize_appearance(approved_curation, tmp_path)
    assert len({image.size for image in result.images}) == 1
    assert len({image.foot_anchor for image in result.images}) == 1

def test_fallback_cycle_is_rejected(appearance):
    appearance.fallbacks = {"comfort": "worry", "worry": "comfort"}
    report = validate_appearance(appearance)
    assert "fallback cycle" in report.errors
```

- [ ] **Step 2: Run normalization tests and verify failure**

Run: `python -m pytest tests/art/test_normalize.py tests/art/test_validate.py -v`

Expected: FAIL because normalize/validate modules are missing.

- [ ] **Step 3: Implement deterministic derived images and overlay diagnostics**

Create new RGBA canvases in the external workspace; use configured crop/scale/anchor values and never overwrite originals. Generate an overlay image per appearance to reveal expression jump. Validate default presence, source hashes, Alpha, canvas bounds, bubble anchor bounds, all ten emotions, and acyclic fallbacks that terminate at an existing image.

- [ ] **Step 4: Run art validation tests**

Run: `python -m pytest tests/art/test_normalize.py tests/art/test_validate.py -v`

Expected: all tests pass.

Run: `python -m pytest -q`

Expected: the complete content-pipeline suite passes.

- [ ] **Step 5: Commit normalization and validation**

```bash
git add src/fgo_pet_content/art tests/art
git commit -m "feat: normalize and validate pet portraits"
```

### Task 5: Mash Phase 0 package assembly and real-asset acceptance

**Files:**
- Create: `src/fgo_pet_content/art/package.py`
- Create: `src/fgo_pet_content/package_manifest.py`
- Create: `tests/art/test_package.py`
- Create: `content/servants/mash/manifest.template.json`
- Create: `docs/art-pipeline.md`
- Modify: `src/fgo_pet_content/cli.py`

**Interfaces:**
- Consumes: one approved normalized appearance, approved persona outputs, local lines, and optional CE references.
- Produces: `build_servant_package(inputs, output_dir) -> PackageManifest`; `validate_package(path) -> ValidationReport`; CLI `package build` and `package validate`.

- [ ] **Step 1: Write failing deterministic-package tests**

```python
def test_package_manifest_uses_relative_paths_and_hashes(package_inputs, tmp_path):
    manifest = build_servant_package(package_inputs, tmp_path / "mash")
    assert manifest.servant_id == 800100
    assert all(not Path(item.path).is_absolute() for item in manifest.files)
    assert all(item.sha256.startswith("sha256:") for item in manifest.files)

def test_package_requires_a_valid_default_appearance(package_inputs, tmp_path):
    package_inputs.appearance.validation.errors.append("missing default")
    with pytest.raises(ValueError, match="appearance validation failed"):
        build_servant_package(package_inputs, tmp_path / "mash")
```

- [ ] **Step 2: Run package tests and verify failure**

Run: `python -m pytest tests/art/test_package.py -v`

Expected: FAIL because package modules are missing.

- [ ] **Step 3: Implement deterministic assembly and documentation**

Copy only approved derived assets into a user-selected package output directory. Generate relative paths and SHA-256 hashes for every file. Include package version, Servant ID, appearance definitions, emotion fallbacks, anchors, persona schema version, and source-data version. Refuse to build if any required validation error exists.

Document inventory, preview, manual curation, normalization, package build, package validation, and safe rebuild commands in `docs/art-pipeline.md`.

- [ ] **Step 4: Run tests and real Mash acceptance commands**

Run: `python -m pytest -q`

Expected: all tests pass.

After approving at least one appearance in `content/servants/mash/art-curation.json`, run:

```text
fgo-content art inventory --servant-dir D:\fgo_unpack\fgo_assets\servant\800100 --data-root D:\fgo_unpack\fgo_assets
fgo-content art preview --servant 800100 --data-root D:\fgo_unpack\fgo_assets
fgo-content art normalize --servant 800100 --data-root D:\fgo_unpack\fgo_assets
fgo-content package build --servant 800100 --data-root D:\fgo_unpack\fgo_assets --output D:\fgo_unpack\fgo_assets\packages\mash
fgo-content package validate D:\fgo_unpack\fgo_assets\packages\mash
```

Expected:

- The source directory hash inventory is unchanged after the run.
- At least one appearance resolves all ten semantic emotions through direct images or fallbacks.
- All normalized images share a canvas and stable foot anchor.
- Package validation reports zero errors.
- `git status --short` lists no generated images or raw copyrighted assets.

- [ ] **Step 5: Commit the Phase 0 package builder**

```bash
git add src/fgo_pet_content content/servants/mash/manifest.template.json tests/art docs/art-pipeline.md
git commit -m "feat: build validated Mash content package"
```
