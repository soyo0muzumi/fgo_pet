# Phase 3 对话、Persona 与记忆实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在不破坏 Phase 1/2 离线桌宠和运行时合同的前提下，实现 OpenAI-compatible 对话、版本化角色内容、按 servant_id 隔离的记忆和 approved-only 剧情知识。

**Architecture:** 采用 Core / Infrastructure / App 三层端口/适配器结构。Provider、Credential Manager、SQLite 和角色包读取位于 Infrastructure；对话编排、Prompt 组合、流式取消、输出校验和设置 ViewModel 位于 App；WPF 只绑定状态与命令。servant_id 是角色领域身份，Persona/Knowledge 使用按角色、外观和包版本解析的内容上下文。

**Tech Stack:** .NET 8、WPF、CommunityToolkit.Mvvm、Microsoft.Data.Sqlite 8.0.1、Windows Credential Manager P/Invoke、OpenAI-compatible HTTP/SSE、xUnit、现有 STA/Windows integration test harness。

**Spec:** docs/superpowers/specs/2026-08-29-phase3-dialogue-memory-design.md

## Global Constraints

- 没有模型连接时，Phase 1/2 的桌宠、番茄钟、今日时间线、羁绊和本地事件台词必须继续可用。
- servant_id 是稳定角色身份；appearance_id 只标识外观，不作为记忆归属。
- 角色包只能包含严格校验的 JSON/JSONL，不允许脚本、条件表达式、函数、工具调用或可执行模板。
- API Key 只写入 Windows Credential Manager；不进入 SQLite、JSON、日志、导出文件或角色包。
- 普通对话不加载剧情 Knowledge；明确剧情问题才使用 approved-only 检索。
- 对话只由用户主动发送消息触发模型请求；Phase 2 事件继续使用本地反馈。
- 称呼只提供 package_default 与 user_defined 两种模式，并按 servant_id 保存。
- 不修改画像锚点、窗口拖动、DPI 几何、面板状态机、番茄结算和羁绊归属。
- Phase 3 不实现云端账号、任务模型、Codex/Agent、应用感知、模型工具调用、语音、远程同步或完整备份恢复。

---

### Task 1: Define Core contracts and settings

**Files:**
- Create: src/FgoPet.Core/Dialogue/ConversationContracts.cs
- Create: src/FgoPet.Core/Dialogue/ChatProviderContracts.cs
- Create: src/FgoPet.Core/Dialogue/PromptContracts.cs
- Create: src/FgoPet.Core/Memory/MemoryContracts.cs
- Create: src/FgoPet.Core/Packs/PersonaContract.cs
- Create: src/FgoPet.Core/Packs/KnowledgeContract.cs
- Create: src/FgoPet.Core/Settings/ModelConnectionSettings.cs
- Create: src/FgoPet.Core/Settings/ServantPreference.cs
- Modify: src/FgoPet.Core/Settings/AppSettings.cs
- Create: tests/FgoPet.Core.Tests/Dialogue/ConversationContractTests.cs
- Create: tests/FgoPet.Core.Tests/Dialogue/PromptContractTests.cs
- Create: tests/FgoPet.Core.Tests/Memory/MemoryContractTests.cs
- Create: tests/FgoPet.Core.Tests/Packs/ContentContractTests.cs
- Create: tests/FgoPet.Core.Tests/Settings/ServantPreferenceTests.cs

**Interfaces:**
- Produce Conversation, ChatMessage, ContentContextKey, ChatRequest, ChatStreamChunk, ChatCompletion, PromptContext, MemoryCandidate, StoredMemory, ModelConnectionSettings, ServantPreference, PersonaBundle, and KnowledgeEntry.
- IChatProvider exposes ProviderId, ModelId, StreamAsync(ChatRequest, CancellationToken), and ListModelsAsync(CancellationToken).
- ServantPreference exposes AddressMode and AddressText; valid modes are PackageDefault and UserDefined.

- [ ] **Step 1: Write failing contract tests**

```csharp
[Fact]
public void Content_context_includes_servant_package_version_and_appearance()
{
    var key = new ContentContextKey("800100", "official.mash", "1.1.0", "casual", "persona-2", "knowledge-1");

    Assert.Equal("800100", key.ServantId);
    Assert.Equal("casual", key.AppearanceId);
}

[Fact]
public void Address_preference_has_only_two_modes()
{
    var preference = new ServantPreference(AddressMode.UserDefined, "御主");

    Assert.Equal(AddressMode.UserDefined, preference.AddressMode);
    Assert.Equal("御主", preference.AddressText);
}
```

- [ ] **Step 2: Run tests and verify failure**

