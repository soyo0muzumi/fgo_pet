# focus Agent Rules

## Scope

This file applies to this module directory tree and inherits the repository root `AGENTS.md`.

## Ownership

Own focus sessions, state transitions, recovery, timeline entries, bond progression, focus persistence, and focus feedback.

## Boundaries and dependencies

- Allowed dependencies: `foundation`; `ui-foundation` for shared visual infrastructure.
- Forbidden dependencies: `desktop-shell`, dialogue, memory, todo, servant-packs, and agent-integration implementations.
- Cross-module calls must use published contracts.

## Safety invariants

Persisted focus state must recover deterministically and reject invalid transitions. Focus must remain usable without Agent integration. Do not let role-pack data execute code.

## Minimum validation

Run affected focus unit tests for domain, application, persistence, and UI changes. Run integration tests for cross-module behavior and the release build for boundary changes.
