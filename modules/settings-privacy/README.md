# settings-privacy

## Responsibilities

Own application settings, user profile, model connection configuration, credential coordination, export, deletion, private backup, restore, settings shell, and privacy-operation orchestration.

## Non-responsibilities

Feature-specific settings pages remain with their feature modules. This module does not own dialogue, pack, Todo, focus, Agent, or desktop-shell implementation.

## Current source and tests

Current source is in `src/FgoPet.Core/{Settings,Backup}`, non-feature-specific code in `src/FgoPet.App/{Settings,Privacy}`, `src/FgoPet.Infrastructure/{Settings,Secrets,Backup}`, `src/FgoPet.Infrastructure/Persistence/RuntimeDatabase*`, and corresponding tests.

## Target layout

Move shared settings storage, privacy operations, backup/restore, credential adapters, settings shell, and tests under this module; feature-owned pages remain with their modules.

## Public interfaces

Settings, profile, model-connection, credential-store, export, deletion, backup, restore, and shared settings-storage contracts.

## Dependencies

May depend on each module's published settings/data contract, `ui-foundation`, and `foundation`. It must not depend on `desktop-shell` implementation or internal feature classes.

## Data and security boundaries

Keep API keys in Windows Credential Manager and pairing credentials in protected local state. Treat exports and backups as private, require explicit confirmation for deletion, and validate restore content.

## Development and validation

Run affected Core, App, Infrastructure, and Windows settings/privacy tests. Backup, deletion, credentials, or cross-module changes require relevant integration/end-to-end checks and the release build.

## Migration status

Documentation skeleton only. Source and tests remain in legacy projects and paths.

## Migration debt

The combined Settings UI includes feature pages that must be reassigned without weakening shared privacy coordination.
