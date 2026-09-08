# settings-privacy Agent Rules

## Scope

This file applies to this module directory tree and inherits the repository root `AGENTS.md`.

## Ownership

Own shared settings, profile, model connection configuration, credential storage adapters, export/deletion, private backup/restore, and privacy coordination.

## Boundaries and dependencies

- Allowed dependencies: published settings/data contracts from feature modules, `ui-foundation`, and `foundation`.
- Forbidden dependencies: feature implementations, `desktop-shell` internals, and direct access to feature-private tables.
- Cross-module calls must use published contracts and explicit privacy authorization.

## Safety invariants

Use Windows Credential Manager for API keys and protected local state for pairing credentials. Deletion and restore require explicit confirmation and safe validation. Never log secrets, raw private backups, or unnecessary local paths.

## Minimum validation

Run settings, privacy, credential, backup, restore, and Windows tests affected by the change. Security or cross-module changes require integration/end-to-end checks and the release build.
