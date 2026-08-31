# Phase 4 Runtime Repair Design

**Date:** 2026-08-31  
**Status:** Approved by user  
**Scope:** Windows desktop app, Agent Relay, Codex adapter, local plugin packaging, and real-process acceptance

## 1. Purpose

Phase 4 has a sound protocol boundary and a passing unit/integration suite, but it does not yet satisfy real-machine acceptance. The repair completes the runtime path without weakening the original privacy or authorization model.

The required outcome is:

1. FGO Pet and the Codex adapter can independently ensure that one user-level Relay is running.
2. A new adapter requests registration, the app shows the request, and the user can approve or reject it.
3. An approved adapter stores a durable per-source credential and reconnects after all processes restart.
4. Codex can dispatch a task, receive progress and completion events, and observe cancellation or rejection correctly.
5. The app can test a connection, edit target permissions, and revoke a source.
6. Revocation takes effect immediately and remains effective after restart.
7. The repository provides a reproducible installation path for the adapter command and Codex plugin.

## 2. Confirmed Runtime Gaps

The repair is based on real Windows acceptance evidence, not only test doubles:

- Two Relay processes can remain alive simultaneously, each owning one named pipe. This creates a split-brain runtime.
- Registration grants and source credentials are process-local. Approval, polling, restart recovery, and revocation are therefore not real end-to-end operations.
- The desktop app does not start or reconnect to the Relay, so its default runtime exposes no Relay pipes.
- The plugin declares `fgo-pet-codex-adapter`, but the command is not installed or resolvable from `PATH`.
- The settings page lacks pending-request approval, connection testing, revocation, and target-permission editing.
- A client disconnect or malformed exchange can leave the adapter or listener half-alive instead of returning a bounded protocol error and accepting the next connection.

## 3. Constraints and Non-Goals

### Constraints

- Keep the existing two-pipe trust boundary: an app-control pipe and an adapter pipe.
- Keep named pipes restricted to the current Windows user.
- Do not disable registration or authentication to simplify acceptance.
- Do not introduce a shared hard-coded credential.
- Do not grant Codex arbitrary FGO Pet filesystem, shell, or terminal access.
- Preserve the existing task/event protocol wherever compatible; add explicit versioned messages only where required.
- Keep model connection disabled by default.
- Do not change top-level navigation or unrelated desktop behavior.

### Non-goals

- FGO Pet itself will not silently install or update Codex plugins.
- Phase 4 will not add remote-network transport, multi-user sharing, cloud identity, or administrator services.
- Phase 4 will not make Relay a Windows service.
- Phase 4 will not migrate unverified credentials from the current in-memory prototype. Existing prototype sources must pair again once.

## 4. Chosen Architecture

The existing standalone Relay remains the authority for registration, credentials, source status, permissions, and task routing. Both the app and adapter use a small bootstrap component to ensure that the Relay executable is running.

```text
Codex MCP host
    |
    | stdio / MCP
    v
Codex adapter ---- adapter pipe ----+
                                     |
                              user-level Relay
                                     |
FGO Pet app ------ control pipe -----+
```

This is preferred over embedding Relay in the app because Codex may start the adapter while the app is closed. It is preferred over trusting every adapter process because pairing, revocation, and per-source permissions are Phase 4 requirements rather than optional UI state.

## 5. Relay Ownership and Single Instance

### 5.1 User-level mutex

Relay acquires a Windows mutex before opening either pipe. Its name is versioned and derived from the current user's SID, for example:

`Local\FgoPet.AgentRelay.<sid-hash>.v1`

The SID is hashed before inclusion to keep the object name bounded and log-safe. If the mutex already exists, the second Relay exits successfully with an `already_running` result. It never opens only one of the two pipes.

### 5.2 Atomic startup

The owner process creates both listeners as one host lifecycle. If either listener cannot be created during startup, it closes the other listener, releases the mutex, records a bounded diagnostic, and exits non-zero. A single client failure is contained to that connection and does not terminate the listener.

### 5.3 Bootstrap

`RelayProcessBootstrapper` is used by the app and adapter:

1. Probe the expected pipe with a short timeout.
2. If unavailable, start the sibling `FgoPet.AgentRelay.exe` without a visible console window.
3. Poll readiness with a bounded timeout.
4. Return a typed status: `Ready`, `StartFailed`, `TimedOut`, or `VersionMismatch`.

The launcher and clock are injectable so tests do not start uncontrolled processes. Production resolves Relay relative to the installed application/adapter directory; it does not guess arbitrary locations on `PATH`.

Concurrent app/adapter startup is safe because the mutex chooses one Relay owner. The losing Relay exits normally while both callers continue polling the same pipes.

## 6. Durable Registration and Authorization

### 6.1 State ownership

