# Phase 4 Runtime Repair Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make Phase 4 pass real Windows/Codex acceptance with one Relay process, durable pairing, complete administration UI, installed adapter command, restart recovery, and verified bidirectional task communication.

**Architecture:** Keep the standalone Relay as the authorization and routing authority. Add a small Windows-only runtime library shared by Relay, Adapter, and Infrastructure for pipe naming, single-instance ownership, process bootstrap, atomic protected state, and framed pipe I/O; complete the existing versioned protocol and drive all app administration through the separate current-user-only control pipe.

**Tech Stack:** .NET 8, C# 12, WPF, `System.IO.Pipes`, Windows mutexes, DPAPI `CurrentUser`, `System.Text.Json`, CommunityToolkit.Mvvm, xUnit, PowerShell, Codex local plugins/MCP stdio.

**Spec:** `docs/superpowers/specs/2026-08-31-phase4-runtime-repair-design.md`

## Global Constraints

- Windows desktop only; all new executable/runtime projects target `net8.0-windows`.
- Keep separate app-control and adapter named pipes with `PipeOptions.CurrentUserOnly`.
- Keep model connection disabled by default.
- Never log or return credentials through the app-control pipe.
- Credentials are random 256-bit values and are protected at rest with DPAPI `CurrentUser`.
- No hard-coded shared credential and no credential command-line argument or environment-variable fallback.
- Codex receives no arbitrary FGO Pet filesystem, shell, or terminal capability.
- FGO Pet reports installation state but never silently installs or updates the plugin.
- Preserve unrelated working-tree changes; inspect each target file immediately before editing it.
- Every behavior is implemented test-first; focused tests pass before the next task begins.

## File Structure

### New shared runtime project

- `src/FgoPet.AgentRuntime/FgoPet.AgentRuntime.csproj` — Windows-only shared runtime assembly.
- `src/FgoPet.AgentRuntime/RelayRuntimeOptions.cs` — validated pipe suffix, state root, executable path, and timeouts.
- `src/FgoPet.AgentRuntime/RelayPipeNames.cs` — canonical current-user pipe and mutex names.
- `src/FgoPet.AgentRuntime/RelaySingleInstance.cs` — owns and releases the user-SID mutex.
- `src/FgoPet.AgentRuntime/RelayProcessBootstrapper.cs` — probes and starts the sibling Relay with injectable launcher/delay.
- `src/FgoPet.AgentRuntime/Security/ISecretProtector.cs` — testable protection boundary.
- `src/FgoPet.AgentRuntime/Security/DpapiSecretProtector.cs` — DPAPI `CurrentUser` implementation.
- `src/FgoPet.AgentRuntime/Storage/AtomicProtectedJsonStore.cs` — atomic JSON read/write and corrupt-file quarantine.
- `src/FgoPet.AgentRuntime/Pipes/JsonLinePipeClient.cs` — bounded single-request pipe client.

### Relay

- `src/FgoPet.AgentRelay/Storage/RelayState.cs` — persisted pending requests, grants, permissions, and decisions.
- `src/FgoPet.AgentRelay/Storage/IRelayStateStore.cs` — persistence interface and in-memory test implementation.
- `src/FgoPet.AgentRelay/Storage/ProtectedRelayStateStore.cs` — production protected state store.
- Existing Relay store, registration, routing, pipe servers, host, and program are modified rather than duplicated.

### Adapter

- `src/FgoPet.CodexAdapter/Relay/AdapterIdentityState.cs` — stable source ID, request nonce, and credential state.
- `src/FgoPet.CodexAdapter/Relay/AdapterIdentityStore.cs` — protected adapter state persistence.
- `src/FgoPet.CodexAdapter/Relay/CodexRelayConnector.cs` — bootstrap, registration, polling, authentication, and reconnect orchestration.
- Existing `CodexRelaySession`, MCP server, and program consume the connector.

### App and UI

- `src/FgoPet.Infrastructure/Agents/AgentRelayAdminClient.cs` — typed control-pipe operations.
- `src/FgoPet.Infrastructure/Agents/AgentRelayRuntimeService.cs` — enable/disable, bootstrap, reconnect, polling, and snapshots.
- `src/FgoPet.Core/Agents/AgentRelayAdministration.cs` — UI-safe status, pending-source, source, and decision records/interfaces.
- Existing service registration, app lifetime, connection view model, page XAML, and code-behind are extended.

### Packaging and acceptance

- `scripts/install-codex-adapter.ps1` — idempotent explicit installer and smoke test.
- `scripts/uninstall-codex-adapter.ps1` — scoped uninstall preserving state by default.
- `scripts/test-phase4.ps1` — Release automated acceptance driver.
- `tests/FgoPet.EndToEnd.Tests/Support/RelayProcessHarness.cs` — isolated real-process harness.
- `docs/testing/phase4-windows-matrix.md` — visible manual acceptance record.

---

### Task 1: Versioned Administration and Registration Contracts

**Files:**
- Modify: `src/FgoPet.AgentProtocol/Messages/RegistrationMessages.cs`
- Create: `src/FgoPet.AgentProtocol/Messages/RelayAdministrationMessages.cs`
- Modify: `src/FgoPet.AgentProtocol/Validation/AgentProtocolValidator.cs`
- Modify: `tests/FgoPet.AgentProtocol.Tests/Fixtures/ProtocolFixtureTests.cs`

