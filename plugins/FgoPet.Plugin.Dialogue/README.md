# dialogue

## Ownership

Native conversation history can query existing terminal protected run checkpoints by exact role/project/conversation and user-message IDs. This read path reuses the existing schema, encryption, retention and quotas; no tool data is copied into semantic chat messages. Kernel projects those records into subsequent same-conversation requests and includes them in the existing summary flow. Missing/pruned checkpoints cannot be reconstructed; this is bounded execution retention, not a permanent external-content archive.

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

The loopback `buddy/deepseek-v4.1-flash` adapter maps provider tool indexes to response-local call indexes in first-seen order, preserving fragment grouping. Native identity, argument, completion and call-count validation remains authoritative. Other model routes keep their original indexes.

Run relevant Core contract, App conversation/prompt, Infrastructure provider/SQLite and Windows dialogue/settings tests. Prompt/budget and context lifetime changes also require the affected EndToEnd checks and the evaluated architecture gate. These suites and browser fixtures are regression checks, not product Chat/UI, real-provider or device acceptance.

The concrete provider/WPF conversation presentation mix remains to be narrowed. Dialogue has no Character, Speech or HostContracts project reference; window integration belongs to Shell. Legacy Core/Infrastructure aggregation is retired; that does not by itself complete generic hosting or long-file decomposition.

## Chat Web presentation

`AddChatWebPresentation` publishes `IChatWebSessionFactory` through `FgoPet.Dialogue.Contracts`. Each lightweight session projects the existing shared `ConversationViewModel`; disposing the projection never disposes or cancels that owner. `IChatWebHostActions` supplies bounded role/selector metadata and finite native presentation actions. Draft revisions and live session/role/conversation identities reject stale commands; model and history strings render as plain text in `Desktop/ui/chat`.

`ConversationViewModel` allocates a fresh request GUID before each send and accepts only matching `ConversationUpdate.RequestId` events. Kernel owns the request identity contract; queued portrait consumers must check it again when applying an update. A Chat Web command's transport request ID is separate from the model-send identity. Uncorrelated events are not accepted as current streaming feedback.

`IChatWorkspaceSelection` owns the in-memory local directory authorization selected through the host's native picker. Empty Chat binds its pending selection synchronously on first persisted user message, before native tool dispatch. Hiding or minimizing preserves the grant; new conversation, role/project changes, history navigation, explicit removal and disposal revoke it. Streaming forbids selection/removal. The Web bridge accepts no directory path; it publishes only a bounded directory label. Existing tool approval and file-access guards remain authoritative.

The adapter schedules generation without waiting for provider completion, so stop remains independently executable. The original provider, storage, history, capability and speech rules remain authoritative. App `ChatWebSessionTests` exercise these bridge boundaries; Shell Windows tests and actual-module browser fixtures exercise presentation separately.

Model connection settings use `ModelConnectionWebPage` over the existing connection view model. The old ModelConnectionPage XAML is removed; catalog registration retains metadata only.

The Margin settings detail uses English localization keys and the shared paper controls. Endpoint, token limits and reasoning visibility are disclosed under Advanced configuration; this is presentation state only. Save, connection testing, draft validation and immediate key clearing retain the existing owner commands. Known owner feedback is mapped to English keys in the page only; unknown service content and names remain literal text. Keys are write-only and omitted from navigation state. `MarginServiceSettingsWebTests` checks the production page with synthetic data, narrow-window scrolling and command dispatch; real provider and desktop acceptance remain separate.

The Chat composer uses an editor above a fixed send/stop toolbar. Its height is bounded by available conversation space (at most 55% before reserving room for notices, the toolbar and the reading area), rounded to complete text lines; longer drafts scroll internally. Input, authoritative draft replacement and layout changes share the same sizing path. The reading anchor survives composer/layout changes; latest-follow mode remains explicit. Jump to bottom occupies its own row below the message scroller. Empty context rows are hidden while available workspace actions and valid selections remain visible. Send and Enter share the same availability check; pending send/stop commands reject repeated activation while editing remains available. Copy shows a transient confirmation only after a successful host acknowledgement and preserves keyboard focus; errors stay in the conversation notice area. Hover, pressed, focus-visible and disabled states use existing Margin tokens; tooltips support pointer/focus activation and Escape dismissal. Native window geometry remains owned by Shell.

`node tests/chat_composer_probe.cjs <playwright-module> <ignored-output-dir> [no-preference|reduce]` exercises production Chat and shared UiSdk assets in installed Edge with a synthetic host. It covers bounded input, draft restore, send/stop/resend, reading anchors, streaming, narrow/sidebar layouts and content-sized user bubbles (CJK, multiline text, long URLs/identifiers and plain-text code fences); it does not establish physical IME or real-provider acceptance. `MarginWebIntegrationTests` also checks multiline drafts and reading position through the native WebView2 bridge.

Conversation sends allocate a RequestId before calling the orchestrator. UI updates must match that identity as well as the active role/conversation; session invalidation and completed send cleanup discard it. The native expression bridge preserves the public expression event and rejects queued expressions after a subsequent send starts.
