# foundation

## Responsibilities

Own only stable, genuinely shared infrastructure used by at least two feature modules, such as common file-system, JSON, diagnostics, geometry, event foundations, and database connection primitives.

## Non-responsibilities

Do not absorb business capability, feature state, temporary code, or code that is merely difficult to classify.

## Current source and tests

Candidate current source is in `src/FgoPet.Infrastructure/{FileSystem,Json,Diagnostics}`, shared Geometry and event basics, and database connection infrastructure, with corresponding tests in legacy projects.

## Target layout

Move only confirmed cross-module primitives here; preserve namespaces and assembly names until a later boundary stable point.

## Public interfaces

Stable, business-neutral primitives for file operations, JSON, diagnostics, geometry, events, and database connectivity.

## Dependencies

May depend on framework and third-party infrastructure only. It must not depend on `desktop-shell`, `ui-foundation`, or any business module.

## Data and security boundaries

Provide safe primitives without weakening callers' validation, authorization, privacy, or protected-storage requirements. Do not store feature data by implicit ownership.

## Development and validation

Run all affected consumer unit tests and the solution build; run integration tests when a primitive changes cross-module behavior.

## Migration status

Documentation skeleton only. Candidate source and tests remain in legacy projects and paths.

## Migration debt

The final contents depend on proving at least two stable feature consumers for each candidate primitive.