**Interfaces:**
- Produces: `RegistrationRequestMessage`, `RegistrationStatusRequest`, `RegistrationStatusResponse`, `AuthenticateRequest`, `RelayConnectionTestResponse`, `PendingSourceDto`, `ApprovedSourceDto`, `RegistrationDecisionRequest`, `UpdatePermissionsRequest`, and `RevokeSourceRequest`.
- Contract invariant: only `RegistrationStatusResponse` on the adapter pipe may contain `Credential`; app-control DTOs have no credential property.

- [ ] **Step 1: Write failing serialization and privacy tests**

```csharp
[Fact]
public void Registration_status_round_trips_one_time_credential()
{
    var value = new RegistrationStatusResponse("approved", "req-1", "codex-abc", "secret", null);
    var copy = ProtocolEnvelope.Create("m1", "registration_status", value)
        .DeserializePayload<RegistrationStatusResponse>();
    Assert.Equal(value, copy);
}

[Fact]
public void App_administration_contracts_cannot_expose_credentials()
{
    Assert.DoesNotContain(typeof(PendingSourceDto).GetProperties(), p => p.Name.Contains("Credential"));
    Assert.DoesNotContain(typeof(ApprovedSourceDto).GetProperties(), p => p.Name.Contains("Credential"));
}
```

- [ ] **Step 2: Run the protocol tests and confirm the new types are missing**

Run: `dotnet test tests/FgoPet.AgentProtocol.Tests/FgoPet.AgentProtocol.Tests.csproj -c Release --no-restore`

Expected: FAIL at compile time for the new contract names.

- [ ] **Step 3: Add the exact immutable contracts and validator cases**

```csharp
public sealed record RegistrationRequestMessage(
    string SourceType, string DisplayName, string SourceInstanceId,
    string AdapterVersion, string ProtocolVersion, string RequestNonce);
public sealed record RegistrationStatusRequest(string RequestId, string SourceInstanceId, string RequestNonce);
public sealed record RegistrationStatusResponse(
    string Status, string RequestId, string? SourceInstanceId, string? Credential, string? Error);
public sealed record AuthenticateRequest(string SourceType, string SourceInstanceId, string Credential);
public sealed record RegistrationDecisionRequest(string RequestId, string Decision);
public sealed record UpdatePermissionsRequest(string SourceType, IReadOnlyList<string> AllowedTargetIds, bool Enabled);
public sealed record RevokeSourceRequest(string SourceType, string SourceInstanceId);
```

Validate bounded non-empty identifiers, known decision values, 64-character nonces, protocol compatibility, opaque target IDs, and the existing forbidden-text rules. Keep `ProtocolEnvelope.CurrentProtocolVersion` unchanged unless a fixture proves a breaking wire change.

- [ ] **Step 4: Run tests and commit the contract boundary**

Run: `dotnet test tests/FgoPet.AgentProtocol.Tests/FgoPet.AgentProtocol.Tests.csproj -c Release --no-restore`

Expected: PASS.

```powershell
git add src/FgoPet.AgentProtocol tests/FgoPet.AgentProtocol.Tests
git commit -m "feat(agent): add pairing administration contracts"
```

### Task 2: Shared Windows Runtime, Single Instance, and Bootstrap

**Files:**
- Create: `src/FgoPet.AgentRuntime/FgoPet.AgentRuntime.csproj`
- Create: `src/FgoPet.AgentRuntime/RelayRuntimeOptions.cs`
- Create: `src/FgoPet.AgentRuntime/RelayPipeNames.cs`
- Create: `src/FgoPet.AgentRuntime/RelaySingleInstance.cs`
- Create: `src/FgoPet.AgentRuntime/RelayProcessBootstrapper.cs`
- Create: `src/FgoPet.AgentRuntime/Pipes/JsonLinePipeClient.cs`
- Create: `tests/FgoPet.AgentRuntime.Tests/FgoPet.AgentRuntime.Tests.csproj`
- Create: `tests/FgoPet.AgentRuntime.Tests/RelaySingleInstanceTests.cs`
- Create: `tests/FgoPet.AgentRuntime.Tests/RelayProcessBootstrapperTests.cs`
- Modify: `FgoPet.sln`

**Interfaces:**
- Produces: `RelayRuntimeOptions`, `RelayPipeNames.ForCurrentUser(options)`, `IRelayProbe`, `IRelayProcessLauncher`, `IRuntimeDelay`, `RelayProcessBootstrapper.EnsureReadyAsync`, and `RelayBootstrapResult`.
- Consumers: Relay `Program`, adapter connector, and app runtime service.

- [ ] **Step 1: Add the test project and failing mutex/bootstrap tests**

