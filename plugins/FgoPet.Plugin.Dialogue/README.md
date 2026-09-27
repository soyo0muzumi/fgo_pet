# dialogue

## Ownership

Dialogue owns HTTP provider adapters and wire policy, conversation SQLite repositories, model connection settings, and conversation presentation. The provider-neutral conversation engine, prompt assembly/budgets, recall and summary use cases compile in pure .NET Kernel. The existing `FgoPet.Dialogue` assembly still combines its concrete provider/storage implementation and WPF presentation.

Kernel publishes conversation/request/context contracts and typed provider failures, and coordinates turn admission, persistence and bounded model calls against those contracts. Dialogue preserves their established namespaces and uses Kernel context lifetime to reject stale requests and commits. Original messages remain authoritative; summaries and search projections are rebuildable.

## Capability boundary

Conversation contributions are captured through Extensibility and the Kernel capability router. Todo owns draft parsing, versions, explicit confirmation and persistence; Memory owns candidates, review and extraction work. Dialogue does not own their business state or concrete capability implementation.

Todo negotiation remains ordinary dialogue with explicit confirmation. The former TodoProposalCard and ArchiveDraftCard controls and their view models are retired. The host owns windows and presents the independent Todo workspace through its generic catalog.

## Presentation and settings

`ModelConnectionRegistration.AddDialogueRuntime` constructs the owned provider/storage/presentation services and wires the Kernel engine to their published ports. The application supplies cross-module content, capability, credentials, lifecycle and window composition. Presentation settings navigation consumes the UiSdk/Platform compatibility ports and no longer references HostContracts. `AddModelConnectionSettings` supplies a lazy settings view factory. ModelConnectionPage declares that it owns scrolling and detaches its saved-event subscription when disposed. The host owns routing, placement and page scroll offsets.

The shell owns `DialogueWindowViewModel` (window navigation, focus/read receipts and playback presentation) alongside the actual window; it reuses the same conversation model and retains its public constructor. It consumes the published Speech playback port. It marshals notifications to its owning dispatcher and checks request/message identity before applying late results. Hiding a window preserves its editing state; disposal detaches subscriptions and stops playback without disposing shared services.

## Persistence and security

Dialogue repositories own `conversations`, `chat_messages`, `conversation_summaries`, `conversation_contexts`, `chat_message_search` and their original-message projections. They preserve existing schemas and conversation/role/project scope checks. Model outputs remain untrusted: retain injection wrapping, typed tool/schema validation, and safe provider failure handling. Credentials remain in Windows Credential Manager; do not write keys or raw user data to diagnostics.

## Validation and remaining boundaries

Run relevant Core contract, App conversation/prompt, Infrastructure provider/SQLite and Windows dialogue/settings tests. Prompt/budget and context lifetime changes also require the affected EndToEnd checks and the evaluated architecture gate. These suites are regression checks, not real-provider or device acceptance.

The concrete provider/WPF conversation presentation mix remains to be narrowed. Dialogue has no Character, Speech or HostContracts project reference; window integration belongs to Shell. Legacy Core/Infrastructure aggregation is retired; that does not by itself complete generic hosting or long-file decomposition.
