# Phase 5.2 Versioned Backup and Transactional Restore Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a private, versioned `.fgopetbackup` format that can round-trip the supported FGO Pet user state into a clean state root and safely reject or roll back invalid restores.

**Architecture:** Keep shareable export and private restore as separate services and file formats. The private backup is a ZIP containing a manifest, a SQLite-consistent snapshot, a safe settings snapshot, and package references; restore validates in an isolated staging directory, migrates only the staged database, normalizes non-terminal Agent executions without network calls, then swaps database/settings atomically while retaining a rollback copy.

**Tech Stack:** .NET 8, C# 12, WPF, `System.IO.Compression`, `System.Text.Json`, Microsoft.Data.Sqlite, SQLite WAL, xUnit.

**Spec:** `docs/superpowers/specs/2026-09-01-phase5-productization-design.md` sections 6.1–6.5.

## Global Constraints

- Keep shareable `UserDataExportService` and private restore backups as separate extensions, services, manifests, and code paths.
- Backup archive members are exactly `manifest.json`, `runtime.sqlite`, `settings.json`, and `packages.json`; the manifest hashes the three payload members and does not hash itself; no credentials, DPAPI state, absolute paths, logs, screenshots, build outputs, or role-package contents.
- Use a SQLite backup API or equivalent consistent snapshot; never copy an active `-wal` or `-shm` file as the backup database.
- Validate archive paths, duplicate entries, required members, size limits, format version, member hashes, SQLite integrity, schema compatibility, settings, and package references before touching the current state.
- A future backup format or database schema is read-only rejected; no downgrade or best-effort interpretation.
- Restore `dispatching`, `active`, and `attention` executions as `dispatch_outcome_unknown` with the original identity and no network request; never automatically re-execute them.
- Create a current-state rollback backup before replacing state; a failed swap or startup self-check must leave the previous database/settings usable.
- The S2 worktree's existing uncommitted changes are user-owned and must remain intact; do not reset, stash, amend, merge, or commit them without separate authorization.
- This plan and other process artifacts remain local by default and are not part of the eventual product integration.

---

### Task 1: Freeze the private backup contract and safe snapshots

**Files:**
- Create: `src/FgoPet.Core/Backup/BackupContracts.cs`
- Create: `src/FgoPet.Infrastructure/Backup/BackupArchivePolicy.cs`
- Create: `src/FgoPet.Infrastructure/Settings/AppSettingsSnapshotCodec.cs`
- Modify: `src/FgoPet.Infrastructure/Settings/JsonAppSettingsStore.cs`
- Test: `tests/FgoPet.Core.Tests/Backup/BackupContractTests.cs`
- Test: `tests/FgoPet.Infrastructure.Tests/Backup/BackupArchivePolicyTests.cs`
- Test: `tests/FgoPet.Infrastructure.Tests/Settings/AppSettingsSnapshotCodecTests.cs`

**Interfaces:**
- `BackupContracts.cs` produces `BackupFormat.CurrentVersion`, `BackupMember`, `PrivateBackupManifest`, `BackupPackageReferences`, `BackupFailureCode`, and `BackupException`.
- `BackupArchivePolicy` exposes the exact allowed member names, per-member and total byte limits, path validation, and manifest-member validation.
- `AppSettingsSnapshotCodec` exposes `Serialize(AppSettings)` and `Deserialize(string)` and reuses the existing `JsonAppSettingsStore` DTO rules so model metadata remains serializable while API keys remain impossible to represent.

- [ ] **Step 1: Write the failing contract tests**

  Assert that a manifest with format version 1 and the four required members is accepted; a missing member, duplicate path, absolute path, `..` segment, unknown required member, oversized member, invalid SHA-256, or format version 2 is rejected with a stable `BackupFailureCode`.

  Assert that settings serialization contains selection, theme, profile, package settings, model metadata, and Agent allowlist but contains no credential/key field; deserialize a valid snapshot back to an equivalent `AppSettings`.

