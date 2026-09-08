# integration-tests Agent Rules

## Scope

This file applies to this directory tree and inherits the repository root `AGENTS.md`.

## Ownership

Own tests that exercise two or more modules, complete WPF behavior, application startup, dependency injection, protocol round trips, backup/restore, and release smoke flows.

## Boundaries and dependencies

- Allowed dependencies: public contracts of modules under test and minimal test infrastructure.
- Forbidden dependencies: production code paths that bypass public contracts, module-private internals, and production assets as fixtures.
- Cross-module assertions must test observable contracts and safety boundaries.

## Safety invariants

Fixtures are minimal and synthetic. Do not record secrets, private prompts, tool arguments, terminal output, personal data, or unnecessary local paths. Test logs belong only in ignored or explicitly approved evidence locations.

## Minimum validation

Run the affected integration test project and the release solution test command. Agent protocol or Phase 4 behavior additionally requires the relevant end-to-end tests and `pwsh -File scripts/test-phase4.ps1`.
