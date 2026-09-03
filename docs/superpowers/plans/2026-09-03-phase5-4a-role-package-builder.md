# Phase 5.4A Role Package Contract and Deterministic Builder Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Deliver the Phase 5.4A role-package contract and deterministic Python builder, with the same safe package decisions enforced by the existing .NET reader/installer.

**Architecture:** Keep content extraction and package construction in the existing Python content project, while Core remains the source of the pack and art contracts consumed by the .NET installer. Reuse only the reviewed art-v3 and confirmed-layout changes from `feature/phase1-packaging-sdk`; add a code-free pack manifest, canonical ZIP writer, metadata-only Mash release project, and cross-runtime fixtures. GUI installation, upgrade rollback, and final release smoke remain the next 5.4B/5.5 slice.

**Tech Stack:** Python 3.11+, Pydantic 2, Pillow, Typer, pytest, C#/.NET 8, SHA-256, ZIP archives, xUnit, PowerShell verification script.

**Spec:** `docs/superpowers/specs/2026-09-01-phase5-productization-design.md` sections 8.1 and 10; supporting art contract `docs/superpowers/specs/2026-08-27-phase1-offline-pet-and-servant-packs-design.md`; selective source implementation `docs/superpowers/plans/2026-08-27-phase1-packaging-sdk-implementation.md`.

## Global Constraints

- Python builder accepts only data and reviewed assets; package archives never contain executable code, scripts, XAML, HTML, shaders, logs, prompts, absolute source paths, or raw source directories.
- Unknown or ambiguous layouts stop with `confirmation_required`; the tool never guesses crops or semantic meanings.
- Publish only pack schema v1 plus art schema v3; art schema v2 remains immutable migration input.
- Normalize input order, timestamps, ZIP attributes, and compression settings so identical input produces identical package bytes, member hashes, and manifest hashes.
- Python validator and .NET installer use the same legal/illegal fixtures and aligned extraction ceilings and acceptance rules.
- Generated role-package artifacts remain separate from program artifacts; this plan does not upload or publish a release.
- Existing Phase 1–4 portrait, dialogue, persona, knowledge, navigation, and Agent behavior contracts are not rewritten.

---

### Task 1: Selectively adopt art schema v3 and confirmed layout contracts

**Files:**
- Create: `src/fgo_pet_content/art/v3_models.py`
- Create: `src/fgo_pet_content/art/migrate.py`
- Create: `src/fgo_pet_content/art/layout_spec.py`
- Modify: `src/fgo_pet_content/art/sheet.py`
- Create: `tests/art/test_v3_models.py`
- Create: `tests/art/test_migrate_v2.py`
- Create: `tests/art/test_layout_spec.py`
- Modify: `tests/art/test_sheet.py`
- Create: `tests/fixtures/art/layouts/mash-7x4.json`
- Create: `tests/fixtures/art/layouts/alternate-2x3.json`

**Interfaces:**
- `ArtManifestV3`, `ArtAssetV3`, and `CompositionV3` expose schema version 3, typed body/expression assets, geometry, eight core expression semantics, and fallback mappings.
- `migrate_v2_to_v3(manifest: ArtManifest, semantic_map: dict[str, str]) -> ArtManifestV3` converts only an explicit reviewed semantic map.
- `analyze_sheet(image: Image.Image, expected: LayoutExpectation | None = None) -> LayoutProposal` proposes rectangles and returns `confirmation_required` for ambiguous layouts.
- `confirm_layout(proposal: LayoutProposal, confirmation_file: str | Path) -> LayoutSpec` accepts only a valid explicit confirmation file.

- [ ] **Step 1: Add failing contract tests before production changes**

  Add assertions for exact v3 fields, strict unknown-property rejection, missing neutral, unknown asset IDs, duplicate IDs, fallback cycles, unsupported scales, v2 geometry preservation, and explicit layout approval. Include an ambiguous synthetic sheet whose proposal cannot be converted without confirmation.

- [ ] **Step 2: Run the focused tests and verify the expected red state**

  Run: `python -m pytest tests/art/test_v3_models.py tests/art/test_migrate_v2.py tests/art/test_layout_spec.py tests/art/test_sheet.py -q`

  Expected: failure because the v3 and layout-contract modules/API are absent on this branch.