Relay owns the authoritative source records and target permissions. Adapter owns only its source identifier and issued credential.

Production state lives under:

- Relay: `%LOCALAPPDATA%\FgoPet\AgentRelay\relay-state.v1.json`
- Adapter: `%LOCALAPPDATA%\FgoPet\CodexAdapter\adapter-state.v1.json`

Tests override both roots and pipe suffixes so real-process tests are isolated and parallel-safe.

### 6.2 At-rest protection

Credentials are random 256-bit values generated with a cryptographic random-number generator. Credential bytes are protected with Windows DPAPI using `CurrentUser` scope before they are written. Files are replaced atomically through a temporary file in the same directory.

Relay stores only the protected credential, source metadata, approval state, timestamps, and permission values. Logs never contain credentials, prompt bodies, or task result bodies.

### 6.3 Registration flow

1. Adapter starts with a stable random `source_instance_id` but no credential.
2. Adapter sends `registration_request` with source name, source instance, adapter version, protocol version, and a random request nonce.
3. Relay creates or refreshes a pending request with a ten-minute expiry and returns a request identifier.
4. The app retrieves pending requests through the control pipe.
5. The user approves or rejects a request. Approval creates a new per-source credential and default-deny target permissions.
6. Adapter polls `registration_status`. Relay returns the credential over the current-user-only adapter pipe to the matching source instance and nonce.
7. Adapter protects and persists the credential, then authenticates with it. That successful authentication consumes the pending credential delivery.

The credential is returned only for the matching source instance and request nonce. Repeated polling before successful authentication is idempotent, which makes a lost response recoverable. Expired, rejected, or authenticated/consumed requests never reveal a credential.

### 6.4 Authentication and revocation

Before authentication, the adapter pipe accepts only protocol negotiation, registration request, and registration polling. Task and status operations require the source credential.

Relay compares credentials in constant time. Revocation deletes the authoritative grant, cancels active tasks for that source with a revoked reason, and rejects all later sessions immediately. Restart does not restore a revoked grant.

The app-control pipe remains separate and current-user-only. It does not accept adapter credentials and never returns them. This preserves the original local-user administrative boundary without creating a second shared secret that both desktop processes would need to distribute.

## 7. Protocol Additions

All envelopes retain the existing protocol version field and correlation identifier. The following operations are added or completed:

| Pipe | Operation | Purpose |
|---|---|---|
| Adapter | `registration_request` | Create or refresh a pending source request |
| Adapter | `registration_status` | Poll pending/rejected/expired/approved state and consume credential once |
| Adapter | `authenticate` | Establish an authenticated source session |
| Adapter | `connection_test` | Verify version, authentication, and Relay-to-app availability |
| Control | `pending_sources` | List unexpired pending requests |
| Control | `decide_registration` | Approve or reject one request |
| Control | `list_sources` | List approved sources and live status without credentials |
| Control | `update_permissions` | Replace the source target allowlist atomically |
| Control | `revoke_source` | Revoke a source and cancel its active tasks |
| Control | `connection_test` | Return Relay, adapter, app-handler, and protocol status |

Unknown operations return a versioned `unsupported_operation` response. Malformed frames, oversized payloads, timeouts, and disconnected peers produce bounded errors and close only the offending connection.

## 8. Desktop App Integration

### 8.1 Lifecycle

When model connection is enabled, the app ensures Relay is running and opens a reconnecting control session. Disabling model connection closes the session and stops accepting new task dispatch, but does not kill a Relay that may still serve Codex registration/status requests.

The reconnect loop uses capped exponential backoff and is cancelled on app shutdown. UI state is updated through immutable snapshots so background pipe callbacks never mutate WPF controls directly.

### 8.2 Settings page

The existing Agent Connection page is completed without adding new top-level navigation. Provider/model configuration remains on the separate Model Connection page. The Agent Connection page contains:

- Master enable switch and current runtime status.
- A `Test connection` action with a human-readable result and timestamp.
- Pending requests with source identity, age, expiry, and approve/reject actions.
- Approved sources with online/offline/authentication/version state.
- Per-source target allowlist editing with explicit save feedback.
- Revoke action with confirmation and immediate status refresh.
- Adapter/plugin installation status and a link or command-copy affordance for the repository installer; the app does not run installation silently.

Status is represented by typed states rather than inferred strings: `Disabled`, `RelayOffline`, `AwaitingApproval`, `AdapterOffline`, `AuthenticationFailed`, `VersionMismatch`, and `Connected`.

## 9. Adapter and Plugin Deployment

### 9.1 Adapter runtime

The adapter starts Relay through the same bootstrapper, loads its DPAPI-protected local state, and completes registration when no valid credential exists. MCP initialization remains available while approval is pending; task tools return a structured `approval_required` result rather than crashing or hanging.