Run: dotnet test tests/FgoPet.Core.Tests/FgoPet.Core.Tests.csproj -c Release --filter "FullyQualifiedName~ConversationContractTests|FullyQualifiedName~PromptContractTests|FullyQualifiedName~MemoryContractTests|FullyQualifiedName~ContentContractTests|FullyQualifiedName~ServantPreferenceTests"

Expected: FAIL because the Phase 3 contracts do not exist.

- [ ] **Step 3: Implement immutable records and bounded validation**

Define User/Assistant message roles, Completed/Cancelled/Failed message status, bounded response fields, memory candidate statuses, content context fields, Provider metadata without secrets, and settings fields for Provider metadata, memory flags, and a servant_id preference map. Do not add tool-call fields.

- [ ] **Step 4: Run tests and verify pass**

Run the command from Step 2. Expected: PASS, with the existing Core suite passing.

- [ ] **Step 5: Commit**

```powershell
git add src/FgoPet.Core/Dialogue src/FgoPet.Core/Memory src/FgoPet.Core/Packs/PersonaContract.cs src/FgoPet.Core/Packs/KnowledgeContract.cs src/FgoPet.Core/Settings tests/FgoPet.Core.Tests/Dialogue tests/FgoPet.Core.Tests/Memory tests/FgoPet.Core.Tests/Packs/ContentContractTests.cs tests/FgoPet.Core.Tests/Settings/ServantPreferenceTests.cs
git commit -m "feat: define phase 3 dialogue and content contracts"
```

### Task 2: Add SQLite persistence, settings migration, and credential isolation

**Files:**
- Modify: src/FgoPet.Infrastructure/Persistence/RuntimeDatabaseMigrator.cs
- Create: src/FgoPet.Infrastructure/Dialogue/SqliteConversationRepository.cs
- Create: src/FgoPet.Infrastructure/Memory/SqliteMemoryRepository.cs
- Create: src/FgoPet.Infrastructure/Dialogue/SqliteContentBindingRepository.cs
- Create: src/FgoPet.Infrastructure/Secrets/ICredentialStore.cs
- Create: src/FgoPet.Infrastructure/Secrets/WindowsCredentialStore.cs
- Modify: src/FgoPet.Infrastructure/Settings/JsonAppSettingsStore.cs
- Create: tests/FgoPet.Infrastructure.Tests/Dialogue/SqliteConversationRepositoryTests.cs
- Create: tests/FgoPet.Infrastructure.Tests/Memory/SqliteMemoryRepositoryTests.cs
- Create: tests/FgoPet.Infrastructure.Tests/Secrets/CredentialStoreContractTests.cs
- Modify: tests/FgoPet.Infrastructure.Tests/Persistence/RuntimeDatabaseTests.cs
- Modify: tests/FgoPet.Infrastructure.Tests/Settings/JsonSettingsTests.cs

**Interfaces:**
- SqliteConversationRepository creates conversations, appends messages, loads messages by conversation and servant_id, and deletes conversations.
- SqliteMemoryRepository creates candidates, reviews candidates, lists enabled memories by servant_id, and deletes memories.
- ICredentialStore exposes SaveAsync(target, secret, cancellationToken), ExistsAsync(target, cancellationToken), and DeleteAsync(target, cancellationToken). UI never receives a read-secret method.

- [ ] **Step 1: Write failing migration and isolation tests**

```csharp
[Fact]
public void Phase3_migration_adds_chat_memory_and_binding_tables()
{
    var database = TestDatabase.Create();
    new RuntimeDatabaseMigrator(database).Migrate();

    Assert.Contains("chat_messages", TestDatabase.TableNames(database));
    Assert.Contains("memory_candidates", TestDatabase.TableNames(database));
    Assert.Contains("content_bindings", TestDatabase.TableNames(database));
}

[Fact]
public void Conversation_reads_are_isolated_by_servant_id()
{
    var repository = CreateRepository();
    repository.Append(UserMessage("c1", "800100", "你好"));
    repository.Append(UserMessage("c2", "100001", "你好"));

    Assert.Single(repository.LoadMessages("c1", "800100"));
    Assert.Empty(repository.LoadMessages("c1", "100001"));
}
```

- [ ] **Step 2: Run tests and verify failure**

Run: dotnet test tests/FgoPet.Infrastructure.Tests/FgoPet.Infrastructure.Tests.csproj -c Release --filter "FullyQualifiedName~Phase3|FullyQualifiedName~Conversation|FullyQualifiedName~Memory|FullyQualifiedName~RuntimeDatabase"

Expected: FAIL because the migration and repositories do not exist.

- [ ] **Step 3: Add one ordered schema migration**

Create conversations, chat_messages, conversation_summaries, memory_candidates, memories, and content_bindings with foreign keys, status constraints, unique message ordering, and servant_id lookup indexes. Store content versions and hashes, not full Prompts or Provider payloads. A newer schema still raises RuntimeDatabaseVersionException.