- [ ] **Step 3: Transplant only the reviewed art-v3/layout implementation**

  Bring the implementation from commits `150e5e3` and `29e95c5` on `feature/phase1-packaging-sdk`, resolving imports against the current `art/models.py` and preserving the existing `analyze_sheet(image)` compatibility behavior. Do not merge unrelated SDK or Phase 1 application commits.

- [ ] **Step 4: Run the focused tests and inspect the contract**

  Run the focused command from Step 2. Confirm all tests pass, the confirmed-layout provenance is serialized, and no source path is introduced into the v3 runtime contract.

- [ ] **Step 5: Commit the isolated contract slice**

  ```bash
  git add src/fgo_pet_content/art tests/art tests/fixtures/art/layouts
  git commit -m "feat(phase5): adopt confirmed art package contracts"
  ```

### Task 2: Export v3 assets and deterministic visual QA artifacts

**Files:**
- Modify: `src/fgo_pet_content/art/export.py`
- Modify: `src/fgo_pet_content/art/qa.py`
- Create: `src/fgo_pet_content/art/preview.py`
- Create: `tests/art/test_v3_export.py`
- Create: `tests/art/test_preview.py`
- Modify: `tests/art/test_qa_cli.py`

**Interfaces:**
- `export_appearance_v3(source: Path, layout: LayoutSpec, metadata: AppearanceExportMetadata, output_dir: Path) -> ArtManifestV3` writes runtime assets and a path-relative v3 manifest without modifying `source`.
- `write_preview_artifacts(bundle: Path, manifest: ArtManifestV3, output_dir: Path) -> PreviewArtifacts` writes a deterministic contact sheet and one composite per core semantic.
- `validate_art_bundle(bundle: Path) -> ArtQaReport` returns `PASS` only when all declared hashes, alpha, dimensions, bounds, mappings, and review inputs are valid.

- [ ] **Step 1: Write failing export and preview tests**

  Cover unchanged source bytes, preservation of pre-existing alpha, visible runtime alpha, path-relative v3 assets, configured overlay offsets, deterministic preview ordering, all eight semantic slots, and fail-closed behavior for missing mappings, clipping, edge-touching foreground, dimension mismatch, or hash mismatch.

- [ ] **Step 2: Run the focused tests and verify the expected red state**

  Run: `python -m pytest tests/art/test_v3_export.py tests/art/test_preview.py tests/art/test_qa_cli.py -q`

  Expected: failure because the v3 export and preview interfaces do not yet exist.

- [ ] **Step 3: Implement the minimal generalized export and QA flow**

  Consume only `LayoutSpec`, derive no semantic labels automatically, write runtime files under a controlled relative tree, generate previews by stable asset order and evenly sampled expression IDs, and serialize QA details without absolute paths or raw source metadata.

- [ ] **Step 4: Run the focused art suite and the existing art regression suite**

  Run: `python -m pytest tests/art -q`

  Expected: all art tests pass with no source-tree mutation.

- [ ] **Step 5: Commit the v3 export slice**

  ```bash
  git add src/fgo_pet_content/art tests/art
  git commit -m "feat(phase5): export reviewed art v3 assets"
  ```

### Task 3: Define the code-free pack manifest and deterministic builder/validator

**Files:**
- Create: `src/fgo_pet_content/packs/__init__.py`
- Create: `src/fgo_pet_content/packs/models.py`
- Create: `src/fgo_pet_content/packs/build.py`
- Create: `src/fgo_pet_content/packs/validate.py`
- Create: `tests/packs/__init__.py`
- Create: `tests/packs/conftest.py`
- Create: `tests/packs/test_models.py`
- Create: `tests/packs/test_build.py`
- Create: `tests/packs/test_validate.py`

**Interfaces:**
- `PackManifestV1` contains `schema_version`, `package_id`, SemVer `package_version`, `servant_id`, display metadata, `min_app_version`, a controlled capability list, preview path, and appearance manifest references.
- `build_pack(project_dir: Path, output_dir: Path) -> PackBuildResult` produces a closed `.fgopetpack`, external `.sha256`, `qa-report.json`, and release-notes Markdown.
- `validate_pack_project(project_dir: Path) -> PackValidationReport` validates project metadata, declared files, appearance manifests, capabilities, hashes, and archive allowlist before building.

