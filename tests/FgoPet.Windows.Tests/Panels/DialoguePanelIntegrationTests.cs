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
using FgoPet.Core.Speech;
using FgoPet.Dialogue.Settings;
using FgoPet.Infrastructure.Dialogue;
using FgoPet.Infrastructure.Memory;
using FgoPet.Infrastructure.Packs;
using FgoPet.Infrastructure.Persistence;
using FgoPet.Speech.Settings;
using Xunit;

namespace FgoPet.Windows.Tests.Panels;

[Trait("Category", "WindowsIntegration")]
public sealed class DialoguePanelIntegrationTests
{
    [Fact]
    public void Attached_panel_never_embeds_a_dialogue_body_or_composer()
    {
        StaRun(() =>
        {
            var view = new AttachedPanelView
            {
                DataContext = new AttachedPanelViewModel(TimeProvider.System),
            };

            // Dialogue is owned by the main window; the panel must not grow a second one.
            Assert.Null(view.FindName("DialogueContent"));
            Assert.Null(view.FindName("DialogueEmptyState"));
            Assert.Null(view.FindName("DialogueMessageList"));
            Assert.Null(view.FindName("DialogueSettingsButton"));
            Assert.Null(view.FindName("DialogueInputBox"));
            Assert.Null(view.FindName("SendDialogueButton"));
            Assert.Null(view.FindName("DialogueComposer"));
            Assert.Null(view.FindName("NewConversationButton"));
            Assert.NotNull(view.FindName("CompanionControlIsland"));
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

            // The island stacks the compact entries; the panel width comes from
            // AttachedPanelVisualMetrics.CalculateWidth, not from local Min/MaxWidth.
            Assert.Equal(Orientation.Vertical, island.Orientation);
            Assert.NotNull(view.FindName("ChatEntryButton"));
            Assert.NotNull(view.FindName("SpeechEntryButton"));
            Assert.NotNull(view.FindName("MoreEntryButton"));
        });
    }

    [Fact]
    public void Speech_entry_toggles_the_owned_speech_setting()
    {
        StaRun(() =>
        {
            var settings = new SpeechSettingsStore(SpeechSettings.Defaults with
            {
                Connection = SpeechConnectionSettings.Defaults with
                {
                    Provider = SpeechProviderKind.GptSoVits,
                    GptSoVitsReferenceAudioPath = "D:\\voices\\reference.wav",
                },
            });
            var viewModel = new AttachedPanelViewModel(TimeProvider.System, focus: null, settings: settings);
            var view = new AttachedPanelView { DataContext = viewModel };

            Assert.IsType<Button>(view.FindName("SpeechEntryButton"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            Assert.True(settings.Current.Connection.AutoReadEnabled);
            Assert.Equal(SpeechProviderKind.GptSoVits, settings.Current.Connection.Provider);
            Assert.Equal("D:\\voices\\reference.wav", settings.Current.Connection.GptSoVitsReferenceAudioPath);
        });
    }
    [Fact]
    public void Attached_panel_presents_no_dialogue_state()
    {
        StaRun(() =>
        {
            var viewModel = new AttachedPanelViewModel(TimeProvider.System);
            var view = new AttachedPanelView { DataContext = viewModel };
            viewModel.PortraitClick();

            // Empty/configured/configuration-required presentation lives in the main
            // window now; the panel only switches between the entry shell and focus.
            Assert.Null(view.FindName("DialogueEmptyState"));
            Assert.Null(view.FindName("DialogueMessageList"));
            Assert.Null(view.FindName("DialogueSettingsButton"));
            var shell = Assert.IsType<StackPanel>(view.FindName("CompanionControlIsland"));
            Assert.Equal(Visibility.Visible, shell.Visibility);
            Assert.Equal(Visibility.Collapsed, Assert.IsType<Grid>(view.FindName("FocusSetupCard")).Visibility);
            Assert.Equal(Visibility.Collapsed, Assert.IsType<Grid>(view.FindName("CompactTimer")).Visibility);
        });
    }

    [Fact]
    public void Focus_entry_reveals_the_setup_card_with_its_start_action()
    {
        StaRun(() =>
        {
            var viewModel = new AttachedPanelViewModel(TimeProvider.System);
            var view = new AttachedPanelView { DataContext = viewModel };
            viewModel.PortraitClick();
            var setup = Assert.IsType<Grid>(view.FindName("FocusSetupCard"));
            Assert.Equal(Visibility.Collapsed, setup.Visibility);

            Assert.IsType<Button>(view.FindName("FocusEntryButton"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            Assert.Equal(Visibility.Visible, setup.Visibility);
            Assert.NotNull(view.FindName("StartFocusButton"));
        });
    }

    private static ConversationViewModel CreateConversationViewModel()
    {
        var settingsStore = new FakeSettingsStore(DialogueSettings.Defaults with
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

    private sealed class FakeSettingsStore(DialogueSettings initial) : IDialogueSettingsStore
    {
        public DialogueSettings Load() => initial;
        public void Save(DialogueSettings settings) { }
    }

    private sealed class SpeechSettingsStore(SpeechSettings initial) : ISpeechSettingsStore
    {
        public SpeechSettings Current { get; private set; } = initial;
        public SpeechSettings Load() => Current;
        public void Save(SpeechSettings settings) => Current = settings;
    }

    private static void StaRun(Action action) => StaRunner.Run(action);
}
