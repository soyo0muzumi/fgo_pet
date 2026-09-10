using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Shapes;
using System.Windows.Input;
using System.Windows.Threading;
using FgoPet.App.Dialogue;
using FgoPet.App.Panels;
using FgoPet.App.Settings;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Packs;
using FgoPet.Core.Settings;
using FgoPet.Infrastructure.Dialogue;
using FgoPet.Infrastructure.Memory;
using FgoPet.Infrastructure.Packs;
using FgoPet.Infrastructure.Persistence;
using Xunit;

namespace FgoPet.Windows.Tests.Panels;

[Trait("Category", "WindowsIntegration")]
public sealed class DialoguePanelIntegrationTests
{
    [Fact]
    public void Attached_panel_keeps_dialogue_body_without_embedded_composer()
    {
        StaRun(() =>
        {
            var view = new AttachedPanelView
            {
                DataContext = new AttachedPanelViewModel(TimeProvider.System),
            };

            Assert.IsAssignableFrom<FrameworkElement>(view.FindName("DialogueContent"));
            Assert.NotNull(view.FindName("FocusButton"));
            Assert.NotNull(view.FindName("TodayButton"));
            Assert.NotNull(view.FindName("TodoButton"));
            Assert.NotNull(view.FindName("DialogueButton"));
            Assert.Null(view.FindName("DialogueInputBox"));
            Assert.Null(view.FindName("SendDialogueButton"));
            Assert.Null(view.FindName("DialogueComposer"));
            Assert.Null(view.FindName("NewConversationButton"));
        });
    }

    [Fact]
    public void Attached_panel_exposes_companion_control_island()
    {
        StaRun(() =>
        {
            var view = new AttachedPanelView
            {
                DataContext = new AttachedPanelViewModel(TimeProvider.System),
            };
            var island = Assert.IsType<StackPanel>(view.FindName("CompanionControlIsland"));

            Assert.Equal(230, island.MinWidth);
            Assert.Equal(280, island.MaxWidth);
            Assert.NotNull(view.FindName("ChatEntryButton"));
            Assert.NotNull(view.FindName("ToolsEntryButton"));
            Assert.NotNull(view.FindName("AttentionEntryButton"));
        });
    }
    [Fact]
    public void Dialogue_presentation_state_switches_empty_configured_and_configuration_views()
    {
        StaRun(() =>
        {
            var viewModel = new AttachedPanelViewModel(TimeProvider.System);
            var view = new AttachedPanelView { DataContext = viewModel };
            var emptyState = (FrameworkElement)view.FindName("DialogueEmptyState")!;
            var messageList = (FrameworkElement)view.FindName("DialogueMessageList")!;
            var settingsButton = (FrameworkElement)view.FindName("DialogueSettingsButton")!;

            Assert.Equal(Visibility.Visible, emptyState.Visibility);
            Assert.Equal(Visibility.Collapsed, settingsButton.Visibility);

            viewModel.Conversation = CreateConversationViewModel();
            viewModel.Conversation.SetActiveServant("mash_kyrielight");

            // A configured but still-empty conversation keeps showing the empty state.
            Assert.Equal(Visibility.Visible, emptyState.Visibility);
            Assert.Equal(Visibility.Collapsed, messageList.Visibility);
            Assert.Equal(Visibility.Collapsed, settingsButton.Visibility);

            viewModel.Conversation.Turns.Add(new ConversationTurnViewModel(
                "message-1", ChatMessageRole.User, "你好"));

            Assert.Equal(Visibility.Visible, messageList.Visibility);
            Assert.Equal(Visibility.Collapsed, emptyState.Visibility);
            Assert.Equal(Visibility.Collapsed, settingsButton.Visibility);
        });
    }

    [Fact]
    public void Configuration_required_state_shows_the_settings_action()
    {
        StaRun(() =>
        {
            var viewModel = new AttachedPanelViewModel(TimeProvider.System);
            SettingsSection? requested = null;
            var conversation = CreateConversationViewModel();
            conversation.SettingsRequested += section => requested = section;
            viewModel.Conversation = conversation;
            var view = new AttachedPanelView { DataContext = viewModel };
            var settingsButton = (FrameworkElement)view.FindName("DialogueSettingsButton")!;

            conversation.NotifyConfigurationRequired();
            conversation.OpenSettingsCommand.Execute(null);

            Assert.Equal(Visibility.Visible, settingsButton.Visibility);
            Assert.Equal(SettingsSection.ModelConnection, requested);
        });
    }

    private static ConversationViewModel CreateConversationViewModel()
    {
        var settingsStore = new FakeSettingsStore(AppSettings.Defaults with
        {
            ModelConnection = new ModelConnectionSettings("test", "https://example.test/v1", "test-model"),
        });
        var orchestrator = new ConversationOrchestrator(
            new ThrowingProviderResolver(),
            new ThrowingContentResolver(),
            NoopDatabase.CreateConversationRepository(),
            NoopDatabase.CreateMemoryRepository(),
            new PromptComposer(),
            TimeProvider.System,
            settingsStore);
        return new ConversationViewModel(orchestrator, settingsStore);
    }

    private sealed class ThrowingProviderResolver : IChatProviderResolver
    {
        public IChatProvider Resolve() =>
            throw new FgoPet.Infrastructure.Providers.ProviderRequestException(
                FgoPet.Infrastructure.Providers.ProviderFailureCategory.Configuration, "未配置。");
    }

    private sealed class ThrowingContentResolver : IConversationContentResolver
    {
        public Task<ContentBinding> ResolveAsync(string servantId, CancellationToken cancellationToken) =>
            Task.FromResult(new ContentBinding(
                new ContentContextKey("stub", "stub.pack", "1.0.0", "default", "1", string.Empty),
                null,
                Array.Empty<KnowledgeEntry>(),
                Array.Empty<string>(),
                string.Empty,
                string.Empty));
    }

    private static class NoopDatabase
    {
        public static SqliteConversationRepository CreateConversationRepository() =>
            new(new RuntimeDatabase(":memory:"));

        public static SqliteMemoryRepository CreateMemoryRepository() =>
            new(new RuntimeDatabase(":memory:"));
    }

    private sealed class FakeSettingsStore(AppSettings initial) : IAppSettingsStore
    {
        public string Location => "memory";
        public AppSettings Load() => initial;
        public void Save(AppSettings settings) { }
    }

    private static void StaRun(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception error)
            {
                failure = error;
            }
            finally
            {
                var dispatcher = System.Windows.Threading.Dispatcher.FromThread(Thread.CurrentThread);
                if (dispatcher is not null && !dispatcher.HasShutdownStarted)
                {
                    dispatcher.InvokeShutdown();
                }
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
