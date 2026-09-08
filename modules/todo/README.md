# todo

## Responsibilities

Own Todo entities, status, priority, repository contracts, list UI, proposal confirmation, task timeline display, and Todo conversation-tool bridging.

## Non-responsibilities

Do not own Relay, Codex Adapter, pipeline protocol, desktop-shell composition, or general Agent transport.

## Current source and tests

Current source is in `src/FgoPet.Core/Todo`, Todo services, view models and views in `src/FgoPet.App`, `src/FgoPet.Infrastructure/Persistence/SqliteTodoRepository.cs`, and corresponding tests.

## Target layout

Move Todo domain, application, persistence, UI, proposal flow, and tests under this module, replacing implementation coupling with a narrow Agent dispatch contract.

## Public interfaces

Todo entity, repository, proposal, status/priority, timeline, and narrow Agent dispatch-request contracts.

## Dependencies

May depend on `agent-integration` through a narrow dispatch interface, on `ui-foundation`, and on `foundation`. It must not depend on Relay, Codex Adapter, pipeline implementation, or `desktop-shell`.

## Data and security boundaries

Todo data and proposed dispatches are user-controlled. Require explicit confirmation before dispatch, validate identifiers and state transitions, and avoid sensitive task data in logs.

## Development and validation

Run affected Core, App, Infrastructure, and Windows Todo tests. Run agent-dispatch integration/end-to-end checks for bridging changes and the release build.

## Migration status

Documentation skeleton only. Source and tests remain in legacy projects and paths.

## Migration debt

Todo tools and proposal services currently share App code with dialogue and need a narrow integration contract.
