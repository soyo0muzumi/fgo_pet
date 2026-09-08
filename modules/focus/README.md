# focus

## Responsibilities

Own focus state machines, sessions, persistence and recovery, today's timeline, bond progression, and focus-related feedback.

## Non-responsibilities

Do not own desktop windows, generic shell lifecycle, dialogue orchestration, memory review, Todo entities, or role-pack content.

## Current source and tests

Current source is in `src/FgoPet.Core/{Focus,Bond,Timeline}`, `src/FgoPet.App/Focus`, and `src/FgoPet.Infrastructure/{Focus,Bond,Timeline}`, plus focus-specific event types and implementations. Corresponding Core, App, Infrastructure, and Windows tests remain in their legacy projects.

## Target layout

Move focus domain, application, persistence, and focused UI code into this module; keep only genuinely shared runtime events in `foundation` after consumer analysis.

## Public interfaces

Focus session, command, state, preset, timeline, bond progression, and persistence contracts required by the shell and other consumers.

## Dependencies

May depend on `foundation` and `ui-foundation` when UI is present. It must not depend on `desktop-shell` or other business-module implementations.

## Data and security boundaries

Own focus sessions, completion state, bond progression, and timeline records. Validate persisted state and treat imported or local data as untrusted; do not handle credentials or execute package content.

## Development and validation

Run the affected Core, App, Infrastructure, and Windows focus tests. For cross-module startup or timeline behavior, run the relevant integration tests and the release build.

## Migration status

Documentation skeleton only. Source and tests remain in legacy projects and paths.

## Migration debt

The final ownership of generic runtime events depends on how many modules consume them.
