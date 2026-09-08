# integration-tests

## Responsibilities

Own cross-module behavior, complete WPF flows, protocol round trips, backup/restore, application startup, dependency injection, and end-to-end smoke validation.

## Non-responsibilities

Do not duplicate single-module unit tests or use copyright resources, local machine data, or production secrets as fixtures.

## Current source and tests

Current sources are in `tests/FgoPet.EndToEnd.Tests` and cross-module portions of `tests/FgoPet.Windows.Tests`, including database migration, startup, and dependency-injection validation.

## Target layout

Move tests only when at least two modules participate or the scenario is a complete application, protocol, backup/restore, or release smoke flow. Shared fixtures follow the nearest-consumer rule.

## Public interfaces

Test fixtures, harnesses, and validation contracts for cross-module and end-to-end behavior.

## Dependencies

May reference the modules and test-support infrastructure under test. It is validation support outside `modules/`, not a production dependency.

## Data and security boundaries

Use minimal synthetic fixtures. Keep credentials, prompts, terminal output, personal data, copyright assets, and machine-specific state out of tests and logs.

## Development and validation

Run the narrowest affected test project first, then `dotnet test FgoPet.sln -c Release`; protocol and Agent-integration changes also require `pwsh -File scripts/test-phase4.ps1` when applicable.

## Migration status

Documentation skeleton only. Existing end-to-end and Windows tests remain in their legacy projects.

## Migration debt

Cross-module Windows tests are currently mixed with module-specific tests and require classification before relocation.