- [ ] **Step 2: Run the focused RED tests**

  Run:

  ```powershell
  dotnet test tests/FgoPet.Core.Tests/FgoPet.Core.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~BackupContractTests
  dotnet test tests/FgoPet.Infrastructure.Tests/FgoPet.Infrastructure.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~BackupArchivePolicyTests|FullyQualifiedName~AppSettingsSnapshotCodecTests"
  ```

  Expected: the new tests fail because the backup contracts, policy, and codec do not exist.

- [ ] **Step 3: Implement the minimum contracts and codec**

  Use ordinal path comparisons and lowercase hexadecimal SHA-256 validation. Allow only the four exact member names. Move the existing settings DTO conversion into `AppSettingsSnapshotCodec` and make `JsonAppSettingsStore.Load`/`Save` delegate to it without changing its current corrupt-file quarantine behavior.

- [ ] **Step 4: Run the focused GREEN tests**

  Re-run the two focused commands above. Expected: all contract and codec tests pass with no warnings or unrelated file changes.

- [ ] **Step 5: Record the task boundary**

  Run `git diff --check` and inspect only the new Core/Infrastructure files plus the settings codec refactor. Do not commit the worktree; the user requested final unified integration later.

### Task 2: Create a consistent SQLite backup and deterministic archive

**Files:**
- Create: `src/FgoPet.Infrastructure/Backup/RuntimeDatabaseSnapshotService.cs`
- Create: `src/FgoPet.App/Privacy/PrivateBackupService.cs`
- Modify: `src/FgoPet.App/Bootstrap/ServiceRegistration.cs`
- Test: `tests/FgoPet.Infrastructure.Tests/Backup/RuntimeDatabaseSnapshotServiceTests.cs`
- Test: `tests/FgoPet.App.Tests/Privacy/PrivateBackupServiceTests.cs`

**Interfaces:**
- `RuntimeDatabaseSnapshotService.CreateAsync(string destinationPath, CancellationToken)` creates a standalone SQLite file using `SqliteConnection.BackupDatabase` or `VACUUM INTO`, then verifies `PRAGMA integrity_check` and the current schema version without copying WAL/SHM sidecars.
- `PrivateBackupService.CreateAsync(string destinationPath, CancellationToken)` reads the validated `AppSettings` model and `PackIndexV1`, builds `settings.json` and `packages.json`, creates the SQLite snapshot, writes the manifest with stable member ordering and hashes, and atomically replaces the requested destination.

- [ ] **Step 1: Write failing snapshot and archive tests**

  Create a migrated database containing at least one focus session/event/timeline/bond row, a conversation/message/summary, pending and approved memory, Todo, Agent execution including S2 `remote_task_id`, event receipt, and short/long work archive. Create settings with model metadata and Agent target references. Assert that the produced archive has exactly four members, can be opened as a standalone database, has no `runtime.sqlite-wal`/`runtime.sqlite-shm`, and round-trips the selected data.

  Assert that archive bytes are deterministic for the same logical input except for the explicitly recorded creation timestamp, that cancellation removes temporary output, and that an existing destination is replaced only after a complete archive is available.

- [ ] **Step 2: Run the focused RED tests**

  Run:

  ```powershell
  dotnet test tests/FgoPet.Infrastructure.Tests/FgoPet.Infrastructure.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~RuntimeDatabaseSnapshotServiceTests
  dotnet test tests/FgoPet.App.Tests/FgoPet.App.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~PrivateBackupServiceTests
  ```

  Expected: failures identify the missing snapshot and private backup service rather than test setup errors.

- [ ] **Step 3: Implement the consistent snapshot**

  Ensure the destination does not already exist, create its parent directory, open the live database through the existing `RuntimeDatabase`, invoke the SQLite backup API or `VACUUM INTO`, close the source connection, validate the snapshot in read-only mode, and delete the snapshot on any failure. Never expose the source path in a thrown user-facing message.

- [ ] **Step 4: Implement archive assembly and safe serialization**

  Build a staging directory beside the requested archive, write only the four allowed entries, use UTF-8 JSON with explicit property names, normalize ZIP timestamps and entry order, calculate SHA-256 from final member bytes, serialize the manifest last, and use `File.Move(tempArchive, destination, overwrite: true)` only after `ZipArchive.Dispose` succeeds.