- [ ] **Step 4: Implement repositories and deletion semantics**

Use parameterized SQL and explicit UTC strings. Cancelled or failed assistant rows contain no partial response body. Conversation deletion removes messages, summaries, and unapproved candidates; approved memories require explicit deletion.

- [ ] **Step 5: Extend JSON settings and implement Credential Manager**

Persist Provider ID, Base URL, Model ID, privacy flags, and a servant_id keyed address map. Reject and never re-emit any API Key property. Implement Windows Credential Manager behind ICredentialStore and test with an in-memory fake.

- [ ] **Step 6: Run tests and verify pass**

Run the command from Step 2. Expected: PASS, with all existing Infrastructure tests passing.

- [ ] **Step 7: Commit**

```powershell
git add src/FgoPet.Infrastructure/Persistence src/FgoPet.Infrastructure/Dialogue src/FgoPet.Infrastructure/Memory src/FgoPet.Infrastructure/Secrets src/FgoPet.Infrastructure/Settings tests/FgoPet.Infrastructure.Tests/Persistence tests/FgoPet.Infrastructure.Tests/Dialogue tests/FgoPet.Infrastructure.Tests/Memory tests/FgoPet.Infrastructure.Tests/Secrets tests/FgoPet.Infrastructure.Tests/Settings
git commit -m "feat: persist phase 3 conversations and isolated credentials"
```

### Task 3: Implement Provider catalog and separate model connection Login

**Files:**
- Create: src/FgoPet.Infrastructure/Providers/ProviderCatalog.cs
- Create: src/FgoPet.Infrastructure/Providers/OpenAiCompatibleChatProvider.cs
- Create: src/FgoPet.App/Providers/ChatProviderFactory.cs
- Create: src/FgoPet.App/Settings/ModelConnectionViewModel.cs
- Create: src/FgoPet.App/Settings/ModelConnectionWindow.xaml
- Create: src/FgoPet.App/Settings/ModelConnectionWindow.xaml.cs
- Modify: src/FgoPet.App/Tray/TrayService.cs
- Modify: src/FgoPet.App/Bootstrap/DesktopAppUi.cs
- Modify: src/FgoPet.App/Bootstrap/ServiceRegistration.cs
- Create: tests/FgoPet.Infrastructure.Tests/Providers/OpenAiCompatibleChatProviderTests.cs
- Create: tests/FgoPet.App.Tests/Settings/ModelConnectionViewModelTests.cs
- Create: tests/FgoPet.Windows.Tests/Settings/ModelConnectionWindowIntegrationTests.cs

**Interfaces:**
- ProviderCatalog supplies OpenAI, DeepSeek, and custom-openai-compatible presets.
- OpenAiCompatibleChatProvider implements IChatProvider, model discovery, and chat-completions SSE parsing through injected HttpClient and ICredentialStore.
- ModelConnectionViewModel exposes SelectedProviderId, BaseUrl, ModelId, AvailableModels, masked-key state, TestCommand, SaveCommand, and ClearKeyCommand.

- [ ] **Step 1: Write failing HTTP and ViewModel tests**

```csharp
[Fact]
public async Task Model_discovery_returns_ids_without_logging_the_key()
{
    var handler = FakeHttp.Responding("/models", "{\"data\":[{\"id\":\"deepseek-chat\"}]}");
    var provider = CreateProvider("deepseek", "secret-not-to-log", handler);

    var models = await provider.ListModelsAsync(CancellationToken.None);

    Assert.Equal(new[] { "deepseek-chat" }, models);
    Assert.DoesNotContain("secret-not-to-log", handler.RequestLog);
}
```

- [ ] **Step 2: Run tests and verify failure**

Run: dotnet test tests/FgoPet.Infrastructure.Tests/FgoPet.Infrastructure.Tests.csproj -c Release --filter "FullyQualifiedName~OpenAiCompatibleChatProviderTests"

Then run: dotnet test tests/FgoPet.App.Tests/FgoPet.App.Tests.csproj -c Release --filter "FullyQualifiedName~ModelConnectionViewModelTests"

Expected: FAIL because the Provider adapter and Login ViewModel do not exist.

- [ ] **Step 3: Implement Provider presets and HTTP/SSE behavior**

Use HTTPS for non-loopback endpoints and allow HTTP only for loopback. Send the key only in Authorization. Parse model IDs from /models; retain manual Model ID entry when discovery is unsupported. Parse data frames through [DONE] and expose only safe error categories.

- [ ] **Step 4: Implement the Login window**

The window contains Provider, API Key, Base URL, Model, refresh, connection test, save, clear, and offline skip. It contains no role package selector, appearance selector, nickname, or Persona control. Saving metadata uses IAppSettingsStore; saving the key uses ICredentialStore.

