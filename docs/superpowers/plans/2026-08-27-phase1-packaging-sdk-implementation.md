# Phase 1 Servant Pack SDK and Release Tooling Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Turn reviewed servant artwork and metadata into deterministic, validated `.fgopetpack` release artifacts without modifying raw source material or guessing unknown sheet layouts.

**Architecture:** Extend the existing Python content CLI with explicit layout specifications, art schema v3 models, v2 migration, preview/QA generation, and deterministic pack assembly. The Python and .NET implementations share canonical JSON fixtures so a package accepted by the SDK is accepted by the app.

**Tech Stack:** Python 3.12+, Pydantic, Pillow, Typer, pytest, SHA-256, ZIP.

**Spec:** `docs/superpowers/specs/2026-08-27-phase1-offline-pet-and-servant-packs-design.md`

## Global Constraints

- Raw source paths are read-only and never included in publishable archives.
- Unknown or ambiguous layouts stop with `confirmation_required`; the tool never guesses crops.
- Preserve existing Alpha; background removal may not erode pre-existing transparency or antialiased edges.
- Publish only pack schema v1 plus art schema v3. Art schema v2 remains immutable migration input.
- `.fgopetpack` contains only the allowlisted data types accepted by the .NET installer.
- Build output must be deterministic for identical inputs and configuration.
- GitHub Releases is the distribution target, but Phase 1 tooling prepares artifacts and release notes only; it does not call GitHub APIs.

---

### Task 1: Add art schema v3 and deterministic v2 migration

**Files:**
- Create: `src/fgo_pet_content/art/v3_models.py`
- Create: `src/fgo_pet_content/art/migrate.py`
- Create: `tests/art/test_v3_models.py`
- Create: `tests/art/test_migrate_v2.py`
- Reuse: `tests/fixtures/packs/mash-art-v2.json`
- Reuse: `tests/fixtures/packs/mash-art-v3.json`

**Interfaces:**
- Produces: `ArtManifestV3`, explicit `asset_type`, expression list, eight semantic mappings, and fallback map.
- Produces: `migrate_v2_to_v3(manifest: ArtManifest, semantic_map: dict[str, str]) -> ArtManifestV3`.

- [ ] **Step 1: Write failing schema and migration tests**

```python
def test_migrate_mash_preserves_stable_ids_and_geometry(v2_manifest, semantic_map):
    result = migrate_v2_to_v3(v2_manifest, semantic_map)
    assert result.schema_version == 3
    assert result.composition.overlay_offset == Point(x=13, y=0)
    assert result.composition.panel_anchor == Point(x=151, y=360)
    assert {asset.stable_id for asset in result.assets} == {asset.stable_id for asset in v2_manifest.assets}
    assert set(result.expression_semantics) == set(CORE_EXPRESSION_SEMANTICS)
```

Also reject missing neutral, unknown asset IDs, fallback cycles, duplicate IDs, unsupported scale, overlay overflow, and unknown fields.

- [ ] **Step 2: Verify failure**

Run: `python -m pytest tests/art/test_v3_models.py tests/art/test_migrate_v2.py -q`  
Expected: FAIL with missing v3/migration modules.

- [ ] **Step 3: Implement frozen strict Pydantic models and converter**

Keep existing `art/models.py` unchanged except shared value types may be imported. Require all eight core semantics; allow them to share one asset. Conversion accepts an explicit semantic-map file and never derives emotional meaning from labels.

- [ ] **Step 4: Run tests and commit**

Run: `python -m pytest tests/art/test_v3_models.py tests/art/test_migrate_v2.py -q`  
Expected: PASS.

```bash
git add src/fgo_pet_content/art tests/art tests/fixtures/packs
git commit -m "feat: add art schema v3 migration"
```

### Task 2: Parameterize layouts with mandatory human confirmation

**Files:**
- Create: `src/fgo_pet_content/art/layout_spec.py`
- Modify: `src/fgo_pet_content/art/sheet.py`
- Create: `tests/art/test_layout_spec.py`
- Modify: `tests/art/test_sheet.py`
- Create: `tests/fixtures/art/layouts/mash-7x4.json`
- Create: `tests/fixtures/art/layouts/alternate-2x3.json`

**Interfaces:**
- Produces: `LayoutSpec` with body rectangle, expression grid/rectangles, IDs, and confidence provenance.
- Produces: `analyze_sheet(image, expected: LayoutExpectation) -> LayoutProposal`.
- Produces: `confirm_layout(proposal, confirmation_file) -> LayoutSpec`.

- [ ] **Step 1: Write failing fixed-grid, alternate-grid, and ambiguous-layout tests**

```python
def test_ambiguous_layout_requires_confirmation(ambiguous_sheet):
    proposal = analyze_sheet(ambiguous_sheet, LayoutExpectation(rows=None, columns=None))
    assert proposal.status == "confirmation_required"
    with pytest.raises(SheetLayoutError, match="human confirmation"):
        proposal.to_layout_spec()
```

