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

The OpenAI-compatible provider preserves native assistant call groups and ordered tool observations using the shared request envelope. A streaming event can contain multiple calls; EOF without an explicit complete response is incomplete. Kernel's native model adapter maps request-local wire aliases, bounds complete responses and charges each provider attempt. Kernel also exposes policy, approval and question broker DTO mechanisms, but product Chat/UI presentation and reply routing remain pending; production backend orchestration uses the native coordinator.

Dialogue repositories own `conversations`, `chat_messages`, `conversation_summaries`, `conversation_contexts`, `chat_message_search` and their original-message projections. They preserve existing schemas and conversation/role/project scope checks. Model outputs remain untrusted: retain injection wrapping, typed tool/schema validation, and safe provider failure handling. Credentials remain in Windows Credential Manager; do not write keys or raw user data to diagnostics.

`SqliteAgentRunStore` owns `agent_runs` and `agent_run_events`. It protects execution checkpoints through the platform protected-state port, binds them to an existing completed user message and conversation scope, and commits revision/cursor/evidence with metadata events atomically. Conversation/message deletion cascades to these records. Storage is bounded to 256 retained runs, 64 MiB of protected checkpoint blobs, 4 MiB per plaintext checkpoint and 1024 metadata events per run; terminal rows expire after seven days during admission. One journal slot is reserved for closure. Startup explicitly closes unfinished runs without replay; unconfirmed command intent becomes `ExecutionUnknown`. This adapter requires startup closure before host admission.

`ModelConnectionRegistration` now registers the native backend for production conversation sends after existing local continuation handling. The frontend approval/question bridge remains deferred: production omits `user.ask` and denies Command tools.

`SqliteAgentFinalDeliveryStore` owns a separately protected acceptance ledger. It verifies completed checkpoints and current source/content scope, then publishes semantic clarification history and the validated final answer in one transaction with deterministic message IDs. Repeated publication returns the same answer; an atomic claim limits post-turn observer dispatch to once. The ledger admits at most 256 rows and 16 MiB of protected payload, rejecting overflow without evicting accepted finals. Conversation/root-message deletion cascades to both stores. Private execution transcripts never become conversation messages or memory evidence.

Both native stores delay module migrations until first use. `NativeAgentLifecyclePlugin` runs after global database initialization, closes unfinished runs without replay and publishes already accepted finals before enabling admission. Recovery never calls a model or tool. Storage failure keeps native admission closed while offline capabilities remain available.

Accepted finals retain their source Run against terminal retention. Stale pending scope/source records remain protected and are skipped during recovery; they do not prevent other valid finals from publishing. Corrupt protected records fail closed. Claimed observer dispatch is best effort and never replays after a crash; observer failure does not disable readiness or undo a published answer.

## Validation and remaining boundaries

Run relevant Core contract, App conversation/prompt, Infrastructure provider/SQLite and Windows dialogue/settings tests. Prompt/budget and context lifetime changes also require the affected EndToEnd checks and the evaluated architecture gate. These suites and browser fixtures are regression checks, not product Chat/UI, real-provider or device acceptance.

The concrete provider/WPF conversation presentation mix remains to be narrowed. Dialogue has no Character, Speech or HostContracts project reference; window integration belongs to Shell. Legacy Core/Infrastructure aggregation is retired; that does not by itself complete generic hosting or long-file decomposition.

## Chat Web presentation

`AddChatWebPresentation` publishes `IChatWebSessionFactory` through `FgoPet.Dialogue.Contracts`. Each lightweight session projects the existing shared `ConversationViewModel`; disposing the projection never disposes or cancels that owner. `IChatWebHostActions` supplies bounded role/selector metadata and finite native presentation actions. Draft revisions and live session/role/conversation identities reject stale commands; model and history strings render as plain text in `Desktop/ui/chat`.

The adapter schedules generation without waiting for provider completion, so stop remains independently executable. The original provider, storage, history, capability and speech rules remain authoritative. App `ChatWebSessionTests` exercise these bridge boundaries; Shell Windows tests and actual-module browser fixtures exercise presentation separately.

Model connection settings use `ModelConnectionWebPage` over the existing connection view model. The old ModelConnectionPage XAML is removed; catalog registration retains metadata only.