- [ ] **Step 1: Write failing model, security, and determinism tests**

  Define a minimal valid project fixture and assert byte-for-byte equality for two builds from the same input. Add tests rejecting absolute/traversal paths, symlink-like entries, duplicate normalized paths, undeclared files, missing preview/appearance files, forbidden extensions, unknown required capabilities, unsupported schema/app versions, invalid hashes, and non-terminating fallback maps.

- [ ] **Step 2: Run the pack tests and verify the expected red state**

  Run: `python -m pytest tests/packs -q`

  Expected: failure because the pack modules and builder interfaces are absent.

- [ ] **Step 3: Implement strict models and fail-closed project validation**

  Use Pydantic `extra="forbid"`, safe relative POSIX paths, a fixed capability allowlist matching the current .NET contract, and the same production entry/expanded byte limits as `PackArchivePolicy.Production`. Require every declared file to exist and every included file to be declared.

- [ ] **Step 4: Implement canonical ZIP assembly**

  Serialize UTF-8 JSON with stable key ordering and separators, sort all archive members ordinally, use a fixed ZIP timestamp and permission bits, fix the compression method/settings, close the archive before hashing it, and atomically move the finished archive into `output_dir`. Never include the project directory prefix in member names.

- [ ] **Step 5: Run the pack suite and inspect archive contents**

  Run: `python -m pytest tests/packs -q`

  Then inspect a generated archive with `python -c "from zipfile import ZipFile; from pathlib import Path; p=next(Path('.tmp').glob('**/*.fgopetpack')); print(ZipFile(p).namelist())"` and verify it contains only declared data files, no source paths, and no executable suffix.

- [ ] **Step 6: Commit the deterministic builder slice**

  ```bash
  git add src/fgo_pet_content/packs tests/packs
  git commit -m "feat(phase5): build deterministic code-free role packs"
  ```

### Task 4: Add the release CLI and metadata-only Mash project

**Files:**
- Modify: `src/fgo_pet_content/cli.py`
- Create: `content/packs/official.mash/package.json`
- Create: `content/packs/official.mash/appearances/casual.json`
- Create: `content/packs/official.mash/expression-semantics.json`
- Create: `content/packs/official.mash/persona/README.md`
- Create: `tests/packs/test_cli.py`
- Modify: `docs/content-pipeline.md`

**Interfaces:**
- `art propose-layout`, `art confirm-layout`, `art export-appearance`, `pack validate`, and `pack build` expose the reviewed workflow through Typer.
- `pack build` refuses a failed QA report and emits a JSON result containing the archive, checksum, QA report, and release-notes paths.
- The Mash release project stores metadata/configuration and references only; generated PNGs remain outside Git unless separately approved.

- [ ] **Step 1: Write failing CLI success/refusal tests**

  Assert ambiguous layout exits with code 2 and names the confirmation file, `pack validate` rejects a failed QA report, `pack build` refuses unapproved inputs, a successful build prints all artifact paths as JSON, and commands do not mutate the raw source tree.

- [ ] **Step 2: Run the CLI tests and verify the expected red state**

  Run: `python -m pytest tests/packs/test_cli.py -q`

  Expected: failure because the pack command group and release commands are absent.

- [ ] **Step 3: Implement the command group and metadata-only Mash project**

  Add a `pack` Typer sub-application, preserve all existing commands, route failures to stable non-zero exits without leaking stack traces or absolute source paths, and document the sequence `propose -> confirm -> export -> fill semantic map -> validate -> build`.

- [ ] **Step 4: Run CLI, art, and pack tests plus a dry run**

  Run: `python -m pytest tests/packs tests/art -q`

  Run: `python -m fgo_pet_content.cli pack build content/packs/official.mash --output .tmp/releases --dry-run`

  Expected: validation succeeds and lists required generated inputs without writing a release archive.

- [ ] **Step 5: Commit the release workflow slice**

  ```bash
  git add src/fgo_pet_content/cli.py content/packs tests/packs docs/content-pipeline.md
  git commit -m "feat(phase5): add role pack release workflow"
  ```

