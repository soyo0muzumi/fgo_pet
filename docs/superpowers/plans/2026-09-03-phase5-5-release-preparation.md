# Phase 5.5 Release Preparation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Produce a reproducible Windows x64 release bundle with explicit runtime requirements, integrity metadata, and a repeatable local acceptance gate without publishing or changing user machine state.

**Architecture:** The release bundle is built from the existing WPF App and its Relay/Adapter companions using the existing .NET publish path. A deterministic manifest and SHA-256 inventory describe only the application payload; role packages remain separate artifacts. PowerShell scripts provide build, verification, and isolated acceptance orchestration, while documentation records the supported environment and known limitations.

**Tech Stack:** .NET 8 WPF, PowerShell 7, existing Python content tooling, JSON, SHA-256, Windows x64.

**Spec:** `docs/superpowers/specs/2026-09-01-phase5-productization-design.md`, sections 8.2 and 9.

## Global Constraints

- Target only Windows x64 for the first candidate; do not claim Windows 10/11 support until the selected environment is verified.
- Use framework-dependent `net8.0-windows` publishing and state the required .NET 8 Desktop Runtime explicitly.
- Keep App binaries and `.fgopetpack` role resources in separate output roots.
- Do not include credentials, pairing state, user data, logs, screenshots, source trees, or development artifacts.
- Release scripts may be used for development and acceptance only; they must not silently modify PATH, Codex configuration, or user data.
- Do not upload, sign, publish, or create an automatic updater in this plan.
- Every generated file must have a stable relative path and SHA-256 entry in the release manifest.

---

### Task 1: Freeze the release contract

**Files:**
- Create: `docs/release/support-matrix.md`
- Create: `src/FgoPet.App/Properties/PublishProfiles/win-x64-release.pubxml`
- Test: `scripts/test-packaging.ps1`

**Interfaces:**
- Consumes: `src/FgoPet.App/FgoPet.App.csproj` and the existing Relay/Adapter project references.
- Produces: one named `win-x64` framework-dependent publish profile and a documented runtime/support contract consumed by the release scripts.

- [ ] **Step 1: Write the release contract document**

  Document Windows x64, .NET 8 Desktop Runtime, offline App startup, optional Agent components, role-package separation, default data retention on uninstall, and the exact unsupported scenarios for the first candidate.

- [ ] **Step 2: Add the publish profile**

  Configure `RuntimeIdentifier=win-x64`, `SelfContained=false`, `Configuration=Release`, `PublishSingleFile=false`, and `IncludeNativeLibrariesForSelfExtract=false`. Keep companion binaries available as separate files so the existing `CopySiblingRelay`/`PublishSiblingRelay` targets remain authoritative.

- [ ] **Step 3: Add a profile smoke assertion**

  Extend the existing packaging gate so it checks that the profile exists, targets `win-x64`, is framework-dependent, and does not request single-file bundling.

- [ ] **Step 4: Run the focused gate**

  Run: `pwsh -NoProfile -File scripts/test-packaging.ps1`

  Expected: Python, Core, Infrastructure, and profile checks pass.

- [ ] **Step 5: Commit**

  ```powershell
  git add docs/release/support-matrix.md src/FgoPet.App/Properties/PublishProfiles/win-x64-release.pubxml scripts/test-packaging.ps1
  git commit -m "build(release): define win-x64 candidate contract"
  ```

### Task 2: Build and verify the deterministic application bundle

**Files:**
- Create: `scripts/publish-release.ps1`
- Create: `scripts/verify-release.ps1`
- Create: `tests/release/test_release_manifest.py`

**Interfaces:**
- Consumes: the Task 1 publish profile and an optional external role-package directory.
- Produces: `<output>/app/FgoPet-win-x64-<version>.zip`, `manifest.json`, `SHA256SUMS`, and a verification result without modifying the source tree.

- [ ] **Step 1: Write manifest and boundary tests**

  Cover stable relative POSIX paths, lowercase SHA-256 values, duplicate-path rejection, forbidden extensions/names, missing hash rejection, and the rule that role-package files are not accepted under the App payload root.

- [ ] **Step 2: Run the tests to verify the new contract fails**

  Run: `D:\fgo_unpack\.venv-phase5-4a\Scripts\python.exe -m pytest -q tests/release/test_release_manifest.py`

  Expected: FAIL because the release manifest verifier does not exist yet.

- [ ] **Step 3: Implement isolated publishing**

  `publish-release.ps1` must publish the App with the Task 1 profile into a generated staging directory, copy only the expected App/Relay/Adapter payload, reject credentials and development files, create a fixed-order manifest and SHA256SUMS file, and atomically move the completed candidate to the output directory. `-OutputRoot` is required for automated use; no default path may point inside the repository.