- [ ] **Step 5: Add separate entry points and current model status**

Add Model Connection to the tray and portrait context menu. Keep Servant Library and Settings dedicated to package and servant preferences. When dialogue has no connection, its inline action opens the Login window. Show Provider and Model in the dialogue status line without adding a fifth header column.

- [ ] **Step 6: Run tests and verify pass**

Run: dotnet test tests/FgoPet.App.Tests/FgoPet.App.Tests.csproj -c Release --filter "FullyQualifiedName~ModelConnection|FullyQualifiedName~Bootstrap"

Then run: dotnet test tests/FgoPet.Windows.Tests/FgoPet.Windows.Tests.csproj -c Release --filter "FullyQualifiedName~ModelConnection"

Expected: PASS; packless startup remains available without a model connection.

- [ ] **Step 7: Commit**

```powershell
git add src/FgoPet.Infrastructure/Providers src/FgoPet.App/Providers src/FgoPet.App/Settings src/FgoPet.App/Tray/TrayService.cs src/FgoPet.App/Bootstrap/DesktopAppUi.cs src/FgoPet.App/Bootstrap/ServiceRegistration.cs tests/FgoPet.Infrastructure.Tests/Providers tests/FgoPet.App.Tests/Settings tests/FgoPet.Windows.Tests/Settings
git commit -m "feat: add provider model connection flow"
```

### Task 4: Add strict Persona/Knowledge readers and Prompt composition

**Files:**
- Create: src/FgoPet.Infrastructure/Packs/PersonaManifestReader.cs
- Create: src/FgoPet.Infrastructure/Packs/KnowledgeManifestReader.cs
- Create: src/FgoPet.Infrastructure/Packs/ContentBindingResolver.cs
- Create: src/FgoPet.App/Dialogue/PromptComposer.cs
- Create: src/FgoPet.App/Dialogue/PromptBudget.cs
- Create: src/FgoPet.App/Dialogue/PromptInjectionGuard.cs
- Create: tests/FgoPet.Infrastructure.Tests/Packs/PersonaManifestReaderTests.cs
- Create: tests/FgoPet.Infrastructure.Tests/Packs/KnowledgeManifestReaderTests.cs
- Create: tests/FgoPet.App.Tests/Dialogue/PromptComposerTests.cs
- Create: tests/fixtures/packs/persona-appearance-valid/persona/manifest.json
- Create: tests/fixtures/packs/persona-appearance-valid/persona/core.json
- Create: tests/fixtures/packs/persona-appearance-valid/persona/appearances/casual.json
- Create: tests/fixtures/packs/knowledge-mixed-approval/knowledge/manifest.json
- Create: tests/fixtures/packs/knowledge-mixed-approval/knowledge/topics.jsonl

**Interfaces:**
- PersonaManifestReader.ReadOptional(packageRoot, servantId) returns PersonaBundle or null.
- KnowledgeManifestReader.ReadOptional(packageRoot, servantId, appearanceId) returns approved KnowledgeEntry values.
- ContentBindingResolver.Resolve(packageRoot, servantId, appearanceId) returns ContentContextKey plus versions and hashes.
- PromptComposer.Compose(PromptContext) returns bounded ComposedPrompt and PromptAssemblyStatus.

- [ ] **Step 1: Write failing overlay and approval tests**

```csharp
[Fact]
public void Resolver_applies_current_appearance_overlay()
{
    var binding = ContentBindingResolver.Resolve(Fixture("persona-appearance-valid"), "800100", "casual");

    Assert.Equal("800100", binding.Context.ServantId);
    Assert.Contains("casual", binding.AppliedLayers);
}

[Fact]
public void Reader_excludes_pending_and_rejected_knowledge()
{
    var entries = KnowledgeManifestReader.ReadOptional(Fixture("knowledge-mixed-approval"), "800100", "casual")!;

    Assert.All(entries, entry => Assert.Equal("approved", entry.Approval));
}
```

- [ ] **Step 2: Run tests and verify failure**

Run: dotnet test tests/FgoPet.Infrastructure.Tests/FgoPet.Infrastructure.Tests.csproj -c Release --filter "FullyQualifiedName~PersonaManifestReader|FullyQualifiedName~KnowledgeManifestReader"

Then run: dotnet test tests/FgoPet.App.Tests/FgoPet.App.Tests.csproj -c Release --filter "FullyQualifiedName~PromptComposer"

Expected: FAIL because the readers and Prompt composer do not exist.

- [ ] **Step 3: Implement strict optional readers**

Keep the Phase 2 pack manifest unchanged so older hosts can ignore new directories. Discover optional persona/manifest.json and knowledge/manifest.json. Require relative paths, exact schema versions, bounded text, safe IDs, hashes, and no unknown properties. Malformed optional content falls back safely without blocking art-pack loading.

