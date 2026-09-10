using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using FgoPet.App.Focus;
using FgoPet.App.Panels;
using FgoPet.App.Settings;
using FgoPet.Core.Focus;
using FgoPet.Core.Panels;
using Xunit;

namespace FgoPet.Windows.Tests.Panels;

[Trait("Category", "WindowsIntegration")]
public sealed class AttachedPanelViewIntegrationTests
{
    [Fact]
    public void Compact_companion_surface_exposes_the_approved_entry_shell()
    {
        StaRun(() =>
        {
            var model = new AttachedPanelViewModel(TimeProvider.System);
            var view = new AttachedPanelView { DataContext = model };
            model.PortraitClick();

            Assert.NotNull(view.FindName("GreetingText"));
            var island = Assert.IsType<StackPanel>(view.FindName("CompanionControlIsland"));
            Assert.Equal(Visibility.Visible, island.Visibility);
            var primaryControls = new[] { "ChatEntryButton", "SpeechEntryButton", "MoreEntryButton" }
                .Select(name => Assert.IsType<Button>(view.FindName(name)))
                .ToArray();
            var secondaryControls = new[] { "SettingsEntryButton", "TasksEntryButton", "FocusEntryButton" }
                .Select(name => Assert.IsType<Button>(view.FindName(name)))
                .ToArray();

            Assert.All(primaryControls.Concat(secondaryControls), control =>
            {
                Assert.IsAssignableFrom<System.Windows.Media.Geometry>(control.Content);
                Assert.NotNull(control.ContentTemplate);
                Assert.False(string.IsNullOrWhiteSpace(control.ToolTip?.ToString()));
                Assert.False(string.IsNullOrWhiteSpace(System.Windows.Automation.AutomationProperties.GetName(control)));
                Assert.True(control.Height >= 34);
            });
            Assert.Null(view.FindName("MicrophoneEntryButton"));
            Assert.Null(view.FindName("AttentionEntryButton"));
            Assert.Null(view.FindName("DialogueContent"));
            Assert.Null(view.FindName("TodayContent"));
            Assert.Null(view.FindName("TodoContent"));
        });
    }

    [Fact]
    public void More_expands_secondary_entries_without_entering_a_business_state_and_escape_collapses_it_first()
    {
        StaRun(() =>
        {
            var model = new AttachedPanelViewModel(TimeProvider.System);
            var view = new AttachedPanelView { DataContext = model };
            model.PortraitClick();
            var actions = Assert.IsType<StackPanel>(view.FindName("CompactActions"));

            Assert.False(view.QuickActionsExpanded);
            Assert.Equal(Visibility.Collapsed, actions.Visibility);
            Assert.IsType<Button>(view.FindName("MoreEntryButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            Assert.True(view.QuickActionsExpanded);
            Assert.Equal(AttachedPanelState.Compact, model.State);
            Assert.Equal(Visibility.Visible, actions.Visibility);
            Assert.True(view.CollapseQuickActions());
            Assert.Equal(AttachedPanelState.Compact, model.State);
            Assert.Equal(Visibility.Collapsed, actions.Visibility);
            Assert.False(view.CollapseQuickActions());
        });
    }

    [Fact]
    public void Settings_entry_requests_the_independent_settings_window()
    {
        StaRun(() =>
        {
            var model = new AttachedPanelViewModel(TimeProvider.System);
            var view = new AttachedPanelView { DataContext = model };
            SettingsSection? requested = null;
            model.SettingsRequested += section => requested = section;
            model.PortraitClick();
            Assert.IsType<Button>(view.FindName("MoreEntryButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            Assert.IsType<Button>(view.FindName("SettingsEntryButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            Assert.Equal(SettingsSection.Personalization, requested);
        });
    }

    [Fact]
    public void Active_focus_replaces_the_greeting_with_a_compact_timer_card()
    {
        StaRun(() =>
        {
            var focus = new FakeFocusService();
            var model = new AttachedPanelViewModel(TimeProvider.System, focus);
            var view = new AttachedPanelView { DataContext = model };
            var greeting = Assert.IsType<Grid>(view.FindName("CompactMessage"));
            var timer = Assert.IsType<Grid>(view.FindName("CompactTimer"));
            model.PortraitClick();

            Assert.Equal(Visibility.Visible, greeting.Visibility);
            Assert.Equal(Visibility.Collapsed, timer.Visibility);

            focus.Start(FgoPet.App.Panels.FocusPresetCatalog.Short, "servant-mash");
            focus.RaiseChanged();

            Assert.Equal(Visibility.Collapsed, greeting.Visibility);
            Assert.Equal(Visibility.Visible, timer.Visibility);
            Assert.NotNull(view.FindName("TimerProgress"));
            Assert.IsType<Button>(view.FindName("PauseResumeButton"));
            Assert.IsType<Button>(view.FindName("StopTimerButton"));
        });
    }

    [Theory]
    [InlineData("ModernGray")]
    [InlineData("FgoLight")]
    public void Companion_card_has_an_opaque_readable_surface_in_both_themes(string theme)
    {
        StaRun(() =>
        {
            var model = new AttachedPanelViewModel(TimeProvider.System);
            var view = new AttachedPanelView { DataContext = model, Width = 240, Height = 110 };
            view.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"/FgoPet.App;component/Themes/{theme}.xaml", UriKind.Relative),
            });
            model.PortraitClick();
            view.ApplyPhase0Clip(240, 110, 12);
            view.Measure(new Size(240, 110));
            view.Arrange(new Rect(0, 0, 240, 110));
            view.UpdateLayout();

            var bitmap = new RenderTargetBitmap(480, 220, 192, 192, System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(view);
            var pixel = new byte[4];
            bitmap.CopyPixels(new Int32Rect(200, 48, 1, 1), pixel, 4, 0);
            Assert.Equal(255, pixel[3]);

            var captureDirectory = Environment.GetEnvironmentVariable("FGO_PET_UI_CAPTURE_DIR");
            if (!string.IsNullOrWhiteSpace(captureDirectory))
            {
                System.IO.Directory.CreateDirectory(captureDirectory);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = System.IO.File.Create(System.IO.Path.Combine(captureDirectory, $"pet-entry-{theme}.png"));
                encoder.Save(file);
            }
        });
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

    private sealed class FakeFocusService : IFocusSessionService
    {
        public FocusSession Current { get; private set; } = FocusSession.Idle;
        public event EventHandler? SnapshotChanged;
#pragma warning disable CS0067
        public event EventHandler? PersistenceFailed;
#pragma warning restore CS0067
        public void Start(FocusPreset preset, string servantId) =>
            Current = FocusSession.Start("fake-session", servantId, preset, DateTimeOffset.UtcNow);
        public void Pause() { }
        public void Resume() { }
        public void Stop() => Current = FocusSession.Idle;
        public void Tick() { }
        public void Restore() { }
        public void RaiseChanged() => SnapshotChanged?.Invoke(this, EventArgs.Empty);
    }
}