- [ ] **Step 4: Implement verification**

  `verify-release.ps1` must validate the manifest, hashes, archive contents, required executable set, absence of role resources in the App archive, and the documented runtime identifier. It must return a non-zero exit code with a safe error message on any mismatch.

- [ ] **Step 5: Make the tests pass**

  Run: `D:\fgo_unpack\.venv-phase5-4a\Scripts\python.exe -m pytest -q tests/release/test_release_manifest.py`

  Expected: all release manifest tests pass.

- [ ] **Step 6: Commit**

  ```powershell
  git add scripts/publish-release.ps1 scripts/verify-release.ps1 tests/release/test_release_manifest.py
  git commit -m "build(release): create verified application bundle"
  ```

### Task 3: Add an isolated release-candidate acceptance gate

**Files:**
- Create: `scripts/test-release-candidate.ps1`
- Create: `docs/testing/release-candidate-checklist.md`
- Create: `docs/release/README.md`

**Interfaces:**
- Consumes: the verified App archive from Task 2, the existing adapter install/uninstall scripts, and a caller-provided temporary root.
- Produces: a pass/fail acceptance report; no changes to the caller’s PATH, Codex home, pairing state, or business database.

- [ ] **Step 1: Define the acceptance matrix**

  Record clean extraction, offline executable presence, MCP smoke using isolated `FGO_PET_STATE_ROOT` and `FGO_PET_PIPE_SUFFIX`, role-package separation, upgrade simulation, failed verification, and uninstall-state preservation. Mark real Windows GUI install, sleep/resume, DPI, multi-monitor, and long-running checks as manual evidence items.

- [ ] **Step 2: Implement the isolated gate**

  Require `-CandidateRoot` and `-TempRoot`; reject roots that resolve to the repository or a filesystem root. Extract into a generated temporary directory, invoke `verify-release.ps1`, run the existing adapter MCP smoke with isolated environment variables, and clean only the generated temporary directory in a `finally` block.

- [ ] **Step 3: Document operator commands**

  Document the exact build, verify, and acceptance commands, expected artifacts, supported runtime, manual evidence fields, and the explicit fact that this gate is not public release authorization.

- [ ] **Step 4: Run the isolated gate**

  Run: `pwsh -NoProfile -File scripts/test-release-candidate.ps1 -CandidateRoot <candidate> -TempRoot <temporary-root>`

  Expected: verification and isolated MCP smoke pass; no user profile or repository files change.

- [ ] **Step 5: Commit**

  ```powershell
  git add scripts/test-release-candidate.ps1 docs/testing/release-candidate-checklist.md docs/release/README.md
  git commit -m "test(release): add isolated candidate acceptance gate"
  ```

### Task 4: Final release-preparation verification

**Files:**
- Modify: `docs/superpowers/plans/2026-09-03-phase5-closeout.md`
- Modify: `docs/superpowers/plans/2026-09-01-project-progress-roadmap.md` only if the pre-existing working copy is explicitly staged later

**Interfaces:**
- Consumes: the release candidate artifacts and all prior gates.
- Produces: a dated release-preparation result with reproducible commands and explicit manual evidence gaps.

- [ ] **Step 1: Run the full code and packaging gates**

  Run: `D:\fgo_unpack\.venv-phase5-4a\Scripts\python.exe -m pytest -q`, `pwsh -NoProfile -File scripts/test-packaging.ps1`, and `dotnet build src/FgoPet.App/FgoPet.App.csproj -c Release --no-restore`.

- [ ] **Step 2: Build and verify one candidate**

  Run the publish, verify, and isolated acceptance commands against a temporary output root. Record the candidate version, archive SHA-256, runtime identifier, and result.

- [ ] **Step 3: Review boundaries**

  Scan the candidate archive for credentials, absolute paths, role resources, source files, logs, scripts, and executables outside the approved runtime payload. Confirm no user data or Codex state was touched.

- [ ] **Step 4: Update the closeout record**

  Add the verified candidate evidence and remaining manual checks to `docs/superpowers/plans/2026-09-03-phase5-closeout.md`; do not claim public release readiness until the manual Windows evidence is attached.

- [ ] **Step 5: Commit**

  ```powershell
  git add docs/superpowers/plans/2026-09-03-phase5-closeout.md
  git commit -m "docs(release): record candidate verification"
  ```

## Self-review

- The plan keeps role packages outside the App archive and leaves signing/upload/automatic update out of scope.
- All paths are explicit; generated output is required to be outside the repository.
- The release scripts have separate build, verify, and acceptance responsibilities.
- Manual Windows evidence is called out rather than inferred from automated tests.