- [ ] **Step 5: Run the focused GREEN tests**

  Re-run both focused test commands and `git diff --check`. Verify specifically that the S2 `remote_task_id` column is present in the backup database and that package references contain IDs/versions/appearance IDs only.

### Task 3: Validate staged archives and normalize active Agent executions

**Files:**
- Create: `src/FgoPet.Infrastructure/Backup/PrivateBackupReader.cs`
- Create: `src/FgoPet.Infrastructure/Backup/BackupDatabaseNormalizer.cs`
- Modify: `src/FgoPet.Infrastructure/Persistence/RuntimeDatabaseMigrator.cs` only if a testable current-version helper is needed
- Test: `tests/FgoPet.Infrastructure.Tests/Backup/PrivateBackupReaderTests.cs`
- Test: `tests/FgoPet.Infrastructure.Tests/Backup/BackupDatabaseNormalizerTests.cs`

**Interfaces:**
- `PrivateBackupReader.ReadAndValidateAsync(string backupPath, string stagingDirectory, CancellationToken)` extracts into a newly-created staging directory, rejects unsafe ZIP entries before extraction, checks all manifest lengths/hashes and exact required members, and returns validated staging paths.
- `BackupDatabaseNormalizer.Normalize(RuntimeDatabase stagingDatabase)` runs after staged migrations and changes only non-terminal Agent execution rows to `dispatch_outcome_unknown`, preserving source type, source instance, task ID, dispatch request ID, previous execution ID, and S2 `remote_task_id`; it does not call Relay, Adapter, Codex, or any network service.

- [ ] **Step 1: Write failing reader and normalization tests**

  Cover valid archives, path traversal, absolute paths, symlink-like ZIP entries, duplicate entries, missing required entries, unknown members, individual and total size limits, truncated members, manifest length mismatch, hash mismatch, malformed JSON, future format, future database schema, and SQLite `integrity_check` failure.

  Insert `dispatching`, `active`, `attention`, and `completed` executions into a staged database. Assert only the first three become `dispatch_outcome_unknown`, their identity and `remote_task_id` remain unchanged, completed records remain terminal, and no outbound fake service is invoked.

- [ ] **Step 2: Run the focused RED tests**

  Run:

  ```powershell
  dotnet test tests/FgoPet.Infrastructure.Tests/FgoPet.Infrastructure.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~PrivateBackupReaderTests|FullyQualifiedName~BackupDatabaseNormalizerTests"
  ```

  Expected: the new tests fail because the reader and normalizer are not implemented.

- [ ] **Step 3: Implement extraction and validation**

  Resolve every entry name using ordinal checks before creating files, reject non-file entries and link metadata, enforce limits while streaming, validate manifest JSON before trusting its file list, and delete the staging directory on all validation failures.

- [ ] **Step 4: Implement staged migration and normalization**

  Open the extracted `runtime.sqlite` through a staging `RuntimeDatabase`, run the existing migrator only when its version is older than the current version, reject `RuntimeDatabaseVersionException`, run `PRAGMA integrity_check`, validate settings and package-reference JSON, then execute one SQLite transaction for non-terminal execution normalization.

- [ ] **Step 5: Run the focused GREEN tests**

  Re-run the reader/normalizer tests and the existing `RuntimeDatabaseTests` plus S2-related `SqliteAgentRepositoryTests`. Confirm the staged database reaches schema version 8 and retains `remote_task_id`.

### Task 4: Add rollback-safe restore coordination

**Files:**
- Create: `src/FgoPet.App/Privacy/PrivateBackupRestoreService.cs`
- Create: `src/FgoPet.App/Privacy/BackupRestoreResult.cs`
- Create: `src/FgoPet.App/Privacy/IAppMaintenanceCoordinator.cs`
- Create: `src/FgoPet.App/Privacy/AppMaintenanceCoordinator.cs`
- Modify: `src/FgoPet.App/Bootstrap/ServiceRegistration.cs`
- Modify: `src/FgoPet.App/Bootstrap/DesktopAppShell.cs`
- Modify: `src/FgoPet.Infrastructure/Agents/AgentRelayRuntime.cs` only if the maintenance boundary needs an explicit awaitable stop/restart hook
- Test: `tests/FgoPet.App.Tests/Privacy/PrivateBackupRestoreServiceTests.cs`
- Test: `tests/FgoPet.App.Tests/Bootstrap/DesktopAppShellTests.cs`