Assert the existing 7x4 fixture remains identical and the controlled 2x3 fixture succeeds only with explicit row/column or confirmed rectangles.

- [ ] **Step 2: Run tests to verify failure**

Run: `python -m pytest tests/art/test_layout_spec.py tests/art/test_sheet.py -q`  
Expected: FAIL.

- [ ] **Step 3: Split detection from approval**

Detection may propose intervals and a preview, but export consumes only a confirmed `LayoutSpec`. Preserve the existing `analyze_sheet(image)` behavior through a v2 compatibility wrapper until migration tests move to the new API.

- [ ] **Step 4: Run tests and commit**

Run: `python -m pytest tests/art/test_layout_spec.py tests/art/test_sheet.py -q`  
Expected: PASS.

```bash
git add src/fgo_pet_content/art tests/art tests/fixtures/art
git commit -m "feat: require confirmed servant sheet layouts"
```

### Task 3: Generate v3 assets, semantic templates, and visual QA

**Files:**
- Modify: `src/fgo_pet_content/art/export.py`
- Modify: `src/fgo_pet_content/art/qa.py`
- Create: `src/fgo_pet_content/art/preview.py`
- Create: `tests/art/test_v3_export.py`
- Create: `tests/art/test_preview.py`
- Modify: `tests/art/test_qa_cli.py`

**Interfaces:**
- Produces: `export_appearance_v3(source, layout, metadata, output_dir) -> ArtManifestV3`.
- Produces: `semantic-map.template.json`, `contact-sheet.png`, overlay composites, and `qa-report.json`.

- [ ] **Step 1: Write failing Alpha, preview, seam, and fallback tests**

Assert source is unchanged, existing RGBA Alpha never decreases, every runtime asset has visible Alpha, overlay composites fit body, configured offsets are used, all semantic-map slots appear, and QA fails rather than choosing a missing mapping.

- [ ] **Step 2: Verify failure**

Run: `python -m pytest tests/art/test_v3_export.py tests/art/test_preview.py tests/art/test_qa_cli.py -q`  
Expected: FAIL.

- [ ] **Step 3: Implement generalized export and review artifacts**

Replace hard-coded `r01c01/r02c02/r04c04/r07c03` preview choices with deterministic first/default plus evenly sampled expression IDs. Include a full composite per core semantic and report possible clipping, Alpha loss, mismatched dimensions, seam deltas, and foreground touching edges.

- [ ] **Step 4: Run tests and commit**

Run: `python -m pytest tests/art/test_v3_export.py tests/art/test_preview.py tests/art/test_qa_cli.py -q`  
Expected: PASS.

```bash
git add src/fgo_pet_content/art tests/art
git commit -m "feat: export and review generalized appearances"
```

### Task 4: Assemble deterministic code-free servant packs

**Files:**
- Create: `src/fgo_pet_content/packs/models.py`
- Create: `src/fgo_pet_content/packs/build.py`
- Create: `src/fgo_pet_content/packs/validate.py`
- Create: `src/fgo_pet_content/packs/__init__.py`
- Create: `tests/packs/test_models.py`
- Create: `tests/packs/test_build.py`
- Create: `tests/packs/test_validate.py`

**Interfaces:**
- Produces: strict `PackManifestV1`.
- Produces: `build_pack(project_dir, output_dir) -> PackBuildResult` with `.fgopetpack`, external `.sha256`, QA report, and release-notes fragment.

- [ ] **Step 1: Write failing allowlist/security/determinism tests**

```python
def test_build_is_byte_for_byte_deterministic(pack_project, tmp_path):
    first = build_pack(pack_project, tmp_path / "a").archive.read_bytes()
    second = build_pack(pack_project, tmp_path / "b").archive.read_bytes()
    assert first == second
```

Reject code/script/XAML/HTML/shader extensions, absolute/traversal paths, links, duplicate normalized paths, missing previews, invalid appearance entrypoints, missing hashes, and undeclared files.

- [ ] **Step 2: Run tests to verify failure**

Run: `python -m pytest tests/packs -q`  
Expected: FAIL with missing pack modules.

- [ ] **Step 3: Implement canonical manifest and deterministic ZIP**

Sort entries ordinally, normalize separators to `/`, use fixed ZIP timestamps and permissions, serialize UTF-8 JSON with stable key/indent rules, and calculate the external SHA-256 after closing the archive. Never include raw images or source absolute paths.

- [ ] **Step 4: Run tests and commit**

Run: `python -m pytest tests/packs -q`  
Expected: PASS.

```bash
git add src/fgo_pet_content/packs tests/packs
git commit -m "feat: build deterministic servant pack releases"
```

### Task 5: Add CLI workflows and Mash release project

**Files:**
- Modify: `src/fgo_pet_content/cli.py`
- Create: `content/packs/official.mash/package.json`
- Create: `content/packs/official.mash/appearances/casual.json`
- Create: `content/packs/official.mash/expression-semantics.json`
- Create: `content/packs/official.mash/persona/README.md`
- Create: `tests/packs/test_cli.py`
- Create: `docs/content-pipeline.md`