```csharp
[Fact]
public void Only_one_owner_can_acquire_the_same_mutex()
{
    using var first = RelaySingleInstance.TryAcquire("Local\\FgoPet.Test." + Guid.NewGuid().ToString("N"));
    using var second = RelaySingleInstance.TryAcquire(first.Name);
    Assert.True(first.IsOwner);
    Assert.False(second.IsOwner);
}

[Fact]
public async Task Bootstrap_starts_once_then_waits_for_both_pipes()
{
    var probe = new SequenceProbe(false, false, true);
    var launcher = new RecordingLauncher();
    var result = await new RelayProcessBootstrapper(probe, launcher, new ImmediateDelay())
        .EnsureReadyAsync(TestOptions(), CancellationToken.None);
    Assert.Equal(RelayBootstrapStatus.Ready, result.Status);
    Assert.Single(launcher.Starts);
}
```

- [ ] **Step 2: Run tests and verify missing runtime types**

Run: `dotnet test tests/FgoPet.AgentRuntime.Tests/FgoPet.AgentRuntime.Tests.csproj -c Release`

Expected: FAIL at compile time.

- [ ] **Step 3: Implement validated options and stable names**

```csharp
public sealed record RelayRuntimeOptions(
    string PipeSuffix, string StateRoot, string RelayExecutablePath,
    TimeSpan ConnectTimeout, TimeSpan StartupTimeout);

public sealed record RelayPipeSet(string Adapter, string App, string Mutex);
```

Use the Windows identity SID hashed with SHA-256 for the mutex component. Keep existing pipe prefixes and use `PipeSuffix` for isolated tests. Reject suffixes outside `[A-Za-z0-9._-]` or longer than 64 characters.

- [ ] **Step 4: Implement ownership, hidden process launch, bounded probing, and JSON-line I/O**

`RelaySingleInstance.TryAcquire` must create the mutex with initial ownership and release it only when `IsOwner`. `DefaultRelayProcessLauncher` uses `ProcessStartInfo.UseShellExecute = false`, `CreateNoWindow = true`, and passes explicit `--pipe-suffix` and `--state-root` arguments. `JsonLinePipeClient` enforces connect/read timeouts and a 1 MiB response limit.

- [ ] **Step 5: Run tests, add projects to the solution, and commit**

Run: `dotnet test tests/FgoPet.AgentRuntime.Tests/FgoPet.AgentRuntime.Tests.csproj -c Release`

Expected: PASS.

```powershell
git add FgoPet.sln src/FgoPet.AgentRuntime tests/FgoPet.AgentRuntime.Tests
git commit -m "feat(agent): add relay runtime bootstrap"
```

### Task 3: Protected Atomic State and Durable Relay Grants

**Files:**
- Create: `src/FgoPet.AgentRuntime/Security/ISecretProtector.cs`
- Create: `src/FgoPet.AgentRuntime/Security/DpapiSecretProtector.cs`
- Create: `src/FgoPet.AgentRuntime/Storage/AtomicProtectedJsonStore.cs`
- Modify: `src/FgoPet.AgentRuntime/FgoPet.AgentRuntime.csproj`
- Create: `src/FgoPet.AgentRelay/Storage/RelayState.cs`
- Create: `src/FgoPet.AgentRelay/Storage/IRelayStateStore.cs`
- Create: `src/FgoPet.AgentRelay/Storage/ProtectedRelayStateStore.cs`
- Modify: `src/FgoPet.AgentRelay/Storage/RelayStore.cs`
- Create: `tests/FgoPet.AgentRuntime.Tests/AtomicProtectedJsonStoreTests.cs`
- Create: `tests/FgoPet.AgentRelay.Tests/ProtectedRelayStateStoreTests.cs`

**Interfaces:**
- `ISecretProtector.Protect(ReadOnlySpan<byte>) -> byte[]`; `Unprotect(ReadOnlySpan<byte>) -> byte[]`.
- `IRelayStateStore.Load() -> RelayState`; `Save(RelayState state)`.
- `RelayState` contains pending requests, decisions, approved grants, source permissions, and schema version; transient queues and online flags remain memory-only.

- [ ] **Step 1: Write failing persistence, restart, revoke, and corruption tests**

```csharp
[Fact]
public void Grant_and_revocation_survive_store_recreation()
{
    using var root = new TempDirectory();
    var first = CreateStore(root.Path);
    first.SaveGrant(TestGrant("credential"));
    Assert.NotNull(CreateStore(root.Path).Authenticate("credential"));
    first.Revoke("codex", "codex-instance");
    Assert.Null(CreateStore(root.Path).Authenticate("credential"));
}

[Fact]
public void Corrupt_state_is_quarantined_and_never_approved()
{
    File.WriteAllText(StatePath, "not-json");
    Assert.Empty(CreateStore(StateRoot).ListGrants());
    Assert.Single(Directory.GetFiles(StateRoot, "relay-state.v1.json.corrupt-*"));
}
```

- [ ] **Step 2: Run focused tests and confirm failure**

Run: `dotnet test tests/FgoPet.AgentRuntime.Tests/FgoPet.AgentRuntime.Tests.csproj -c Release --no-restore`

Run: `dotnet test tests/FgoPet.AgentRelay.Tests/FgoPet.AgentRelay.Tests.csproj -c Release --no-restore`

Expected: FAIL because protected stores do not exist.

- [ ] **Step 3: Implement DPAPI and atomic replacement**