### Task 5: Lock Python/.NET compatibility with shared fixtures

**Files:**
- Create: `tests/fixtures/packs/valid-minimal/**`
- Create: `tests/fixtures/packs/invalid-cases/**`
- Modify: `src/FgoPet.Core/Packs/PackContracts.cs`
- Modify: `tests/FgoPet.Core.Tests/Packs/PackContractTests.cs`
- Modify: `tests/FgoPet.Infrastructure.Tests/Packs/PackArchiveBuilder.cs`
- Modify: `tests/FgoPet.Infrastructure.Tests/Packs/FgoPetPackInstallerTests.cs`
- Create: `scripts/test-packaging.ps1`
- Create: `docs/testing/servant-pack-release-checklist.md`

**Interfaces:**
- The Core pack manifest accepts only the versioned capability declarations emitted by Python and rejects unknown capabilities as `ManifestMalformed`.
- The .NET installer validates the same required members, safe paths, app-version floor, appearance references, and production size limits as the Python validator.
- `scripts/test-packaging.ps1` runs Python art/pack tests, Python deterministic rebuild checks, Core/Infrastructure pack tests, and the archive allowlist scan.

- [ ] **Step 1: Add shared fixtures before changing the .NET contract**

  Add one valid minimal package, one valid multi-appearance package, and invalid cases for path traversal, absolute paths, forbidden extensions, hash mismatch, missing required content, unknown required capability, unsupported pack/art schema, incompatible app version, fallback cycle, invisible alpha, overlay overflow, and truncated archives.

- [ ] **Step 2: Add failing parity assertions**

  Add Python tests that map each fixture to an expected acceptance/error category and .NET tests that map the same fixtures to the expected `PackErrorCode`. Add a capability round-trip assertion and preserve current manifests with no capability field as valid for backward compatibility.

- [ ] **Step 3: Implement the narrow Core/.NET compatibility change**

  Add the optional serialized capability list and its known-value validation to `PackManifestV1`, keep `PackJson` strict, and make installer validation reject unknown required capabilities before extraction is committed. Do not broaden allowed archive extensions or add runtime execution hooks.

- [ ] **Step 4: Run the cross-runtime packaging gate**

  Run: `pwsh -File scripts/test-packaging.ps1`

  Expected: Python art/pack tests, deterministic rebuild checks, Core pack tests, Infrastructure pack/appearance tests, and the zero-forbidden-entry scan all pass.

- [ ] **Step 5: Perform a local candidate dry run without publishing**

  Verify the output filename, SemVer, external SHA-256, QA report, preview, release notes, clean extraction, and installation into a temporary Phase 1 app package root. Record any missing human art/persona/knowledge approval as a blocked candidate input rather than marking it approved.

- [ ] **Step 6: Commit the compatibility and acceptance slice**

  ```bash
  git add tests/fixtures/packs src/FgoPet.Core/Packs tests/FgoPet.Core.Tests/Packs tests/FgoPet.Infrastructure.Tests/Packs scripts/test-packaging.ps1 docs/testing/servant-pack-release-checklist.md
  git commit -m "test(phase5): lock role pack cross-runtime compatibility"
  ```

## Verification Gate

- `python -m pytest tests/art tests/packs -q`
- `pwsh -File scripts/test-packaging.ps1`
- `dotnet test tests/FgoPet.Core.Tests/FgoPet.Core.Tests.csproj --no-restore --filter "FullyQualifiedName~Packs"`
- `dotnet test tests/FgoPet.Infrastructure.Tests/FgoPet.Infrastructure.Tests.csproj --no-restore --filter "FullyQualifiedName~Packs"`
- `dotnet build src/FgoPet.App/FgoPet.App.csproj -c Release --no-restore`
- `git diff --check main..HEAD`
- Source/archive scan confirms no executable entries, absolute paths, raw prompts, logs, or generated release directories are tracked.

## Explicitly Deferred

- GUI program installer, Relay/Adapter payload assembly, installation/upgrade rollback UI, and uninstall smoke belong to 5.4B/5.5.
- Human visual QA of a real Mash candidate is required before a package is treated as release-ready; synthetic fixtures only prove tooling behavior.
- Public upload, signing service integration, and formal release publication remain outside this plan's authorization.
