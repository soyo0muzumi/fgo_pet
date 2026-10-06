# platform

Platform provides business-neutral contracts and shared persistence primitives.

## Responsibilities

- `FgoPet.Platform.Contracts` owns cross-cutting value types and interfaces, including runtime event, settings document, geometry, and window placement contracts.
- `FgoPet.Platform.Storage` owns SQLite runtime database access and migrations, atomic JSON storage, settings documents, and JSON window-placement persistence.
- `RuntimeDatabaseMigrator.MigrateModule` applies ordered module-owned scripts and version records in one transaction, independently of the platform schema version. Business schemas stay with their feature owners.
- `FgoPet.Platform.Windows` owns Windows Credential Manager, monitor-layout and protected-state implementations without WPF. The compatibility credential interfaces keep their namespaces in Platform.Contracts. `IProtectedStateProtector` is the business-neutral protected-state port.

## Boundaries

Platform contracts and storage stay free of feature rules and business-module dependencies. Feature repositories and business schemas remain with their owning modules. Do not move user-facing UI or business settings defaults into this layer.

## Security

Credential implementations must keep secrets protected by Windows credential storage/DPAPI. `WindowsStateProtector` uses DPAPI `CurrentUser`, purpose-separated entropy and bounded envelopes; it has no plaintext fallback. Database migrations must be safe to retry and leave the database consistent on failure. Diagnostics must not include user data.

## Validation

`IBoundedProcessRunner` is a neutral process-supervision port. Windows implements suspended process creation, explicit inherited standard streams, a Job with child-process, memory and lifetime limits, bounded capture and confirmed termination. Safe diagnostics are asynchronous and bounded; output and stdin remain private owner data. These limits do not reduce the host user's filesystem or network permissions and are not a sandbox.

Use `tests/FgoPet.Infrastructure.Tests` for database, SQLite, settings-document, and storage behavior. Use `tests/FgoPet.Windows.Tests` for Windows-specific implementations, including `WindowsStateProtectorTests`. Migration behavior must be covered for both empty and already-migrated databases.
