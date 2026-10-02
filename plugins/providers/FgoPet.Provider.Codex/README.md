# agent-integration

## Responsibilities

The Agent Backend owns agent authorization, pairing, protected local state, backend runtime, reconciliation, and archival services. Its projects divide those responsibilities as follows:

- `FgoPet.AgentBackend.Contracts` publishes backend and settings contracts.
- `FgoPet.AgentBackend.Windows` implements local storage, relay/runtime integration, and backend lifecycle.
- `FgoPet.AgentBackend.Desktop` provides connection and pairing settings UI and registration.
- `FgoPet.AgentProtocol`, `FgoPet.AgentRelay`, `FgoPet.AgentRuntime`, and `FgoPet.CodexAdapter` remain separate protocol/relay/runtime/adapter projects.

The desktop backend surface is for connection and pairing settings. There are no Agent task, dispatch, or archive frontend views in this module. Todo state remains owned by the Todo capability.

## Boundaries

Expose stable contracts to the host and keep backend protocol, persistence, and runtime behind Agent Backend projects. Do not take ownership of feature-module state or repositories.

## Security

Sources and project targets are deny-by-default and require explicit authorization. Pairing requires explicit approval; protected credentials stay in protected storage. Payloads, logs, and archives must exclude prompts, reasoning, tool arguments, terminal output, credentials, and unnecessary local paths. Preserve replay protection, acknowledgement, reconciliation, and safe archive handling.

## Validation

Run the four owned test projects:

- `tests/FgoPet.AgentProtocol.Tests/FgoPet.AgentProtocol.Tests.csproj`
- `tests/FgoPet.AgentRelay.Tests/FgoPet.AgentRelay.Tests.csproj`
- `tests/FgoPet.AgentRuntime.Tests/FgoPet.AgentRuntime.Tests.csproj`
- `tests/FgoPet.CodexAdapter.Tests/FgoPet.CodexAdapter.Tests.csproj`

Changes to protocol, pairing, protected state, dispatch, process launch, archival, or reconciliation also require relevant end-to-end coverage and `pwsh -File tools/scripts/test-phase4.ps1`.
