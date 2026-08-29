using System.IO;
using FgoPet.App.Dialogue;
using FgoPet.App.Providers;
using FgoPet.App.Settings;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Packs;
using FgoPet.Core.Settings;
using FgoPet.Infrastructure.Dialogue;
using FgoPet.Infrastructure.Memory;
using FgoPet.Infrastructure.Packs;
using FgoPet.Infrastructure.Persistence;
using FgoPet.Infrastructure.Providers;
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
    public void Configuration_required_state_is_derived_from_missing_model_metadata()
    {
        var settings = new SequenceSettingsStore(AppSettings.Defaults with { ModelConnection = null });
        var viewModel = CreateViewModel(settings);

        Assert.True(viewModel.IsConfigurationRequired);
        Assert.True(viewModel.IsConfigurationStateVisible);

        viewModel.Turns.Add(new ConversationTurnViewModel("m", ChatMessageRole.User, "你好"));
        Assert.False(viewModel.IsConfigurationStateVisible);
    }

    [Fact]
    public void Open_settings_command_requests_the_model_connection_route_without_owning_a_window()
    {
        var settings = new SequenceSettingsStore(AppSettings.Defaults with { ModelConnection = null });
        var viewModel = CreateViewModel(settings);
        SettingsSection? requested = null;
        viewModel.SettingsRequested += section => requested = section;

        viewModel.OpenSettingsCommand.Execute(null);

        Assert.Equal(SettingsSection.ModelConnection, requested);
    }

    [Fact]
    public void Provider_and_model_badges_refresh_from_settings_on_servant_activation()
    {
        var settings = new SequenceSettingsStore(AppSettings.Defaults with { ModelConnection = null });
        var viewModel = CreateViewModel(settings);
        Assert.Equal("未配置供应商", viewModel.ProviderStatusText);

        settings.Current = AppSettings.Defaults with
        {
            ModelConnection = new ModelConnectionSettings("deepseek", "https://api.deepseek.test/v1", "deepseek-chat"),
        };
        viewModel.SetActiveServant("800100");

        Assert.Equal("deepseek", viewModel.ProviderStatusText);
        Assert.Equal("deepseek-chat", viewModel.ModelStatusText);
    }

    private ConversationViewModel CreateViewModel(SequenceSettingsStore? settings = null)
    {
        settings ??= new SequenceSettingsStore(AppSettings.Defaults with
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
        return new ConversationViewModel(orchestrator, settings);
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

    private sealed class SequenceSettingsStore(AppSettings initial) : IAppSettingsStore
    {
        public AppSettings Current { get; set; } = initial;
        public string Location => "memory";
        public AppSettings Load() => Current;
        public void Save(AppSettings settings) => Current = settings;
    }

    private sealed class DelegatingProviderResolver : IChatProviderResolver
    {
        public IChatProvider Resolve() =>
            throw new ProviderRequestException(ProviderFailureCategory.Configuration, "未配置。");
    }

    private sealed class DelegatingContentResolver : IConversationContentResolver
    {
        public Task<ContentBinding> ResolveAsync(string servantId, CancellationToken cancellationToken) =>
            Task.FromResult(new ContentBinding(
                new ContentContextKey("800100", "official.mash", "1.0.0", "casual", "1", null),
                null,
                Array.Empty<KnowledgeEntry>(),
                Array.Empty<string>(),
                string.Empty,
                string.Empty));
    }
}