Add `System.Security.Cryptography.ProtectedData` version `8.0.0`. `DpapiSecretProtector` calls `ProtectedData.Protect/Unprotect` with fixed application entropy and `DataProtectionScope.CurrentUser`. Write `*.tmp`, flush with `Flush(true)`, then `File.Move(temp, target, true)`. On JSON/decryption/schema failure, atomically rename to `*.corrupt-<UTC timestamp>` and return an empty state.

- [ ] **Step 4: Make `RelayStore` persist every authoritative mutation**

Load state in the constructor. After add/refresh pending, decision, grant, permission change, and revoke, capture an immutable snapshot under `_gate`, release the lock, and save it. Never persist inbound/outbound queues, dedupe keys, or live-online flags.

- [ ] **Step 5: Run focused tests and commit**

Run: `dotnet test tests/FgoPet.AgentRuntime.Tests/FgoPet.AgentRuntime.Tests.csproj -c Release --no-restore`

Run: `dotnet test tests/FgoPet.AgentRelay.Tests/FgoPet.AgentRelay.Tests.csproj -c Release --no-restore`

Expected: PASS.

```powershell
git add src/FgoPet.AgentRuntime src/FgoPet.AgentRelay/Storage tests/FgoPet.AgentRuntime.Tests tests/FgoPet.AgentRelay.Tests
git commit -m "feat(agent): persist protected relay grants"
```

### Task 4: Complete Pairing, Authentication, Revocation, and Resilient Pipe Servers

**Files:**
- Modify: `src/FgoPet.AgentRelay/Registration/RegistrationService.cs`
- Modify: `src/FgoPet.AgentRelay/Routing/RelayRouter.cs`
- Modify: `src/FgoPet.AgentRelay/Pipes/AdapterPipeServer.cs`
- Modify: `src/FgoPet.AgentRelay/Pipes/AppPipeServer.cs`
- Modify: `src/FgoPet.AgentRelay/RelayHost.cs`
- Modify: `src/FgoPet.AgentRelay/Program.cs`
- Modify: `src/FgoPet.AgentRelay/FgoPet.AgentRelay.csproj`
- Modify: `tests/FgoPet.AgentRelay.Tests/RegistrationServiceTests.cs`
- Modify: `tests/FgoPet.AgentRelay.Tests/RelayPipeIntegrationTests.cs`
- Create: `tests/FgoPet.AgentRelay.Tests/RelayHostLifetimeTests.cs`

**Interfaces:**
- Consumes Task 1 contracts and Task 2 runtime ownership/options.
- Produces fully functional adapter operations `registration_request`, `registration_status`, `authenticate`, `connection_test`; control operations `pending_sources`, `decide_registration`, `list_sources`, `update_permissions`, `revoke_source`, `connection_test`.

- [ ] **Step 1: Write failing pairing and one-time credential tests**

Test that request identity+nonce is idempotent, approve creates a default-disabled/default-empty-allowlist grant, matching polls repeat the same credential until authentication succeeds, the next poll returns `approved` with `Credential == null`, a wrong nonce gets `unauthorized`, reject/expiry never reveal credentials, and revocation cancels queued work.

```csharp
var request = new RegistrationRequestMessage("codex", "Codex", "instance-1", "1.0", "1", Nonce);
var pending = service.Request(request, now);
service.Decide(pending.RequestId, RegistrationDecision.Approve, now.AddSeconds(1));
Assert.NotNull(service.Poll(pending.RequestId, "instance-1", Nonce, now.AddSeconds(2)).Credential);
var credential = service.Poll(pending.RequestId, "instance-1", Nonce, now.AddSeconds(3)).Credential!;
service.Authenticate("codex", "instance-1", credential, now.AddSeconds(4));
Assert.Null(service.Poll(pending.RequestId, "instance-1", Nonce, now.AddSeconds(5)).Credential);
```

- [ ] **Step 2: Write failing listener resilience and single-owner process tests**

Connect a raw client, send malformed JSON, disconnect, then connect a valid client and assert `connection_test` succeeds. Start two `RelayHost` instances with the same mutex/pipe set and assert only one reports owner and both pipes belong to that owner.

- [ ] **Step 3: Run Relay tests and verify failures**

Run: `dotnet test tests/FgoPet.AgentRelay.Tests/FgoPet.AgentRelay.Tests.csproj -c Release --no-restore`

Expected: FAIL on missing operations and listener resilience.

- [ ] **Step 4: Implement registration and router behavior**

Generate credentials with `RandomNumberGenerator.GetBytes(32)`, compare decoded bytes with `CryptographicOperations.FixedTimeEquals`, key grants by `sourceType/sourceInstance`, persist decisions and permissions, and remove the constructor-wide `_credential` from both pipe servers.

- [ ] **Step 5: Implement isolated connection handling**

Both servers loop on listener creation. After accepting, call `HandleConnectionAsync` inside a `try/catch` that converts known validation/authentication failures to typed JSON errors and contains `IOException`, `JsonException`, timeout, and peer disconnect. Cancellation exits the host; a client error returns to accept the next client.

- [ ] **Step 6: Replace positional CLI credentials with explicit runtime arguments**

`Program` accepts only `--pipe-suffix <value>` and `--state-root <path>`, builds `RelayRuntimeOptions`, acquires the mutex before creating `RelayHost`, and exits `0` when not owner. No credential appears in args or environment.