- [ ] **Step 4: Implement precedence and budgets**

Compose safety rules, product boundaries, servant core, appearance overlay, approved Knowledge, runtime state, enabled servant memories, and user message in that order. Cap ordinary context at about 2,500 tokens, state at 250, short-term memory at 600, and story Knowledge at 900. Treat package content, Knowledge, memories, and user input as data, not instructions.

- [ ] **Step 5: Run tests and verify pass**

Run the commands from Step 2. Expected: PASS, including appearance changes that alter context but not servant_id memory ownership.

- [ ] **Step 6: Commit**

```powershell
git add src/FgoPet.Infrastructure/Packs/PersonaManifestReader.cs src/FgoPet.Infrastructure/Packs/KnowledgeManifestReader.cs src/FgoPet.Infrastructure/Packs/ContentBindingResolver.cs src/FgoPet.App/Dialogue/PromptComposer.cs src/FgoPet.App/Dialogue/PromptBudget.cs src/FgoPet.App/Dialogue/PromptInjectionGuard.cs tests/FgoPet.Infrastructure.Tests/Packs tests/FgoPet.App.Tests/Dialogue/PromptComposerTests.cs tests/fixtures/packs/persona-appearance-valid tests/fixtures/packs/knowledge-mixed-approval
git commit -m "feat: compose versioned servant persona and knowledge"
```

### Task 5: Implement conversation orchestration and connect the dialogue panel

**Files:**
- Create: src/FgoPet.App/Dialogue/ConversationOrchestrator.cs
- Create: src/FgoPet.App/Dialogue/StructuredOutputValidator.cs
- Create: src/FgoPet.App/Dialogue/ConversationTurnViewModel.cs
- Create: src/FgoPet.App/Dialogue/ConversationViewModel.cs
- Modify: src/FgoPet.App/Panels/AttachedPanelViewModel.cs
- Modify: src/FgoPet.App/Panels/AttachedPanelView.xaml
- Modify: src/FgoPet.App/Panels/AttachedPanelView.xaml.cs
- Modify: src/FgoPet.App/Bootstrap/ServiceRegistration.cs
- Create: tests/FgoPet.App.Tests/Dialogue/ConversationOrchestratorTests.cs
- Create: tests/FgoPet.App.Tests/Dialogue/StructuredOutputValidatorTests.cs
- Modify: tests/FgoPet.App.Tests/Panels/AttachedPanelViewModelTests.cs
- Modify: tests/FgoPet.Windows.Tests/Panels/AttachedPanelViewIntegrationTests.cs
- Modify: tests/FgoPet.Windows.Tests/Windowing/PortraitWindowIntegrationTests.cs

**Interfaces:**
- ConversationOrchestrator.SendAsync(servantId, userText, cancellationToken) returns ConversationSendResult and publishes ConversationUpdate values.
- ConversationOrchestrator.CancelCurrent() cancels only the active request.
- StructuredOutputValidator.Validate(responseText, supportedExpressions) returns ValidatedChatOutput.
- ConversationViewModel exposes Turns, InputText, IsStreaming, CanSend, CanStop, ProviderStatusText, ModelStatusText, SendCommand, StopCommand, and NewConversationCommand.

- [ ] **Step 1: Write failing send/cancel tests**

```csharp
[Fact]
public async Task Send_persists_user_and_final_assistant_messages_with_context()
{
    var result = await _orchestrator.SendAsync("800100", "请陪我工作", CancellationToken.None);

    Assert.Equal(ConversationSendStatus.Completed, result.Status);
    Assert.Equal(2, _conversationStore.LoadMessages(result.ConversationId, "800100").Count);
    Assert.Equal("casual", _conversationStore.LastContext!.AppearanceId);
}

[Fact]
public async Task Cancel_does_not_persist_partial_assistant_text()
{
    using var cancellation = new CancellationTokenSource();
    var task = _orchestrator.SendAsync("800100", "开始", cancellation.Token);
    cancellation.Cancel();

    var result = await task;

    Assert.Equal(ConversationSendStatus.Cancelled, result.Status);
    Assert.DoesNotContain(_conversationStore.LoadMessages(result.ConversationId, "800100"), message => message.Role == ChatMessageRole.Assistant && message.Status == ChatMessageStatus.Completed);
}
```

- [ ] **Step 2: Run tests and verify failure**

Run: dotnet test tests/FgoPet.App.Tests/FgoPet.App.Tests.csproj -c Release --filter "FullyQualifiedName~ConversationOrchestrator|FullyQualifiedName~StructuredOutputValidator"

Expected: FAIL because orchestration and output validation do not exist.

- [ ] **Step 3: Implement output validation and request lifecycle**

