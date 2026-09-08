# agent-integration

## Responsibilities

Own Agent protocol, Relay, Runtime, Codex Adapter, source authorization, dispatch, reconciliation, archival, and replay protection.

## Non-responsibilities

Do not own general dialogue, Todo state, desktop-shell composition, or arbitrary execution beyond explicitly authorized project targets and sources.

## Current source and tests

Owned production projects, by exact project name:

- `FgoPet.AgentProtocol`
- `FgoPet.AgentRelay`
- `FgoPet.AgentRuntime`
- `FgoPet.CodexAdapter`

Owned unit-test projects, by exact project name:

- `FgoPet.AgentProtocol.Tests`
- `FgoPet.AgentRelay.Tests`
- `FgoPet.AgentRuntime.Tests`
- `FgoPet.CodexAdapter.Tests`

The migrated production projects are:

- `modules/agent-integration/src/FgoPet.AgentProtocol/FgoPet.AgentProtocol.csproj`
- `modules/agent-integration/src/FgoPet.AgentRelay/FgoPet.AgentRelay.csproj`
- `modules/agent-integration/src/FgoPet.AgentRuntime/FgoPet.AgentRuntime.csproj`
- `modules/agent-integration/src/FgoPet.CodexAdapter/FgoPet.CodexAdapter.csproj`

The migrated unit-test projects are:

- `modules/agent-integration/tests/FgoPet.AgentProtocol.Tests/FgoPet.AgentProtocol.Tests.csproj`
- `modules/agent-integration/tests/FgoPet.AgentRelay.Tests/FgoPet.AgentRelay.Tests.csproj`
- `modules/agent-integration/tests/FgoPet.AgentRuntime.Tests/FgoPet.AgentRuntime.Tests.csproj`
- `modules/agent-integration/tests/FgoPet.CodexAdapter.Tests/FgoPet.CodexAdapter.Tests.csproj`

Current additional source is in `src/FgoPet.Core/{Agents,Archives}`, `src/FgoPet.Infrastructure/Agents`, `src/FgoPet.Infrastructure/Persistence/{SqliteAgentRepository,SqliteWorkArchiveRepository}.cs`, and Agent/archive services, view models, views, and Settings pages in `FgoPet.App`. External consumers are:

- `src/FgoPet.App/FgoPet.App.csproj`
- `src/FgoPet.Infrastructure/FgoPet.Infrastructure.csproj`
- `tests/FgoPet.EndToEnd.Tests/FgoPet.EndToEnd.Tests.csproj`
- `scripts/install-codex-adapter.ps1`
- `scripts/test-phase4.ps1`

## Target layout

Keep the four existing executable or protocol assemblies as separate projects. Move supporting Agent/archive code behind this module's contracts, update project and script paths in the migration slice, and preserve assembly names and namespaces.

## Public interfaces

Protocol envelopes and messages, gateway and target catalog contracts, pairing and authorization contracts, dispatch, relay administration, reconciliation, archive, and protected-state contracts.

## Dependencies

May depend on `foundation` and `ui-foundation` for shared infrastructure. `desktop-shell` may consume this module; `todo` may use only its narrow dispatch contract. It must not depend on feature-module implementations.

## Data and security boundaries

Sources and project targets are default-deny and require explicit authorization. Credentials belong in protected stores. Payloads, logs, and archives must exclude prompts, reasoning, tool arguments, terminal output, credentials, and unnecessary local paths. Preserve replay, acknowledgement, reconciliation, and archive protections.

## Development and validation

Run the four owned unit-test projects. Protocol, pairing, protected state, dispatch, process launch, archive, or reconciliation changes also require relevant end-to-end tests and `pwsh -File scripts/test-phase4.ps1`, plus the release build when the solution changes.

## Migration status

This migration is complete for the eight dedicated Agent production and unit-test projects. Supporting code remains in the legacy Core, App, and Infrastructure projects until moved.

## Migration debt

App archive/Agent services and Core/Infrastructure Agent/archive code remain physically outside this module and are consumed by the listed external projects and scripts.