- [ ] **Step 7: Run Relay suite and commit**

Run: `dotnet test tests/FgoPet.AgentRelay.Tests/FgoPet.AgentRelay.Tests.csproj -c Release --no-restore`

Expected: PASS.

```powershell
git add src/FgoPet.AgentRelay tests/FgoPet.AgentRelay.Tests
git commit -m "feat(agent): complete resilient relay pairing"
```

### Task 5: Durable Adapter Identity, Registration, and Reconnect

**Files:**
- Create: `src/FgoPet.CodexAdapter/Relay/AdapterIdentityState.cs`
- Create: `src/FgoPet.CodexAdapter/Relay/AdapterIdentityStore.cs`
- Create: `src/FgoPet.CodexAdapter/Relay/CodexRelayConnector.cs`
- Modify: `src/FgoPet.CodexAdapter/Relay/CodexRelaySession.cs`
- Modify: `src/FgoPet.CodexAdapter/Mcp/CodexMcpServer.cs`
- Modify: `src/FgoPet.CodexAdapter/Program.cs`
- Modify: `src/FgoPet.CodexAdapter/FgoPet.CodexAdapter.csproj`
- Create: `tests/FgoPet.CodexAdapter.Tests/AdapterIdentityStoreTests.cs`
- Create: `tests/FgoPet.CodexAdapter.Tests/CodexRelayConnectorTests.cs`
- Modify: `tests/FgoPet.CodexAdapter.Tests/CodexMcpServerTests.cs`

**Interfaces:**
- `ICodexRelayConnector.EnsureAuthenticatedAsync(CancellationToken) -> AdapterConnectionResult`.
- `ICodexRelayConnector.SendEventAsync(...)` and `PollDispatchesAsync(...)` replace direct credential construction.
- `AdapterConnectionStatus`: `Connected`, `ApprovalRequired`, `Rejected`, `Revoked`, `RelayOffline`, `VersionMismatch`.

- [ ] **Step 1: Write failing identity persistence and approval-required MCP tests**

```csharp
[Fact]
public async Task First_run_requests_pairing_and_mcp_stays_alive()
{
    var connector = new FakeConnector(AdapterConnectionStatus.ApprovalRequired, "req-1");
    var response = await new CodexMcpServer(connector, "task-1").HandleAsync(CallToolJson);
    Assert.Contains("approval_required", response);
}
```

Also prove the stable source instance and request nonce survive store recreation, the credential is protected rather than plaintext, restart authenticates without a new request, and a definitive revoked response clears only the credential.

- [ ] **Step 2: Run adapter tests and verify failure**

Run: `dotnet test tests/FgoPet.CodexAdapter.Tests/FgoPet.CodexAdapter.Tests.csproj -c Release --no-restore`

Expected: FAIL for missing connector/store.

- [ ] **Step 3: Implement connector state machine**

On every MCP/hook startup: bootstrap Relay, load/create identity, authenticate when a credential exists, otherwise request or poll registration. Persist a credential only after matching instance+nonce approval. On transient I/O failure retain credentials; on explicit `revoked` clear the credential and return `Revoked`.

- [ ] **Step 4: Remove credential and source-instance environment dependencies**

`Program` may retain `FGO_PET_PIPE_SUFFIX` and `FGO_PET_STATE_ROOT` only for isolated tests. Remove `FGO_PET_ADAPTER_CREDENTIAL` and `FGO_PET_AGENT_INSTANCE`. Construct the connector from validated runtime options and share it with MCP and hook paths.

- [ ] **Step 5: Run adapter tests and commit**

Run: `dotnet test tests/FgoPet.CodexAdapter.Tests/FgoPet.CodexAdapter.Tests.csproj -c Release --no-restore`

Expected: PASS.

```powershell
git add src/FgoPet.CodexAdapter tests/FgoPet.CodexAdapter.Tests
git commit -m "feat(agent): add durable adapter pairing"
```

### Task 6: Typed App Administration Client and Runtime Lifecycle

**Files:**
- Create: `src/FgoPet.Core/Agents/AgentRelayAdministration.cs`
- Create: `src/FgoPet.Infrastructure/Agents/AgentRelayAdminClient.cs`
- Create: `src/FgoPet.Infrastructure/Agents/AgentRelayRuntimeService.cs`
- Modify: `src/FgoPet.Infrastructure/Agents/AgentRelayClient.cs`
- Modify: `src/FgoPet.Infrastructure/Agents/AgentReconnectService.cs`
- Modify: `src/FgoPet.Infrastructure/FgoPet.Infrastructure.csproj`
- Modify: `src/FgoPet.App/Bootstrap/ServiceRegistration.cs`
- Modify: `src/FgoPet.App/Bootstrap/AppStartup.cs`
- Create: `tests/FgoPet.Infrastructure.Tests/Agents/AgentRelayAdminClientTests.cs`
- Create: `tests/FgoPet.Infrastructure.Tests/Agents/AgentRelayRuntimeServiceTests.cs`
- Modify: `tests/FgoPet.App.Tests/Bootstrap/ServiceRegistrationTests.cs`

