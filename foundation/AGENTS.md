# foundation Agent Rules

## Scope

This file applies to this directory tree and inherits the repository root `AGENTS.md`.

## Ownership

Own only business-neutral primitives stably reused by at least two feature modules.

## Boundaries and dependencies

- Allowed dependencies: framework and infrastructure libraries with no feature semantics.
- Forbidden dependencies: every business module, `desktop-shell`, and `ui-foundation`.
- Cross-module consumers must use stable, documented primitives.

## Safety invariants

Do not use foundation as a temporary dumping ground. Preserve caller validation, authorization, privacy, protected storage, and deterministic behavior.

## Minimum validation

Run all affected consumer tests and the release solution build. A shared primitive change requires integration validation for every affected cross-module path.
