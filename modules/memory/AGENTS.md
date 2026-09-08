# memory Agent Rules

## Scope

This file applies to this module directory tree and inherits the repository root `AGENTS.md`.

## Ownership

Own memory lifecycle, review, confirmed records, enable/disable state, summaries, persistence, and memory-management UI.

## Boundaries and dependencies

- Allowed dependencies: `foundation` and `ui-foundation`.
- Forbidden dependencies: dialogue implementation, `desktop-shell`, and direct consumers' internal classes or tables.
- Cross-module access must use published memory contracts.

## Safety invariants

Review and deletion remain explicit user-authorized operations. Preserve privacy, protected storage, and safe handling of conversation-derived data. Do not put raw memory content in logs.

## Minimum validation

Run memory domain, application, persistence, and UI tests. Changes affecting dialogue integration, deletion, or storage require relevant integration/end-to-end tests and a release build.
