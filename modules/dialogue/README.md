# dialogue

## Responsibilities

Own conversation sessions, model calls, prompt composition, knowledge binding, structured output, conversation-tool parsing and orchestration, and dialogue presentation state. The shared DialogueWindow is the single chat/settings host; session chips and the tool drawer expose bounded context and intent without creating a second conversation shell.

## Non-responsibilities

Do not own memory review or deletion, Todo persistence, desktop-shell composition, credentials, or Agent Relay and Codex transport implementation.

## Current source and tests

Current source is in `src/FgoPet.Core/Dialogue`, `src/FgoPet.App/{Dialogue,Providers}`, dialogue-related code in `src/FgoPet.App/Conversation`, and `src/FgoPet.Infrastructure/{Dialogue,Providers}`. Corresponding App, Core, Infrastructure, and Windows tests remain in legacy projects.

## Target layout

Move dialogue contracts, orchestration, providers, presentation, persistence adapters, and tests under this module while keeping public namespaces stable during the first stable point.

## Public interfaces

Conversation, prompt, chat-provider, tool, knowledge-binding, structured-output, and read-only memory-query contracts.

## Dependencies

May depend on `memory` through read-only query and candidate-submission contracts, on `servant-packs` for content contracts, on `ui-foundation`, and on `foundation`. It must not depend on `desktop-shell`.

## Data and security boundaries

Prompts, conversations, model configuration, and tool results are sensitive. Apply sanitization and approval boundaries; session context is bounded and inserted as data, not instructions. Dialogue may submit memory candidates but cannot control memory review or deletion, Agent transport, target authorization, or stop semantics.

## Development and validation

Run affected Core, App, Infrastructure, and Windows dialogue tests. Run integration or end-to-end checks for model/provider and cross-module behavior, plus the release build.

## Migration status

Documentation skeleton only. Source and tests remain in legacy projects and paths.

## Migration debt

Conversation tool parsing and orchestration currently share the App project with Todo integration and must be split behind narrow contracts.
