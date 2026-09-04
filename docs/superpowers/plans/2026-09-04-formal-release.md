# FGO Pet 0.1.0 Formal Release Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Produce a locally verifiable Windows x64 0.1.0 formal release candidate with a frozen Mash role package, a WiX per-user GUI installer, and an indexed screenshot evidence archive.

**Architecture:** Keep the application installer and role package as separate artifacts. Build the app using the existing `win-x64-release` publish flow, install the app and its runtime components through a WiX per-user bundle, and install the signed/frozen role package through the existing package path without embedding role assets in application binaries. Store acceptance evidence under an internal release-evidence directory and keep `docs/guides/` user-facing only.

**Tech Stack:** .NET/WPF, PowerShell release scripts, existing FGO Pet pack schema v3, WiX 5.0.2 per-user installer, SHA-256 manifests, existing .NET/Python test suites.

**Spec:** `docs/testing/servant-pack-release-checklist.md`, `docs/content-pipeline.md`, `README.md` release requirements, and the Phase 3 `ExpressionSemanticKeys.Core` contract.

## Global Constraints

- The eight core expression keys are exactly `neutral`, `happy`, `excited`, `shy`, `concerned`, `sad`, `surprised`, and `angry`.
- The Mash source is `D:\\fgo_unpack\\fgo_assets\\pet\\mash\\casual`; the 28 source expression assets remain available for the visual cycle, while the eight core keys define LLM/runtime semantic routing.
- The role package remains a separate `.fgopetpack` artifact and must pass the existing pack allowlist, schema, hash, dimension, fallback, and deterministic-build checks.
- The installer uses WiX 5.0.2, is per-user, and must not require administrator elevation for normal install or uninstall.
- Existing user data must survive application upgrade and uninstall; unrelated dirty worktree changes must not be reset or deleted.
- Voice, file management, and 0.1.1 conversation-history behavior are explicitly out of scope.

---

### Task 1: Freeze the Mash role package

**Files:**
- Modify: `content/packs/official.mash/package.json`
- Modify: `content/packs/official.mash/expression-semantics.json`
- Create: `content/packs/official.mash/appearances/casual/manifest.json`
- Create: `content/packs/official.mash/appearances/casual/runtime/full_body.png`
- Create: `content/packs/official.mash/appearances/casual/runtime/expressions/r01c01.png` through `r07c04.png`
- Create: `content/packs/official.mash/previews/library.png`
- Create or modify: `content/packs/official.mash/persona/*` using only approved Mash persona artifacts
- Test: existing pack tests and `scripts/test-packaging.ps1`

**Interfaces:**
- Consumes: `D:\\fgo_unpack\\fgo_assets\\pet\\mash\\casual\\manifest.json`, runtime PNGs, and approved persona/knowledge files under `D:\\fgo_unpack\\fgo_assets\\story_cache\\persona\\mash`.
- Produces: a complete pack source tree with the eight semantic mappings: neutral→r01c04, happy→r01c01, excited→r01c02, shy→r07c01, concerned→r06c04, sad→r05c02, surprised→r04c02, angry→r02c01.

- [ ] Confirm every copied image hash and dimensions against the source manifest.
- [ ] Confirm the package file allowlist contains every archive member and excludes raw source trees, databases, prompts, logs, scripts, and executables.
- [ ] Run the pack validator and existing pack tests; stop and fix the pack source if any schema or visual QA error occurs.
- [ ] Generate a deterministic `.fgopetpack` artifact and SHA-256 file using the repository packaging script.
- [ ] Record the final package version and hash in the release evidence index.

### Task 2: Add the WiX 4 per-user GUI installer

**Files:**
- Create: `installer/FgoPet.Installer/FgoPet.Installer.wixproj`
- Create: `installer/FgoPet.Installer/Package.wxs`
- Create: `installer/FgoPet.Installer/Assets/*` for installer metadata/icons only
- Modify: `scripts/publish-release.ps1`
- Create: `scripts/build-installer.ps1`
- Create: `scripts/test-installer.ps1`
- Modify: `README.md` and `docs/release/0.1.0-rc-notes.md`

**Interfaces:**
- Consumes: the existing published `FgoPet.App` and runtime artifacts from the release publish directory.
- Produces: a versioned per-user EXE/MSI installer that installs the app, relay, adapter, shortcuts, and uninstall registration without embedding the separate role package.

- [ ] Pin the WiX toolset version and make the build script fail with a clear toolchain message when it is unavailable.
- [ ] Define stable upgrade identity, per-user install scope, executable shortcuts, ARP metadata, and a rollback-safe component layout.
- [ ] Add installer smoke checks for clean install, app launch, upgrade over the prior candidate, uninstall, and preservation of user data.
- [ ] Ensure the installer output is copied into the release directory beside the ZIP and role package, with SHA-256 checksums.
- [ ] Run the installer script in an elevated/appropriate Windows test environment and save the machine-readable report.

### Task 3: Archive screenshot evidence and closeout records

**Files:**
- Create: `docs/internal/release-evidence/0.1.0/evidence-index.md`
- Create: `docs/internal/release-evidence/0.1.0/checksums.txt`
- Copy: accepted local screenshots into `docs/internal/release-evidence/0.1.0/screenshots/`
- Copy or reference: `Person real test and feedback.md` under the evidence archive without secrets
- Modify: `docs/roadmap.md`
- Modify: `CHANGELOG.md`
- Modify: `docs/release/0.1.0-rc-notes.md`

**Interfaces:**
- Consumes: the user-provided screenshots/feedback, release hashes, automated reports, and installer test report.
- Produces: an internal evidence index with test item, result, source file, date, environment, and explicit N/A reasons for unavailable second monitor and absent upgrade package.

- [ ] Assign stable evidence IDs and map each accepted screenshot to one or more release checks.
- [ ] Mark uninstall as pending until the user performs the real cleanup verification; do not claim it passed from simulation alone.
- [ ] Keep credentials, tokens, local account identifiers, and unrelated screenshots out of the archive.
- [ ] Run `git diff --check` and confirm only intended release files are part of the closeout set.

### Task 4: Final release gate

**Files:**
- Modify only release metadata if a gate reveals a version/hash mismatch.
- Test: all existing test projects, packaging gate, release gate, installer gate.

- [ ] Run `dotnet test ... --no-restore` for the existing solution test projects.
- [ ] Run the pack packaging and release-candidate scripts.
- [ ] Run installer install/upgrade/uninstall verification.
- [ ] Verify every final artifact has a matching SHA-256 and that the release notes distinguish automated pass, manual pass, N/A, and pending checks.
- [ ] Do not label the build publicly released until the user completes the remaining manual uninstall screenshot check.