Accept text, emotion, feedback_type, and memory_candidate. Bound text, map unsupported emotions to Default, route candidates to pending storage, and extract safe plain text from malformed JSON when possible. Persist the user message before sending, stream deltas only to UI, and persist the final assistant message with the turn's content context.

- [ ] **Step 4: Implement cancellation and failure behavior**

Allow one request per conversation. Cancel stores a cancelled status without partial text. Provider errors store a failed status and show an inline neutral error. Missing configuration opens the Login action. App shutdown cancels the request without deleting the user message.

- [ ] **Step 5: Connect ExpandedDialogue**

Replace the Phase 2 static dialogue list with bounded turns, input, send/stop, new conversation, Model status, and errors. Preserve four headers, outer shell, geometry, portrait stability, scrolling and hit-test exclusions. Add the actual ExpandedDialogue plus FocusClick integration test without changing the panel state machine.

- [ ] **Step 6: Run App/Windows tests and verify pass**

Run: dotnet test tests/FgoPet.App.Tests/FgoPet.App.Tests.csproj -c Release --filter "FullyQualifiedName~Dialogue|FullyQualifiedName~Panel"

Then run: dotnet test tests/FgoPet.Windows.Tests/FgoPet.Windows.Tests.csproj -c Release --filter "FullyQualifiedName~Dialogue|FullyQualifiedName~Panel"

Expected: PASS, including existing Phase 2 drag and hit tests.

- [ ] **Step 7: Commit**

```powershell
git add src/FgoPet.App/Dialogue src/FgoPet.App/Panels src/FgoPet.App/Bootstrap/ServiceRegistration.cs tests/FgoPet.App.Tests/Dialogue tests/FgoPet.App.Tests/Panels tests/FgoPet.Windows.Tests/Panels tests/FgoPet.Windows.Tests/Windowing
git commit -m "feat: connect streamed dialogue to attached panel"
```

### Task 6: Add summaries, memory review, servant preferences, export, and delete

**Files:**
- Create: src/FgoPet.App/Memory/ConversationSummaryService.cs
- Create: src/FgoPet.App/Memory/MemoryCandidateService.cs
- Create: src/FgoPet.App/Memory/MemoryViewModel.cs
- Create: src/FgoPet.App/Memory/MemoryWindow.xaml
- Create: src/FgoPet.App/Memory/MemoryWindow.xaml.cs
- Modify: src/FgoPet.App/Servants/ServantLibraryWindow.xaml
- Modify: src/FgoPet.App/Servants/ServantLibraryViewModel.cs
- Create: src/FgoPet.App/Privacy/UserDataExportService.cs
- Create: src/FgoPet.App/Privacy/UserDataDeletionService.cs
- Create: tests/FgoPet.App.Tests/Memory/ConversationSummaryServiceTests.cs
- Create: tests/FgoPet.App.Tests/Memory/MemoryCandidateServiceTests.cs
- Create: tests/FgoPet.App.Tests/Privacy/UserDataControlTests.cs
- Modify: tests/FgoPet.App.Tests/Servants/ServantLibraryViewModelTests.cs
- Create: tests/FgoPet.Windows.Tests/Memory/MemoryWindowIntegrationTests.cs

**Interfaces:**
- ConversationSummaryService.MaybeSummarizeAsync(conversationId, servantId, cancellationToken) returns a bounded summary and does nothing when memory is disabled.
- MemoryCandidateService.ReviewAsync(candidateId, action, editedText) changes only candidate/approved rows for its servant_id.
- UserDataExportService.ExportAsync(destinationPath, cancellationToken) writes a versioned archive without secrets.
- UserDataDeletionService exposes explicit conversation, memory, and all-data deletion methods.

- [ ] **Step 1: Write failing memory and address tests**

```csharp
[Fact]
public async Task Approved_memory_is_scoped_to_servant_and_survives_appearance_change()
{
    await _memory.ReviewAsync("candidate-1", MemoryReviewAction.Approve, null);

    var memories = await _memory.ListEnabledAsync("800100", CancellationToken.None);

    Assert.Single(memories);
    Assert.Equal("800100", memories[0].ServantId);
}

[Fact]
public async Task Address_preference_is_saved_by_servant_id()
{
    await _preferences.SaveAsync("800100", new ServantPreference(AddressMode.UserDefined, "御主"));

    Assert.Equal("御主", (await _preferences.LoadAsync("800100")).AddressText);
}
```

- [ ] **Step 2: Run tests and verify failure**

Run: dotnet test tests/FgoPet.App.Tests/FgoPet.App.Tests.csproj -c Release --filter "FullyQualifiedName~Memory|FullyQualifiedName~Privacy|FullyQualifiedName~ServantLibraryViewModelTests"

Expected: FAIL because memory services and servant preference UI do not exist.

