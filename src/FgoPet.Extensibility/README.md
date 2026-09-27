# Extensibility

Pure .NET contracts and an immutable startup catalog for trusted, statically registered first-party capabilities. Each plugin contributes typed tools, workspace descriptors and settings descriptors, and owns its start/stop/dispose implementation.

The catalog validates IDs, API/version compatibility, dependency order and duplicate contributions. Feature state and user data belong to the plugin. Tool effects distinguish read-only, proposal and command behavior; declaring a command never grants execution authorization.

There is no WPF, database, service locator, external plugin loader, hot reload or security sandbox in this assembly. Stateful provider instances remain owned by their plugin; catalog metadata is captured once.

Validation: `dotnet test tests/FgoPet.Foundation.Tests/FgoPet.Foundation.Tests.csproj -c Release`.
