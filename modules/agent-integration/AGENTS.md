# agent-integration Agent Rules

## Scope

This file applies to this module directory tree and inherits the repository root `AGENTS.md`.

## Ownership

Own Agent protocol, Relay, Runtime, Codex Adapter, source authorization, project-target authorization, dispatch, reconciliation, archive, pairing, and replay protection.

## Boundaries and dependencies

- Allowed dependencies: `foundation`, `ui-foundation` where needed, and published narrow contracts consumed by `desktop-shell` and `todo`.
- Forbidden dependencies: feature-module implementations, direct access to feature-private tables, and unauthorized process or project-target control.
- Cross-module calls must use explicit contracts and deny-by-default authorization.

## Safety invariants

Agent payloads must not carry prompts, reasoning, tool arguments, terminal output, credentials, or local file paths.

Sources and project targets remain deny-by-default and require explicit authorization.

Relay and Adapter replay protection, acknowledgement, reconciliation, and archive safety cannot be weakened.

Protocol, pairing, protected state, dispatch, archive, and process-launch changes require their owning unit tests plus the relevant end-to-end and Phase 4 checks.

## Minimum validation

Documentation changes require Markdown and diff checks. Module changes require all four owned unit-test projects. Protocol, pairing, protected-state, dispatch, archive, reconciliation, or process-launch changes additionally require the relevant `FgoPet.EndToEnd.Tests` coverage and `pwsh -File scripts/test-phase4.ps1`; run the release build for solution or project-boundary changes.
