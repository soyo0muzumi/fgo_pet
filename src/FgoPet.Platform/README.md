# platform

Platform provides business-neutral contracts and shared persistence primitives.

## Responsibilities

- `FgoPet.Platform.Contracts` owns cross-cutting value types and interfaces, including runtime event, settings document, geometry, and window placement contracts.
- `FgoPet.Platform.Storage` owns SQLite runtime database access and migrations, atomic JSON storage, settings documents, and JSON window-placement persistence.
- `FgoPet.Platform.Windows` owns Windows Credential Manager and monitor-layout implementations without WPF. The compatibility credential interfaces keep their namespaces in Platform.Contracts.

## Boundaries

Platform contracts and storage stay free of feature rules and business-module dependencies. Feature repositories and business schemas remain with their owning modules. Do not move user-facing UI or business settings defaults into this layer.

## Security

Credential implementations must keep secrets protected by Windows credential storage/DPAPI. Database migrations must be safe to retry and leave the database consistent on failure. Diagnostics must not include user data.

## Validation

Use `tests/FgoPet.Infrastructure.Tests` for database, SQLite, settings-document, and storage behavior. Use `tests/FgoPet.Windows.Tests` when changing Windows-specific platform implementations. Migration behavior must be covered for both empty and already-migrated databases.