**Interfaces:**
- `IAgentRelayAdministration` exposes `GetSnapshotAsync`, `ApproveAsync`, `RejectAsync`, `UpdatePermissionsAsync`, `RevokeAsync`, and `TestConnectionAsync`.
- `AgentRelayRuntimeSnapshot` includes typed `AgentRelayRuntimeStatus`, pending sources, approved sources, diagnostic code, and `ObservedAtUtc`.
- `IAgentRelayRuntimeService.SetEnabledAsync(bool)` and `SnapshotChanged` drive the app lifecycle/UI.

- [ ] **Step 1: Write failing typed-client tests**

```csharp
[Fact]
public async Task Approve_sends_only_request_id_and_decision()
{
    await client.ApproveAsync("req-1", CancellationToken.None);
    Assert.Equal("decide_registration", transport.LastEnvelope.MessageType);
    Assert.DoesNotContain("credential", transport.LastEnvelope.ToJson(), StringComparison.OrdinalIgnoreCase);
}
```

Cover each operation, protocol mismatch, Relay offline, and cancellation.

- [ ] **Step 2: Write failing lifecycle tests**

Prove disabled startup does not launch Relay; enabling calls bootstrap once and begins bounded polling; disabling cancels polling and sends `enabled=false`; shutdown completes without orphan tasks; a Relay restart transitions `RelayOffline -> Connected` through capped backoff.

- [ ] **Step 3: Run Infrastructure/App bootstrap tests and verify failure**

Run: `dotnet test tests/FgoPet.Infrastructure.Tests/FgoPet.Infrastructure.Tests.csproj -c Release --no-restore`

Run: `dotnet test tests/FgoPet.App.Tests/FgoPet.App.Tests.csproj -c Release --no-restore`

Expected: FAIL for missing administration/runtime types.

- [ ] **Step 4: Implement typed control operations and status mapping**

Use `JsonLinePipeClient`; never expose raw `JsonDocument` above Infrastructure. Map exact statuses to `Disabled`, `RelayOffline`, `AwaitingApproval`, `AdapterOffline`, `AuthenticationFailed`, `VersionMismatch`, and `Connected`.

- [ ] **Step 5: Register and start the runtime through app lifecycle**

Register one `RelayProcessBootstrapper`, `AgentRelayAdminClient`, and `AgentRelayRuntimeService`. At app startup, call `SetEnabledAsync(settings.AgentConnection.Enabled)`. Dispose/cancel it during existing application shutdown. Keep UI dispatch on the WPF dispatcher.

- [ ] **Step 6: Run focused tests and commit**

Run: `dotnet test tests/FgoPet.Infrastructure.Tests/FgoPet.Infrastructure.Tests.csproj -c Release --no-restore`

Run: `dotnet test tests/FgoPet.App.Tests/FgoPet.App.Tests.csproj -c Release --no-restore`

Expected: PASS.

```powershell
git add src/FgoPet.Core/Agents src/FgoPet.Infrastructure/Agents src/FgoPet.Infrastructure/FgoPet.Infrastructure.csproj src/FgoPet.App/Bootstrap tests/FgoPet.Infrastructure.Tests tests/FgoPet.App.Tests/Bootstrap
git commit -m "feat(agent): connect app relay lifecycle"
```

### Task 7: Complete the Settings Approval and Permission Workflow

**Files:**
- Modify: `src/FgoPet.App/ViewModels/AgentConnectionSettingsViewModel.cs`
- Modify: `src/FgoPet.App/Views/Settings/AgentConnectionSettingsView.xaml`
- Modify: `src/FgoPet.App/Views/Settings/AgentConnectionSettingsView.xaml.cs`
- Modify: `tests/FgoPet.App.Tests/Settings/AgentConnectionSettingsViewModelTests.cs`
- Modify: `tests/FgoPet.Windows.Tests/Settings/SettingsEmbeddedPagesIntegrationTests.cs`
- Modify: `tests/FgoPet.Windows.Tests/Settings/SettingsWindowIntegrationTests.cs`

**Interfaces:**
- Consumes `IAgentRelayAdministration` and runtime snapshots from Task 6.
- Produces async commands `RefreshCommand`, `TestConnectionCommand`, `ApproveCommand`, `RejectCommand`, `SavePermissionsCommand`, and `RevokeCommand`.

- [ ] **Step 1: Write failing view-model workflow tests**

Test pending request rendering, approve/reject refresh, connection-test status+timestamp, allowlist replacement, revoke confirmation callback, typed error display, and command disabling while busy.

```csharp
await viewModel.ApproveCommand.ExecuteAsync(pending);
Assert.Equal("req-1", admin.ApprovedRequestId);
Assert.Empty(viewModel.PendingSources);
Assert.Equal(AgentRelayRuntimeStatus.Connected, viewModel.RuntimeStatus);
```

- [ ] **Step 2: Write failing WPF structure tests**

Load the real page on an STA thread and assert named elements `TestConnectionButton`, `PendingSourcesList`, `ApprovedSourcesList`, `PermissionsList`, and `RevokeButton` exist. Assert no new top-level settings navigation item was added.

- [ ] **Step 3: Run App and Windows settings tests and verify failure**

Run: `dotnet test tests/FgoPet.App.Tests/FgoPet.App.Tests.csproj -c Release --no-restore`