- [ ] **Step 3: Implement summary and candidate lifecycle**

Summarize only after a bounded threshold and never by background scanning. Store the last covered message ID and context version. Failed summarization keeps the recent message window. Candidate output remains Pending until user review; only approved enabled memories enter Prompt composition, capped at 600 tokens and filtered by servant_id.

- [ ] **Step 4: Add servant-specific address settings**

In the existing servant library, show exactly two options: package default and user-defined address. Store address mode and one text value under servant_id in versioned JSON. Package default resolution uses appearance default, then servant default, then neutral fallback. No address control appears in Login.

- [ ] **Step 5: Add memory window and data controls**

Add memory enable/disable, candidate review, memory viewing/editing/disabling/deletion, conversation deletion, versioned user export, and all-data deletion. Export chat, summaries, candidates, approved memories, and safe content metadata; exclude keys, full Prompt, Provider payloads, raw story, package assets, and absolute paths. Do not implement restore/import.

- [ ] **Step 6: Run tests and verify pass**

Run: dotnet test tests/FgoPet.App.Tests/FgoPet.App.Tests.csproj -c Release --filter "FullyQualifiedName~Memory|FullyQualifiedName~Privacy|FullyQualifiedName~Servant"

Then run: dotnet test tests/FgoPet.Windows.Tests/FgoPet.Windows.Tests.csproj -c Release --filter "FullyQualifiedName~Memory"

Expected: PASS, including proof that conversation deletion does not silently delete approved memory and all-data deletion removes both.

- [ ] **Step 7: Commit**

```powershell
git add src/FgoPet.App/Memory src/FgoPet.App/Privacy src/FgoPet.App/Servants/ServantLibraryWindow.xaml src/FgoPet.App/Servants/ServantLibraryViewModel.cs tests/FgoPet.App.Tests/Memory tests/FgoPet.App.Tests/Privacy tests/FgoPet.App.Tests/Servants tests/FgoPet.Windows.Tests/Memory
git commit -m "feat: add servant-scoped memory controls"
```

### Task 7: Integrate approved Knowledge retrieval

**Files:**
- Create: src/FgoPet.Core/Knowledge/KnowledgeQuery.cs
- Create: src/FgoPet.Infrastructure/Knowledge/PackagedKnowledgeIndex.cs
- Create: src/FgoPet.Infrastructure/Knowledge/KnowledgeQueryService.cs
- Modify: src/FgoPet.App/Dialogue/ConversationOrchestrator.cs
- Modify: src/FgoPet.App/Dialogue/PromptComposer.cs
- Create: tests/FgoPet.Infrastructure.Tests/Knowledge/KnowledgeQueryServiceTests.cs
- Create: tests/FgoPet.App.Tests/Dialogue/KnowledgeRoutingTests.cs
- Create: tests/fixtures/packs/knowledge-index-valid/knowledge/index.sqlite
- Create: tests/fixtures/packs/knowledge-index-valid/knowledge/topics.jsonl

**Interfaces:**
- KnowledgeQueryService.Query(KnowledgeQuery) returns KnowledgeResult with AnsweredFromProfile, AnsweredFromStory, or CoverageGap, bounded summaries, source locators, and estimated tokens.
- ConversationOrchestrator queries Knowledge only for explicit story/personality questions.

- [ ] **Step 1: Write failing routing tests**

```csharp
[Fact]
public async Task Greeting_does_not_query_story_knowledge()
{
    await _orchestrator.SendAsync("800100", "你好", CancellationToken.None);

    Assert.False(_knowledge.Queried);
}

[Fact]
public async Task Story_question_returns_approved_bounded_context()
{
    var result = await _knowledge.Query(new KnowledgeQuery("800100", "冬木发生了什么", "casual"));

    Assert.Equal(KnowledgeAnswerStatus.AnsweredFromStory, result.Status);
    Assert.InRange(result.EstimatedTokens, 1, 900);
    Assert.All(result.Entries, entry => Assert.Equal("approved", entry.Approval));
}
```

- [ ] **Step 2: Run tests and verify failure**

Run: dotnet test tests/FgoPet.Infrastructure.Tests/FgoPet.Infrastructure.Tests.csproj -c Release --filter "FullyQualifiedName~KnowledgeQueryService"

Then run: dotnet test tests/FgoPet.App.Tests/FgoPet.App.Tests.csproj -c Release --filter "FullyQualifiedName~KnowledgeRouting"

Expected: FAIL because Knowledge query and routing do not exist.

- [ ] **Step 3: Implement packaged index access and routing**

Read only approved summary/index artifacts or a read-only Knowledge SQLite index; never read raw story directories from the runtime app. Use parameterized FTS5 queries, servant/appearance filters, and bounded topic fallback. Profile questions use core Persona; story questions retrieve 1–3 summaries; no result returns CoverageGap.