**Interfaces:**
- `IAppMaintenanceCoordinator.EnterAsync(CancellationToken)` returns an async lease that serializes backup/restore, stops the Agent runtime before replacement, and blocks a second maintenance operation.
- `PrivateBackupRestoreService.RestoreAsync(string backupPath, CancellationToken)` validates in staging, creates a current-state rollback archive, acquires the maintenance lease, closes/flushes current state, atomically swaps the staged database and settings on the same volume, runs migration and startup self-check, and restores the old state plus a failure report if any post-swap check fails.
- `BackupRestoreResult` reports `Restored`, `Rejected`, or `RolledBack`, a safe error code, whether a package must be reinstalled, and whether Agent pairing must be repeated; it never contains a credential or absolute path.

- [ ] **Step 1: Write failing interruption-boundary tests**

  Use a fake maintenance coordinator and file-swap seam to stop before validation, after staging, after rollback creation, after database rename, after settings rename, and during startup self-check. Assert that rejected input leaves the current DB/settings byte-for-byte unchanged, a failed swap returns to the old state, and a successful restore contains all supported tables and the normalized Agent state.

  Assert that Relay/Adapter stores and Credential Manager are not read or written, Agent runtime is stopped before swap, and no automatic Agent task dispatch occurs after restore.

- [ ] **Step 2: Run the focused RED tests**

  Run:

  ```powershell
  dotnet test tests/FgoPet.App.Tests/FgoPet.App.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~PrivateBackupRestoreServiceTests
  ```

  Expected: failures point to missing restore coordination or an unimplemented swap seam.

- [ ] **Step 3: Implement maintenance lease and rollback state machine**

  Use a process-local semaphore plus a state file in the ignored runtime state directory for the active restore attempt. Keep all replacements on the same volume, use uniquely named staging/rollback paths, fsync/close streams before rename, and preserve the original backup archive when restore fails. Do not delete a previous rollback copy until the new state passes self-check.

- [ ] **Step 4: Implement startup self-check and safe result mapping**

  After a successful swap, run the migrator, `PRAGMA integrity_check`, settings decode, package-reference validation, and a repository-open check. Mark missing package references as a warning rather than a restore failure; leave the App in pack-install guidance until a valid selected package is installed. Keep Agent runtime disabled until the restored setting is explicitly re-applied and pairing is available.

- [ ] **Step 5: Run the focused GREEN tests**

  Re-run restore tests and the existing startup/runtime tests. Then run `git diff --check` and inspect that no test output contains paths, prompts, credentials, or complete request payloads.

### Task 5: Expose backup and restore in the existing Privacy settings surface

**Files:**
- Modify: `src/FgoPet.App/Settings/PrivacyPage.xaml`
- Modify: `src/FgoPet.App/Settings/PrivacyPage.xaml.cs`
- Modify: `src/FgoPet.App/Memory/MemoryViewModel.cs` only if commands need to be moved out of the existing privacy view model
- Modify: `src/FgoPet.App/Bootstrap/ServiceRegistration.cs`
- Test: `tests/FgoPet.Windows.Tests/Settings/SettingsEmbeddedPagesIntegrationTests.cs`
- Test: `tests/FgoPet.App.Tests/Privacy/PrivateBackupServiceTests.cs`

**Interfaces:**
- Keep the existing shareable export controls unchanged and add a clearly separate “私有备份与恢复” card with save path, open path, status, and confirmation actions.
- The page invokes each mutation once, requires an explicit confirmation for restore, shows only safe error codes/messages, and never displays state-file paths, credential text, prompts, or archive contents.

