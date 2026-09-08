# memory

## Responsibilities

Own memory candidates, review, confirmed memories, enable/disable state, and conversation summaries.

## Non-responsibilities

Do not own dialogue orchestration, prompt composition, desktop shell, credentials, or any dialogue implementation.

## Current source and tests

Current source is in `src/FgoPet.Core/Memory`, `src/FgoPet.App/Memory`, `src/FgoPet.Infrastructure/Memory`, `ConversationSummaryService`, and the memory-management pages and view models. Corresponding tests remain in legacy projects.

## Target layout

Move memory domain, application, storage, management UI, summary service, and tests into this module; retain separate dialogue and memory boundaries.

## Public interfaces

Read-only memory query, candidate submission, review, confirmation, enable/disable, and summary contracts.

## Dependencies

May depend on `foundation` and `ui-foundation`. It must not depend on dialogue or `desktop-shell`; dialogue consumes its published contracts.

## Data and security boundaries

Memory is user data and may contain private conversation-derived information. Enforce review and deletion authorization, protect persistence, and avoid sensitive content in logs.

## Development and validation

Run affected Core, App, Infrastructure, and Windows memory tests. Run integration tests for dialogue-memory and persistence behavior, plus the release build when boundaries change.

## Migration status

Documentation skeleton only. Source and tests remain in legacy projects and paths.

## Migration debt

`ConversationSummaryService` and the combined conversation-memory settings UI need ownership separation without merging the domains.