Run: `dotnet test tests/FgoPet.Windows.Tests/FgoPet.Windows.Tests.csproj -c Release --no-restore`

Expected: FAIL for missing commands/elements.

- [ ] **Step 4: Implement view-model commands and immutable snapshot projection**

Keep provider-model configuration in `ModelConnectionViewModel`; keep Agent authorization in `AgentConnectionSettingsViewModel`. Use observable item view models for editable allowlist entries and marshal snapshot changes to the dispatcher. Do not infer state from localized text.

- [ ] **Step 5: Implement the existing page layout**

Add compact sections to the existing Agent Connection page in this order: master switch/status, connection test, pending approvals, approved sources, target permissions, installation guidance. Reuse existing theme resources and controls. Approval/rejection is explicit; revoke uses the existing confirmation pattern. Leave the provider Model Connection page and the existing eight top-level navigation items unchanged.

- [ ] **Step 6: Run UI tests and commit**

Run: `dotnet test tests/FgoPet.App.Tests/FgoPet.App.Tests.csproj -c Release --no-restore`

Run: `dotnet test tests/FgoPet.Windows.Tests/FgoPet.Windows.Tests.csproj -c Release --no-restore`

Expected: PASS.

```powershell
git add src/FgoPet.App/ViewModels src/FgoPet.App/Views/Settings src/FgoPet.App/Settings tests/FgoPet.App.Tests/Settings tests/FgoPet.Windows.Tests/Settings
git commit -m "feat(agent): add pairing controls to settings"
```

### Task 8: Installable Adapter Command and Codex Plugin

**Files:**
- Create: `scripts/install-codex-adapter.ps1`
- Create: `scripts/uninstall-codex-adapter.ps1`
- Create: `tests/FgoPet.EndToEnd.Tests/AdapterInstallerTests.cs`
- Modify: `integrations/codex/fgo-pet-agent/.codex-plugin/plugin.json`
- Modify: `integrations/codex/fgo-pet-agent/.mcp.json`
- Modify: `integrations/codex/fgo-pet-agent/hooks/hooks.json`
- Modify: `docs/guides/codex-adapter.md`

**Interfaces:**
- Installer parameters: `-InstallRoot`, `-CodexHome`, `-SkipPathUpdate`, and `-SkipPluginInstall` for isolated tests.
- Uninstaller parameters: same roots plus `-RemoveState`; state is preserved when omitted.
- Installed command: `<InstallRoot>\fgo-pet-codex-adapter.cmd` executes sibling `FgoPet.CodexAdapter.exe` with all original arguments.

- [ ] **Step 1: Write failing installer idempotency tests**

The test invokes the installer twice against temporary roots, asserts one PATH entry, exact shim contents, copied adapter+Relay executables, valid plugin manifest, and an MCP initialize/tools-list response through the shim. Invoke uninstaller and assert plugin/shim removal while adapter state remains.

- [ ] **Step 2: Run installer tests and verify the scripts are missing**

Run: `dotnet test tests/FgoPet.EndToEnd.Tests/FgoPet.EndToEnd.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~AdapterInstallerTests`

Expected: FAIL because installer files are missing.

- [ ] **Step 3: Implement scoped idempotent scripts**

Resolve all paths with `GetFullPath`; reject an empty or filesystem-root install target. Publish Release executables, write the shim, update only the user PATH entry owned by the installer, then call the available Codex plugin install/update command. Every external command checks `$LASTEXITCODE` and exits non-zero on failure.

- [ ] **Step 4: Update plugin metadata and hooks**

Keep `.mcp.json` command as `fgo-pet-codex-adapter` with `mcp`. Hooks call the same installed command and never pass credentials. Update plugin version and instructions to describe approval-required state and explicit installation.

- [ ] **Step 5: Run tests, validator, and commit**

Run: `dotnet test tests/FgoPet.EndToEnd.Tests/FgoPet.EndToEnd.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~AdapterInstallerTests`

Run: `codex plugin validate integrations/codex/fgo-pet-agent`

Expected: both PASS.

```powershell
git add scripts/install-codex-adapter.ps1 scripts/uninstall-codex-adapter.ps1 integrations/codex/fgo-pet-agent tests/FgoPet.EndToEnd.Tests/AdapterInstallerTests.cs docs/guides/codex-adapter.md
git commit -m "feat(agent): package codex adapter command"
```

### Task 9: Real-Process Pairing, Communication, Restart, and Revocation Tests

**Files:**
- Create: `tests/FgoPet.EndToEnd.Tests/Support/RelayProcessHarness.cs`
- Modify: `tests/FgoPet.EndToEnd.Tests/AgentIntegrationEndToEndTests.cs`
- Modify: `tests/FgoPet.EndToEnd.Tests/FgoPet.EndToEnd.Tests.csproj`

**Interfaces:**
- `RelayProcessHarness.StartRelayAsync`, `StartAdapterAsync`, `SendAppAsync`, `SendMcpAsync`, `StopAsync`, and `DisposeAsync`.
- Every harness instance owns a GUID pipe suffix and temporary state root and records child PIDs for guaranteed scoped cleanup.

- [ ] **Step 1: Replace in-process acceptance with failing real-process tests**

