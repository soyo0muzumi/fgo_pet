# servant-packs Agent Rules

## Scope

This file applies to this module directory tree and inherits the repository root `AGENTS.md`.

## Ownership

Own pack data contracts, validation, installation, indexing, content binding, servant appearance content, selection, and preferences.

## Boundaries and dependencies

- Allowed dependencies: `foundation` and `ui-foundation`.
- Forbidden dependencies: `desktop-shell` internals and other feature-module implementations.
- Cross-module calls must use published data and selection contracts.

## Safety invariants

Packages are pure data. Reject traversal, executable payloads, unknown capabilities, invalid schemas, and incompatible versions. Never execute package content or trust package filenames.

## Minimum validation

Run pack contract, installer, validator, persistence, and affected UI tests. Package-format or cross-module changes require invalid-case integration tests and a release build.
