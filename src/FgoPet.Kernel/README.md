# Kernel

Pure .NET foundation for current role identity, user profile and character preferences, semantic companion presentation, conversation execution, context lifetime, and process shutdown. `AppRuntime` owns the active role; `CompanionPresentation` publishes neutral expression and text state. Context lifetime and shutdown coordination own their respective lifecycle boundaries.

ConversationOrchestrator owns turn admission, authoritative message persistence, capability coordination and completion. ConversationModelTurn owns one model call, stream aggregation, bounded attempts, tool downgrade and connection-change fencing. PromptComposer, input token metering, model capacity resolution, original-message recall and transactional summary planning compile here against published interfaces. Original messages remain authoritative; summaries and token anchors are rebuildable. Token anchors are process-local and invalidate on route, input, output reservation or request metadata changes.

Concrete HTTP request policy, SQLite repositories, settings UI and dialogue presentation remain in the Dialogue implementation. Installed package binding belongs to Content. Kernel preserves the existing public namespaces and constructor signatures; it has no WPF, concrete provider, SQLite or feature implementation dependency.

Portrait rendering is a provider capability: Kernel exposes `IPortraitBackend`/`IPortraitController` contracts. The Kernel references Extensibility, which publishes Platform contracts, and standard logging abstractions.

Validation: run the relevant Kernel, Dialogue, Windows, and EndToEnd test projects under `tests/`, plus the architecture gate for dependency changes. These checks do not replace real-provider or device acceptance.
