## [HEAL-20260831-005] stage-c-build-recovery

**Status**: verified
**Area**: build
**Pattern-Key**: build.audit_process_lock_and_control_type

Release build was blocked by PID 36080, verified via Win32_Process as this worktree's Relay with an isolated debug pipe and temporary state. Stop-Process failed internally; guarded Invoke-CimMethod Terminate returned 0. Subsequent build exposed CS0173 in dialog focus selection; explicit Control type fixed it. Original App Release build then passed with 0 warnings/errors. No user data removed.

## [HEAL-20260831-004] stage-b-wire-fixture-and-wpf-readonly-binding

**Logged**: 2026-08-31
**Status**: verified
**Area**: focused Stage B verification

### Failure and fix
- New App wire test supplied a 32-character nonce; the production protocol requires 64 hexadecimal characters. Corrected only the fixture, not validation.
- Windows STA layout rejected TwoWay binding from `Run.Text` to read-only status properties. Set all display-only Run bindings on the Agent Connection page to `Mode=OneWay`; the test includes both pending and approved source rows.

### Verification
- `dotnet test tests/FgoPet.EndToEnd.Tests/FgoPet.EndToEnd.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~AppRelayControlTests`: 1/1 passed after correction.
- `dotnet test tests/FgoPet.Windows.Tests/FgoPet.Windows.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~AgentConnectionPageTests`: 1/1 passed after correction.
- App settings/dispatch/startup subset: 15/15; no repeated full suite.

**Pattern-Key**: wpf.readonly_run_binding
**Recurrence-Count**: 1

---

## [HEAL-20260831-001] bundled-sdd-bash-crlf-on-windows

**Logged**: 2026-08-31
**Status**: abandoned
**Trigger**: tool-failure
**Area**: agent-workflow
**Priority**: low

### Failure
The bundled `sdd-workspace` script failed through WSL Bash. Sandbox invocation returned E_ACCESS_DENIED; the authorized retry reached `/usr/bin/env: 'bash\r': No such file or directory` and exited 1.

### Diagnosis
The bundled shell script has CRLF line endings, so its shebang is not executable under WSL. Product source/build behavior is unrelated.

### Fix
Do not modify the plugin cache. Abandon the bundled invocation and generate the same plan-scoped artifacts with PowerShell reads and apply_patch. Directory convention remains `.superpowers/sdd/<plan-basename>/`; `.superpowers/sdd/.gitignore` contains `*`.

### Verification
Original Bash operation was not repaired. Native artifact creation was checked separately: Test-Path(progress.md) returned True and git check-ignore reported the self-ignoring rule. This is not claimed as a successful Bash rerun.

### Metadata
- Pattern-Key: env.sdd_bash_crlf_windows
- Recurrence-Count: 1
- First-Seen / Last-Seen: 2026-08-31

---

## [HEAL-20260831-002] runtime-missing-json-namespace

**Logged**: 2026-08-31
**Status**: verified
**Trigger**: tool-failure
**Active-Context**: phase4 Task 2 runtime implementation
**Area**: build
**Priority**: low

### Failure
The focused `dotnet test tests/FgoPet.AgentRuntime.Tests/FgoPet.AgentRuntime.Tests.csproj -c Release --no-restore` build reported CS0103: `JsonException` did not exist in the current context in `RelayProcessBootstrapper.cs`.

### Diagnosis
The exception filter referenced `System.Text.Json.JsonException` without importing its namespace.

### Fix
Added `using System.Text.Json;` to `src/FgoPet.AgentRuntime/RelayProcessBootstrapper.cs`.

### Verification
The same focused command completed successfully with 12/12 tests passing, 0 failures, and 0 skips.

### Metadata
- Related Files: src/FgoPet.AgentRuntime/RelayProcessBootstrapper.cs
- Pattern-Key: build.missing_namespace_import
- Recurrence-Count: 1
- First-Seen / Last-Seen: 2026-08-31

---

## [HEAL-20260831-003] runtime-span-local-in-async

**Logged**: 2026-08-31
**Status**: verified
**Trigger**: tool-failure
**Active-Context**: phase4 Task 2 runtime implementation
**Area**: build
**Priority**: low

### Failure
The focused build reported CS4012: an async method in `JsonLinePipeClient.cs` could not declare a `Span<byte>` local variable.

### Diagnosis
The bounded reader kept a `Span<byte>` local across an async method scope, which C# disallows for ref structs in async state machines.

### Fix
Replaced the local span with `Array.IndexOf` and an inline `AsSpan(...).CopyTo(...)`, preserving bounded frame processing without an async-local ref struct.

### Verification
The same focused command completed successfully with 12/12 tests passing, 0 failures, and 0 skips.

### Metadata
- Related Files: src/FgoPet.AgentRuntime/Pipes/JsonLinePipeClient.cs
- Pattern-Key: build.async_ref_struct_local
- Recurrence-Count: 1
- First-Seen / Last-Seen: 2026-08-31