- [ ] **Step 4: Wire Knowledge into Prompt context**

Add selected Knowledge and source locators to the current turn's content context only for story questions. Keep paths, full Prompt and index internals out of user-facing text.

- [ ] **Step 5: Run tests and verify pass**

Run the commands from Step 2. Expected: PASS, including ordinary chat without Knowledge and explicit story questions with approved-only retrieval.

- [ ] **Step 6: Commit**

```powershell
git add src/FgoPet.Core/Knowledge src/FgoPet.Infrastructure/Knowledge src/FgoPet.App/Dialogue tests/FgoPet.Infrastructure.Tests/Knowledge tests/FgoPet.App.Tests/Dialogue tests/fixtures/packs/knowledge-index-valid
git commit -m "feat: add approved-only knowledge retrieval"
```

### Task 8: Bootstrap degradation, full Release gate, and documentation

**Files:**
- Modify: src/FgoPet.App/Bootstrap/AppPaths.cs
- Modify: src/FgoPet.App/Bootstrap/ServiceRegistration.cs
- Modify: src/FgoPet.App/Bootstrap/DesktopAppShell.cs
- Modify: src/FgoPet.App/Bootstrap/DesktopAppUi.cs
- Create: tests/FgoPet.App.Tests/Bootstrap/Phase3StartupTests.cs
- Modify: tests/FgoPet.App.Tests/Bootstrap/PacklessStartupTests.cs
- Create: scripts/test-phase3.ps1
- Create: docs/testing/phase3-windows-matrix.md
- Modify: README.md

**Interfaces:**
- Startup leaves Phase 1/2 available when Phase 3 migration, Provider metadata, optional content, or credentials fail.
- test-phase3.ps1 runs Release build, Core/Infrastructure/App/Windows tests, migration fixtures, secret/path scans, and export/deletion checks.

- [ ] **Step 1: Write failing degradation tests**

```csharp
[Fact]
public async Task Invalid_phase3_configuration_does_not_block_portrait_startup()
{
    _settings.Save(_settings.Load() with
    {
        ModelConnection = new ModelConnectionSettings("deepseek", "not-a-valid-url", "deepseek-chat")
    });

    await _shell.StartAsync([], CancellationToken.None);

    Assert.True(_ui.PortraitShown);
    Assert.True(_phase3Availability.IsAvailable == false || _ui.DialogueShowsConfigurationState);
}
```

- [ ] **Step 2: Run tests and verify failure**

Run: dotnet test tests/FgoPet.App.Tests/FgoPet.App.Tests.csproj -c Release --filter FullyQualifiedName~Phase3Startup

Expected: FAIL because Phase 3 startup degradation is not composed.

- [ ] **Step 3: Register Phase 3 after Phase 2**

Register the same RuntimeDatabase, repositories, Provider factory, content resolver, orchestrator, memory services, and settings windows. Initialize after tray and Phase 2 migration. Log only exception type and safe category; mark Phase 3 unavailable while keeping portrait and library startup.

- [ ] **Step 4: Add Release gate and manual matrix**

Create test-phase3.ps1 without changing Phase 1 or Phase 2 gate behavior. Cover Provider setup, offline mode, Login separation, servant-scoped address settings, appearance Persona changes, streaming/cancel, panel drag/hit behavior, 150%/200%/mixed DPI, and the Phase 2 navigation regression.

- [ ] **Step 5: Update stable docs**

Document separate model connection, OpenAI/DeepSeek/custom presets, current Model display, per-servant address settings, offline behavior, memory review, approved-only Knowledge, export/delete, and unavailable external task integration.

- [ ] **Step 6: Run the full Release gate and security scan**

Run: pwsh -NoProfile -File scripts/test-phase3.ps1

Then run:

```powershell
git diff --check
rg -n -i "api[_ -]?key|password|secret|C:\\Users\\|D:\\fgo_unpack" src tests docs README.md
git status --short
```

Expected: all automated tests pass, build has 0 warnings/errors, no credential or user-specific runtime path is introduced, and only intended Phase 3 files are changed in addition to pre-existing worktree changes.

- [ ] **Step 7: Commit**

```powershell
git add src/FgoPet.App/Bootstrap scripts/test-phase3.ps1 docs/testing/phase3-windows-matrix.md README.md tests/FgoPet.App.Tests/Bootstrap
git commit -m "test: add phase 3 release and privacy gate"
```

## Implementation Handoff

Execute Tasks 1–8 in order. Each task must pass focused tests before the next begins, and each commit must contain only that task's files. Before UI or Provider work, re-read the approved spec and preserve Phase 2 attached-panel geometry and offline degradation. Before declaring Phase 3 complete, run the full Release gate and report automated results separately from the Windows manual matrix.