Add tests named:

```text
Concurrent_bootstrap_has_exactly_one_relay_owner
Malformed_client_does_not_break_next_connection
Pairing_approval_authentication_and_dispatch_complete_across_processes
Credential_survives_relay_and_adapter_restart
Revocation_is_immediate_and_survives_restart
Allowlist_change_blocks_the_next_dispatch
```

For the communication test, send MCP `initialize`, `tools/list`, and task-tool calls over adapter stdio; approve through the app pipe; dispatch a task; submit `task_started` and `task_completed`; assert the app pipe receives monotonically sequenced events.

- [ ] **Step 2: Run the new tests and observe the first missing runtime behavior**

Run: `dotnet test tests/FgoPet.EndToEnd.Tests/FgoPet.EndToEnd.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~AgentIntegrationEndToEndTests`

Expected: FAIL before any process-level gaps are patched.

- [ ] **Step 3: Implement the isolated harness**

Build executable paths from `AppContext.BaseDirectory`, launch with `UseShellExecute=false`, redirect stdio, and pass only `FGO_PET_PIPE_SUFFIX`/`FGO_PET_STATE_ROOT`. Use readiness polling rather than sleeps. On disposal, terminate only recorded PIDs that have not exited and delete only the verified temporary state root.

- [ ] **Step 4: Fix only defects exposed by the process tests**

For each failure, add a focused unit regression test in the owning project, make the minimal implementation correction, rerun that focused test, then rerun the real-process test. Do not weaken assertions or increase timeouts beyond the bounded design values to hide races.

- [ ] **Step 5: Run the complete E2E project and commit**

Run: `dotnet test tests/FgoPet.EndToEnd.Tests/FgoPet.EndToEnd.Tests.csproj -c Release --no-restore`

Expected: PASS with no leftover Relay/Adapter process whose command line contains the test suffix.

```powershell
git add tests/FgoPet.EndToEnd.Tests src/FgoPet.AgentRuntime src/FgoPet.AgentRelay src/FgoPet.CodexAdapter src/FgoPet.Infrastructure
git commit -m "test(agent): verify real process integration"
```

### Task 10: Automated Release Gate and Visible Real-Machine Acceptance

**Files:**
- Create: `scripts/test-phase4.ps1`
- Create: `docs/testing/phase4-windows-matrix.md`
- Modify: `docs/guides/agent-integration.md`
- Modify: `docs/reports/2026-08-30-phase4-agent-integration-handoff.md`
- Modify: `README.md`

**Interfaces:**
- `scripts/test-phase4.ps1` runs restore, Release build, all tests, plugin validation, isolated install, MCP smoke, and cleanup; any failed command exits non-zero.
- The manual matrix records timestamp, build commit, executable paths, PIDs, screenshots, pairing source ID, test-connection result, dispatch ID, event sequence, restart result, and revocation result without credentials or prompt bodies.

- [ ] **Step 1: Write the release driver with explicit gates**

```powershell
dotnet restore FgoPet.sln
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet build FgoPet.sln -c Release --no-restore -warnaserror
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet test FgoPet.sln -c Release --no-build
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
```

Continue with plugin validation and a temporary-root installer/MCP smoke. Cleanup must target only paths/PIDs created by the script.

- [ ] **Step 2: Run the automated Release gate**

Run: `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/test-phase4.ps1`

Expected: zero warnings, zero errors, all test assemblies pass, plugin validates, installed shim returns MCP initialize/tools-list, and cleanup succeeds.

- [ ] **Step 3: Perform visible app/Codex acceptance**

Launch the Release app and installed plugin. Capture evidence for: pending request shown; approve; connected test; allowlist save; dispatch; progress; completion; app+adapter+Relay restart recovery; revoke; revoked reconnect rejection; exactly one Relay PID. Use only synthetic task text.

- [ ] **Step 4: Record evidence and operational guidance**

Update the matrix and handoff with exact results and artifact paths. Update README/guides with install, pair, test, revoke, restart, uninstall, and troubleshooting steps. State Phase 4 accepted only if every automated and visible row passes.

- [ ] **Step 5: Run documentation/working-tree checks and commit**

Run: `git diff --check`

Run: `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/test-phase4.ps1`

Expected: both PASS.

```powershell
git add scripts/test-phase4.ps1 docs/testing/phase4-windows-matrix.md docs/guides/agent-integration.md docs/reports/2026-08-30-phase4-agent-integration-handoff.md README.md
git commit -m "docs(agent): record phase 4 acceptance"
```

## Completion Checklist

- [ ] Review `git status --short` and confirm no unrelated user changes were staged or overwritten.
- [ ] Confirm all ten task commits exist or document why commits were intentionally withheld.
- [ ] Confirm Release build reports zero warnings and zero errors.
- [ ] Confirm every solution test passes, including real-process tests.
- [ ] Confirm plugin validation and installed-command MCP smoke pass.
- [ ] Confirm visible pairing, test connection, dispatch, progress, completion, restart recovery, permission change, and revocation evidence exists.
- [ ] Confirm exactly one Relay owner and no acceptance-only processes or installations remain.
- [ ] Run the required post-implementation simplify/harden review before claiming completion.