**Interfaces:**
- Produces commands: `art propose-layout`, `art confirm-layout`, `art export-appearance`, `pack validate`, and `pack build`.
- Produces a Mash project containing metadata/config only; generated PNGs remain outside Git unless the owner separately approves distribution.

- [ ] **Step 1: Write failing CLI success and refusal tests**

Assert ambiguous proposal exits 2 and prints the confirmation-file path, build refuses failed QA, successful build prints all artifact paths as JSON, and no command mutates the raw input tree.

- [ ] **Step 2: Run CLI tests**

Run: `python -m pytest tests/packs/test_cli.py -q`  
Expected: FAIL.

- [ ] **Step 3: Implement Typer commands and metadata-only Mash project**

The documented release flow is propose -> inspect preview -> confirm -> export -> fill semantic map -> validate -> build. `pack build` requires QA status PASS and emits `<package-id>-<version>.fgopetpack`, `.sha256`, `qa-report.json`, previews, and Markdown release notes.

- [ ] **Step 4: Run tests and a local Mash dry run**

Run: `python -m pytest tests/packs/test_cli.py tests/art -q`  
Expected: PASS.  
Run: `python -m fgo_pet_content.cli pack build content/packs/official.mash --output .tmp/releases --dry-run`  
Expected: validates metadata and lists required generated inputs without writing an archive.

- [ ] **Step 5: Commit**

```bash
git add src/fgo_pet_content/cli.py content/packs tests/packs docs/content-pipeline.md
git commit -m "feat: add servant pack release workflow"
```

### Task 6: Lock Python/.NET compatibility and release verification

**Files:**
- Create: `tests/fixtures/packs/valid-minimal/**`
- Create: `tests/fixtures/packs/invalid-cases/**`
- Create: `scripts/test-packaging.ps1`
- Create: `docs/testing/servant-pack-release-checklist.md`
- Modify: `tests/FgoPet.Infrastructure.Tests/Packs/AppearanceValidatorTests.cs`
- Modify: `tests/FgoPet.Infrastructure.Tests/Packs/FgoPetPackInstallerTests.cs`

**Interfaces:**
- The same canonical fixture set is consumed by Python builder/validator and .NET reader/installer tests.
- Release checklist produces GitHub-ready archive, SHA-256, QA report, preview, and notes.

- [ ] **Step 1: Add shared positive and negative fixtures**

Include valid one-expression/eight-semantic, valid multi-appearance, hash mismatch, Zip Slip, unknown schema, fallback cycle, missing neutral, invisible Alpha, overlay overflow, forbidden extension, and truncated archive cases.

- [ ] **Step 2: Make both runtimes consume the fixtures**

Python asserts build/validation outcomes; .NET asserts identical acceptance and stable `PackErrorCode` values. No runtime may maintain a private fixture that contradicts the shared contract.

- [ ] **Step 3: Run the full packaging gate**

Run: `pwsh -File scripts/test-packaging.ps1`  
Expected: Python art/pack tests PASS, .NET pack/appearance tests PASS, deterministic rebuild hashes match, and archive allowlist scan reports zero forbidden entries.

- [ ] **Step 4: Perform the first GitHub Release dry run**

Follow `docs/testing/servant-pack-release-checklist.md` without uploading: verify filenames, SemVer, app compatibility, external SHA-256, preview, QA PASS, release notes, clean extraction, and local installation into a Phase 1 app build.

- [ ] **Step 5: Commit**

```bash
git add tests/fixtures/packs tests/FgoPet.Infrastructure.Tests scripts docs/testing
git commit -m "test: lock servant pack cross-runtime compatibility"
```

## Knowledge Map

| Step | Knowledge Source | Confidence |
|---|---|---|
| Existing Mash extraction, Alpha handling, and QA | Codebase: `src/fgo_pet_content/art/` and 105 passing Python tests | High |
| v2 composition and stable IDs | Phase 0 report, ADR, existing manifests | High |
| v3/pack v1 contract | Approved Phase 1 spec and main implementation plan | High |
| Alternate controlled layout | User-approved P1.4 requirement; synthetic 2x3 fixture supplies evidence | High |
| Actual new-servant sheet conventions | Not yet available; tool must require explicit layout confirmation | Blocked per new sheet, not a tooling blocker |
| Permission to redistribute generated FGO images | Project owner/release policy, not present in repository | Blocked for public upload only |

## Open Questions

- [ ] Before the first public GitHub Release, confirm the legal/distribution policy for generated FGO runtime images. This blocks upload, not SDK implementation or local packaging.
- [ ] Measure the final Mash archive and copy the main plan's installer limits into Python validation fixtures so both runtimes enforce identical ceilings.

## Implementation Checklist

- [ ] Task 1: Art schema v3 and v2 migration
- [ ] Task 2: Explicit confirmed layout specifications
- [ ] Task 3: Generalized export and visual QA
- [ ] Task 4: Deterministic code-free pack assembly
- [ ] Task 5: CLI and Mash release project
- [ ] Task 6: Cross-runtime contract and release verification