The adapter reconnects after a Relay restart and re-authenticates with its durable credential. A revoked adapter clears its local credential after a definitive revoked response and creates a fresh pending request only when the user initiates reconnection or the next MCP session starts.

### 9.2 Reproducible installation

The repository provides an explicit user-run PowerShell installer and uninstaller. The installer:

1. Publishes or copies Release adapter and Relay binaries to `%LOCALAPPDATA%\FgoPet\bin`.
2. Creates `fgo-pet-codex-adapter.cmd` in that directory.
3. Adds that exact directory to the user `PATH` only when absent.
4. Installs or updates the local Codex marketplace/plugin definition.
5. Validates the plugin manifest and executes an MCP initialize/tools-list smoke test through the installed command.

The installer is idempotent and prints when a Codex restart is required for the updated user `PATH`. The uninstaller removes the plugin registration and shim it owns, while preserving Relay pairing state unless the user explicitly requests state removal.

The settings page reports whether the installed shim resolves and whether the detected adapter protocol is compatible. It provides guidance only; installation remains an explicit user action.

## 10. Failure Handling and Diagnostics

- Every connection has independent cancellation, frame-size limits, read/write timeouts, and exception containment.
- Relay startup failures include a stable error code, executable path, and pipe readiness state, but no credential or task content.
- Correlation identifiers connect adapter, Relay, and app diagnostics.
- State corruption moves the invalid file to a timestamped quarantine name and starts with no trusted grants. It never silently treats corrupted state as approved.
- Version mismatch is surfaced separately from offline or authentication failure.
- A crashed Relay can be restarted by either client; durable grants survive and in-flight tasks terminate with a clear interrupted reason.

## 11. Verification Strategy

### 11.1 Test-first slices

Each behavior begins with a failing test before implementation:

1. Relay mutex and atomic dual-pipe startup.
2. Per-connection failure containment.
3. Durable pending/approved/revoked state and DPAPI round trip.
4. Registration request, approval, one-time credential delivery, and authentication.
5. App and adapter bootstrap/reconnect behavior.
6. Settings commands and state transitions.
7. Installed command and plugin smoke path.

### 11.2 Real-process integration tests

A Windows-only test harness launches published executables with unique pipe suffixes and temporary state roots. It proves:

- A concurrent second Relay cannot create split ownership.
- A disconnected or malformed client does not stop later clients.
- Adapter request appears in the app-control API, approval reaches the adapter, and authentication succeeds.
- A credential works after Relay and adapter restart.
- Test connection reports all runtime layers accurately.
- Codex-shaped MCP input dispatches a task and receives progress plus completion events.
- Revocation rejects the current source and remains rejected after restart.
- Target allowlist changes affect subsequent dispatch immediately.

Unit tests continue to use in-memory stores and fake launchers. Real-process tests never use the developer's normal pipes or `%LOCALAPPDATA%` state.

### 11.3 Final acceptance gate

Phase 4 is accepted only when all of the following are captured from a Release build on Windows:

- Restore, build, analyzers, and all test assemblies pass with zero warnings and zero errors.
- Plugin validation passes.
- The installed `fgo-pet-codex-adapter` command completes MCP initialize and tools/list.
- The real app visibly shows a pending request, approves it, tests connected, edits permissions, and revokes it.
- A real dispatch produces progress and completion in both Codex and FGO Pet.
- App, adapter, and Relay restarts preserve authorization; revocation also persists.
- Process inspection shows exactly one Relay owner.
- Temporary acceptance installations and test state are removed or explicitly documented for the user.

## 12. Implementation Boundaries

The work is divided into five ordered boundaries:

1. **Relay correctness:** mutex, listener resilience, durable grant store, and completed protocol handlers.
2. **Client lifecycle:** shared bootstrap contract, app reconnecting control client, adapter durable registration/authentication.
3. **Settings workflow:** pending approval, connection test, allowlist, revoke, and installation state.
4. **Packaging:** installed adapter command, plugin installer/uninstaller, and manifest/runtime validation.
5. **Acceptance:** Windows real-process automation followed by a visible real-app/Codex verification pass.

Each boundary must compile and pass its focused tests before the next begins. Existing unrelated working-tree changes are preserved; repair edits are limited to the Phase 4 projects, their tests, scripts, and documentation.

## 13. Decision Summary

- Keep Relay standalone and make it a real user-level single instance.
- Let both app and adapter ensure Relay availability through one injectable bootstrap contract.
- Persist grants and adapter credentials with Windows CurrentUser protection.
- Complete registration and administration through the two existing current-user-only pipes.
- Keep plugin installation explicit, reproducible, and outside automatic app startup.
- Treat real-process Windows tests and installed-command smoke tests as release gates, not optional manual checks.