- [ ] **Step 1: Write failing UI contract tests**

  Assert that the Privacy page exposes distinct backup and restore controls, explicit `AutomationProperties.Name`, keyboard focusability, separate `.fgopetbackup` copy, and confirmation text explaining that restore replaces current business data and excludes credentials.

- [ ] **Step 2: Run the focused RED test**

  Run:

  ```powershell
  dotnet test tests/FgoPet.Windows.Tests/FgoPet.Windows.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~SettingsEmbeddedPagesIntegrationTests
  ```

  Expected: the new control assertions fail because the page has no private backup card.

- [ ] **Step 3: Implement the existing-surface controls**

  Reuse the current Privacy settings page and message-box confirmation style. Keep shareable export copy explicit that it cannot be restored. Use file dialogs only for user-selected `.fgopetbackup` paths and pass the selected path to the service; never put internal storage roots into visible text.

- [ ] **Step 4: Run the focused GREEN test and accessibility checks**

  Re-run the Windows integration test and the privacy App tests. Verify the page still loads with the existing fake memory service and the backup controls remain offline-safe when the Agent runtime is unavailable.

### Task 6: End-to-end recovery evidence, documentation, and verification gate

**Files:**
- Create: `tests/FgoPet.EndToEnd.Tests/BackupRestoreEndToEndTests.cs`
- Modify: `README.md`
- Modify: `README.zh-CN.md`
- Create: `docs/reports/2026-09-02-phase5-2-backup-restore-acceptance.md`

- [ ] **Step 1: Add the end-to-end matrix**

  Cover clean-directory round-trip for focus, timeline, bond, dialogue, memory, Todo, Agent executions/receipts, work archives, settings, and package references; active-task normalization; missing package; corrupted archive; future format/schema; size/path violations; partial-write failure; migration failure; startup self-check failure; no credential/absolute-path leakage; and no duplicate Agent execution after restore.

- [ ] **Step 2: Run the focused end-to-end suite**

  ```powershell
  dotnet test tests/FgoPet.EndToEnd.Tests/FgoPet.EndToEnd.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~BackupRestoreEndToEndTests
  ```

- [ ] **Step 3: Update both README languages**

  Document the separate shareable export and private `.fgopetbackup`, supported contents, excluded credentials, restore replacement/rollback behavior, active-task “待核对/中断恢复” behavior, missing package guidance, and the fact that a backup is not a role package.

- [ ] **Step 4: Run the full verification gate**

  ```powershell
  dotnet build FgoPet.sln -c Release --no-restore -warnaserror
  dotnet test FgoPet.sln -c Release --no-build --no-restore
  & 'D:/environments/anaconda/python.exe' -m pytest -q -p no:cacheprovider --basetemp D:/fgo_unpack/.pytest-phase5-2
  git diff --check
  ```

  Report any DPAPI user-profile failures separately from code failures; do not suppress them or claim the full gate passed without a successful exit code.

- [ ] **Step 5: Perform manual Windows acceptance**

  Record clean-directory round-trip, restore rejection, rollback, missing package, offline/no-model behavior, active-task non-reexecution, and Privacy page accessibility evidence in the acceptance report. Keep S2 visible-session evidence and diagnostics separate from the backup archive.

## Plan Completion Gate

- [ ] Private backups and shareable exports are separate and not interchangeable.
- [ ] A backup contains only the four allowed members and a complete manifest with size/hash checks.
- [ ] Database snapshots are consistent and never package active WAL/SHM sidecars.
- [ ] Future versions, corrupted archives, unsafe paths, oversized members, and invalid settings are rejected before current-state mutation.
- [ ] Round-trip restores all specified business data, including S2 `remote_task_id` and Agent archive/reconciliation records.
- [ ] Active Agent executions become explicit unknown/interrupted outcomes and never auto-dispatch.
- [ ] Rollback preserves the old database/settings when any swap or startup check fails.
- [ ] Credentials, DPAPI state, raw role assets, prompts, absolute paths, logs, and screenshots are absent.
- [ ] Both README languages describe the same restore semantics.
- [ ] Release build, relevant .NET tests, Python tests, whitespace checks, and manual Windows evidence are complete before claiming Phase 5.2 done.
