using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using FgoPet.App.Dialogue;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Packs;
using FgoPet.Infrastructure.Packs;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using System.Windows.Controls;
using System.Windows.Media;

namespace FgoPet.Windows.Tests.Dialogue;

[Trait("Category", "WindowsIntegration")]
public sealed class DialogueWindowIntegrationTests
{
    [Fact]
    public void Close_hides_instead_of_closing_so_the_window_reuses_the_session()
    {
        StaRun(() =>
        {
            var window = CreateWindow();
            try
            {
                window.Show();
                Assert.True(window.IsVisible);

                window.Close();

                Assert.False(window.IsVisible);

                window.Show();
                Assert.True(window.IsVisible);
            }
            finally
            {
                window.Dispatcher.InvokeShutdown();
            }
        });
    }

    [Fact]
    public void Open_request_shows_and_activates_the_window()
    {
        StaRun(() =>
        {
            var viewModel = CreateViewModel();
            var window = new DialogueWindow(viewModel);
            try
            {
                viewModel.NotifyWindowHidden();
                viewModel.RequestOpen();

                Assert.True(window.IsVisible);
                Assert.Equal(0, viewModel.UnreadCount);
            }
            finally
            {
                window.Dispatcher.InvokeShutdown();
            }
        });
    }

    [Fact]
    public void Unread_counts_while_hidden_and_clears_on_open()
    {
        StaRun(() =>
        {
            var viewModel = CreateViewModel();
            var window = new DialogueWindow(viewModel);
            try
            {
                viewModel.RequestOpen();
                window.Hide();

                viewModel.Conversation.Turns.Add(new ConversationTurnViewModel("m1", Core.Dialogue.ChatMessageRole.Assistant, "回复"));

                Assert.Equal(1, viewModel.UnreadCount);

                viewModel.RequestOpen();

                Assert.Equal(0, viewModel.UnreadCount);
            }
            finally
            {
                window.Dispatcher.InvokeShutdown();
            }
        });
    }

    [Fact]
    public void Turns_added_while_following_scroll_kept_the_bottom()
    {
        StaRun(() =>
        {
            var viewModel = CreateViewModel();
            var window = new DialogueWindow(viewModel);
            try
            {
                window.Show();
                for (var index = 1; index <= 12; index++)
                {
                    viewModel.Conversation.Turns.Add(new ConversationTurnViewModel(
                        $"m{index}", Core.Dialogue.ChatMessageRole.Assistant, $"消息 {index}"));
                }

                window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);

                var scroller = FindField(window, "MessageScroller") as System.Windows.Controls.ScrollViewer;
                Assert.NotNull(scroller);
                Assert.True(scroller!.ScrollableHeight <= 0.5 || scroller.VerticalOffset >= scroller.ScrollableHeight - 0.5);
            }
            finally
            {
                window.Dispatcher.InvokeShutdown();
            }
        });
    }

    [Fact]
    public void Assistant_bubble_is_left_aligned_and_user_bubble_is_right_aligned()
    {
        StaRun(() =>
        {
            var viewModel = CreateViewModel();
            var window = new DialogueWindow(viewModel);
            try
            {
                window.Show();
                viewModel.Conversation.Turns.Add(new ConversationTurnViewModel(
                    "assistant", ChatMessageRole.Assistant, "从者消息"));
                viewModel.Conversation.Turns.Add(new ConversationTurnViewModel(
                    "user", ChatMessageRole.User, "用户消息"));

                window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);

                var scroller = Assert.IsType<ScrollViewer>(FindField(window, "MessageScroller"));
                var assistantText = FindVisualChildren<TextBlock>(window).Single(text => text.Text == "从者消息");
                var userText = FindVisualChildren<TextBlock>(window).Single(text => text.Text == "用户消息");
                var assistantBubble = Assert.IsType<Border>(VisualTreeHelper.GetParent(assistantText));
                var userBubble = Assert.IsType<Border>(VisualTreeHelper.GetParent(userText));
                var scrollerOrigin = scroller.TransformToAncestor(window).Transform(new Point(0, 0));
                var assistantOrigin = assistantBubble.TransformToAncestor(window).Transform(new Point(0, 0));
                var userOrigin = userBubble.TransformToAncestor(window).Transform(new Point(0, 0));

                Assert.InRange(assistantOrigin.X - scrollerOrigin.X, 0, 48);
                Assert.InRange(
                    scrollerOrigin.X + scroller.ActualWidth - (userOrigin.X + userBubble.ActualWidth),
                    0,
                    48);
            }
            finally
            {
                window.Dispatcher.InvokeShutdown();
            }
        });
    }

    [Fact]
    public void Service_registration_uses_one_dialogue_window_instance()
    {
        StaRun(() =>
        {
            using var provider = FgoPet.App.Bootstrap.ServiceRegistration.AddFgoPet(
                new Microsoft.Extensions.DependencyInjection.ServiceCollection(), []).BuildServiceProvider();

            var first = provider.GetRequiredService<DialogueWindow>();
            var second = provider.GetRequiredService<DialogueWindow>();
            var firstViewModel = provider.GetRequiredService<DialogueWindowViewModel>();
            var secondViewModel = provider.GetRequiredService<DialogueWindowViewModel>();

            Assert.Same(first, second);
            Assert.Same(firstViewModel, secondViewModel);
            Assert.Same(firstViewModel.Conversation, secondViewModel.Conversation);
            first.Close();
        });
    }

    private static DialogueWindow CreateWindow() => new(CreateViewModel());

    private static DialogueWindowViewModel CreateViewModel()
    {
        var settingsStore = new FakeSettingsStore(
            FgoPet.Core.Settings.AppSettings.Defaults with
            {
                ModelConnection = new FgoPet.Core.Settings.ModelConnectionSettings(
                    "test", "https://example.test/v1", "test-model"),
            });
        var orchestrator = new ConversationOrchestrator(
            new ThrowingProviderResolver(),
            new ThrowingContentResolver(),
            new FgoPet.Infrastructure.Dialogue.SqliteConversationRepository(
                new FgoPet.Infrastructure.Persistence.RuntimeDatabase(":memory:")),
            new FgoPet.Infrastructure.Memory.SqliteMemoryRepository(
                new FgoPet.Infrastructure.Persistence.RuntimeDatabase(":memory:")),
            new PromptComposer(),
            TimeProvider.System,
            settingsStore);
        return new DialogueWindowViewModel(new ConversationViewModel(orchestrator, settingsStore));
    }

    private static DependencyObject? FindField(DialogueWindow window, string name) =>
        window.FindName(name) as DependencyObject;

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match)
        {
            yield return match;
        }

        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            foreach (var child in FindVisualChildren<T>(VisualTreeHelper.GetChild(root, index)))
            {
                yield return child;
            }
        }
    }

    private sealed class FakeSettingsStore(FgoPet.Core.Settings.AppSettings initial) : FgoPet.Core.Settings.IAppSettingsStore
    {
        public string Location => "memory";
        public FgoPet.Core.Settings.AppSettings Load() => initial;
        public void Save(FgoPet.Core.Settings.AppSettings settings) { }
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

    private static void StaRun(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception error) { failure = error; }
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
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
