using System.IO;
using System.Net.Http;
using FgoPet.App.Dialogue;
using FgoPet.App.Providers;
using FgoPet.App.Settings;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Packs;
using FgoPet.Core.Settings;
using FgoPet.Dialogue.Settings;
using FgoPet.Infrastructure.Dialogue;
using FgoPet.Infrastructure.Memory;
using FgoPet.Infrastructure.Packs;
using FgoPet.Infrastructure.Persistence;
using FgoPet.Infrastructure.Providers;
using FgoPet.Infrastructure.Secrets;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FgoPet.App.Tests.Dialogue;

public sealed class ConversationViewModelPresentationTests : IDisposable
{
    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"fgo-dialogue-presentation-{Guid.NewGuid():N}.db");

    [Fact]
    public void Empty_state_is_visible_until_the_first_user_message_is_added()
    {
        var viewModel = CreateViewModel();

        Assert.True(viewModel.IsConversationEmpty);
        Assert.True(viewModel.IsEmptyStateVisible);

        viewModel.Turns.Add(new ConversationTurnViewModel(
            "message-1", ChatMessageRole.User, "你好"));

        Assert.False(viewModel.IsConversationEmpty);
        Assert.False(viewModel.IsEmptyStateVisible);
    }

    [Fact]
    public void Reasoning_presentation_is_assistant_only_and_has_a_dynamic_summary()
    {
        var user = new ConversationTurnViewModel("user", ChatMessageRole.User, "你好");
        var assistant = new ConversationTurnViewModel("assistant", ChatMessageRole.Assistant, string.Empty, true);

        Assert.False(user.IsAssistant);
        Assert.True(assistant.IsAssistant);

        assistant.SetReasoningSummary("正在等待模型响应");
        Assert.Equal("正在等待模型响应", assistant.ReasoningSummary);
    }

    [Fact]
    public void Reasoning_well_is_hidden_until_assistant_reasoning_content_exists()
    {
        var user = new ConversationTurnViewModel("user", ChatMessageRole.User, "你好");
        var assistant = new ConversationTurnViewModel("assistant", ChatMessageRole.Assistant, string.Empty, true);

        Assert.False(user.HasVisibleReasoning);
        Assert.False(assistant.HasVisibleReasoning);

        assistant.AppendReasoning("先分析需求");

        Assert.True(assistant.HasVisibleReasoning);
    }

    [Fact]
    public void Todo_view_entry_is_available_only_when_a_confirmed_id_is_attached_to_the_turn()
    {
        var assistant = new ConversationTurnViewModel("assistant", ChatMessageRole.Assistant, "已创建待办");
        var user = new ConversationTurnViewModel("user", ChatMessageRole.User, "请安排");

        Assert.False(assistant.CanViewTodo);
        Assert.False(user.CanViewTodo);

        assistant.CreatedTodoId = "todo-1";

        Assert.True(assistant.CanViewTodo);
        Assert.Equal("todo-1", assistant.CreatedTodoId);
    }

    [Fact]
    public void Configuration_required_state_is_derived_from_missing_model_metadata()
    {
        var settings = new SequenceSettingsStore(DialogueSettings.Defaults with { ModelConnection = null });
        var viewModel = CreateViewModel(settings);

        Assert.True(viewModel.IsConfigurationRequired);
        Assert.True(viewModel.IsConfigurationStateVisible);

        viewModel.Turns.Add(new ConversationTurnViewModel("m", ChatMessageRole.User, "你好"));
        Assert.False(viewModel.IsConfigurationStateVisible);
    }

    [Fact]
    public async Task Failed_send_preserves_draft_and_session_intent()
    {
        var viewModel = CreateViewModel();
        viewModel.SetActiveServant("800100");
        viewModel.SessionContext.TrySetIntent("todo");
        viewModel.InputText = "请保留这段草稿";

        await viewModel.SendCommand.ExecuteAsync(null);

        Assert.Equal("请保留这段草稿", viewModel.InputText);
        Assert.Equal("todo", viewModel.SessionContext.IntentId);
        Assert.Equal("整理成待办", viewModel.SessionContext.IntentLabel);
    }

    [Fact]
    public void Model_connection_recovery_is_offered_for_model_and_network_errors_only()
    {
        var viewModel = CreateViewModel();

        viewModel.ErrorText = "无法连接模型服务。";
        Assert.True(viewModel.CanOpenModelSettings);

        viewModel.ErrorText = "本地对话存储暂时不可用，请重试。";
        Assert.False(viewModel.CanOpenModelSettings);
    }
    [Fact]
    public void Open_settings_command_requests_the_model_connection_route_without_owning_a_window()
    {
        var settings = new SequenceSettingsStore(DialogueSettings.Defaults with { ModelConnection = null });
        var viewModel = CreateViewModel(settings);
        SettingsSection? requested = null;
        viewModel.SettingsRequested += section => requested = section;

        viewModel.OpenSettingsCommand.Execute(null);

        Assert.Equal(SettingsSection.ModelConnection, requested);
    }

    [Fact]
    public void Provider_and_model_badges_refresh_from_settings_on_servant_activation()
    {
        var settings = new SequenceSettingsStore(DialogueSettings.Defaults with { ModelConnection = null });
        var viewModel = CreateViewModel(settings);
        Assert.Equal("未配置供应商", viewModel.ProviderStatusText);

        settings.Current = DialogueSettings.Defaults with
        {
            ModelConnection = new ModelConnectionSettings("deepseek", "https://api.deepseek.test/v1", "deepseek-chat"),
        };
        viewModel.SetActiveServant("800100");

        Assert.Equal("deepseek", viewModel.ProviderStatusText);
        Assert.Equal("deepseek-chat", viewModel.ModelStatusText);
    }

    [Fact]
    public async Task Provider_and_model_badges_refresh_when_connection_is_saved()
    {
        var settings = new SequenceSettingsStore(DialogueSettings.Defaults with { ModelConnection = null });
        var credentials = new TestCredentials();
        var catalog = new ProviderCatalog();
        var connection = new ModelConnectionViewModel(
            settings,
            credentials,
            catalog,
            new ChatProviderFactory(catalog, credentials, new HttpClient()));
        var viewModel = CreateViewModel(settings, connection);

        Assert.Equal("未配置供应商", viewModel.ProviderStatusText);
        connection.SelectedProviderId = "deepseek";
        connection.BaseUrl = "https://api.deepseek.com/v1";
        connection.ModelId = "deepseek-chat";
        connection.SetApiKey("secret-value");

        await connection.SaveCommand.ExecuteAsync(null);

        Assert.Equal("deepseek", viewModel.ProviderStatusText);
        Assert.Equal("deepseek-chat", viewModel.ModelStatusText);
        Assert.False(viewModel.IsConfigurationRequired);
    }

    private ConversationViewModel CreateViewModel(
        SequenceSettingsStore? settings = null,
        ModelConnectionViewModel? modelConnection = null)
    {
        settings ??= new SequenceSettingsStore(DialogueSettings.Defaults with
        {
            ModelConnection = new ModelConnectionSettings("test", "https://example.test/v1", "test-model"),
        });
        var database = new RuntimeDatabase(_databasePath);
        new RuntimeDatabaseMigrator(database).Migrate();
        var orchestrator = new ConversationOrchestrator(
            new DelegatingProviderResolver(),
            new DelegatingContentResolver(),
            new SqliteConversationRepository(database),
            new SqliteMemoryRepository(database),
            new PromptComposer(),
            TimeProvider.System,
            settings);
        return new ConversationViewModel(orchestrator, settings, modelConnection);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            var file = _databasePath + suffix;
            if (File.Exists(file)) File.Delete(file);
        }
    }

    private sealed class SequenceSettingsStore(DialogueSettings initial) : IDialogueSettingsStore
    {
        public DialogueSettings Current { get; set; } = initial;
        public DialogueSettings Load() => Current;
        public void Save(DialogueSettings settings) => Current = settings;
    }

    private sealed class DelegatingProviderResolver : IChatProviderResolver
    {
        public IChatProvider Resolve() =>
            throw new ProviderRequestException(ProviderFailureCategory.Configuration, "未配置。");
    }

    private sealed class TestCredentials : ICredentialStore, ICredentialReader
    {
        public Task SaveAsync(string target, string secret, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<bool> ExistsAsync(string target, CancellationToken cancellationToken) => Task.FromResult(false);

        public Task DeleteAsync(string target, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<string?> ReadAsync(string target, CancellationToken cancellationToken) => Task.FromResult<string?>("secret-value");
    }

    private sealed class DelegatingContentResolver : IConversationContentResolver
    {
        public Task<ContentBinding> ResolveAsync(string servantId, CancellationToken cancellationToken) =>
            Task.FromResult(new ContentBinding(
                new ContentContextKey("800100", "official.mash", "1.0.0", "casual", "1", string.Empty),
                null,
                Array.Empty<KnowledgeEntry>(),
                Array.Empty<string>(),
                string.Empty,
                string.Empty));
    }
}
