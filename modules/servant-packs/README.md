# servant-packs

## Responsibilities

Own role-pack manifests, validation, installation, indexing, content binding, appearance content, current servant selection, and servant preferences.

## Non-responsibilities

Do not own pure window portrait layout, desktop-shell hosting, dialogue or Todo state, or arbitrary code execution from packages.

## Current source and tests

Current source is in `src/FgoPet.Core/{Packs,Portraits}`, `src/FgoPet.App/Servants`, role-pack and servant Settings pages, and `src/FgoPet.Infrastructure/Packs`, with corresponding tests.

## Target layout

Move package contracts, validation, installation, indexing, content binding, servant selection, preferences, and tests under this module. Keep pure window portrait layout in `desktop-shell`.

## Public interfaces

Pack manifests and compatibility contracts, installer/catalog, content binding, appearance resolution, servant selection, and preference contracts.

## Dependencies

May depend on `foundation` and `ui-foundation` for shared infrastructure. It must not depend on `desktop-shell` implementation or other business-module internals.

## Data and security boundaries

Treat `.fgopetpack` contents and names as untrusted pure data. Validate manifests, schemas, paths, hashes, compatibility, and allowed data types; never execute package content.

## Development and validation

Run affected Core, App, Infrastructure, and Windows pack tests, including invalid-package cases. Run integration checks for installation and binding changes, plus the release build.

## Migration status

Documentation skeleton only. Source and tests remain in legacy projects and paths.

## Migration debt

Portrait contracts are currently split between package content and window-hosting code and require careful ownership separation.
