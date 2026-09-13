using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using System.Windows.Shapes;
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
            // Bindings on the entry shell (the auto-read switch announces its state)
            // only settle once the shell has been through a layout pass.
            view.Measure(new Size(240, 240));
            view.Arrange(new Rect(0, 0, 240, 240));
            view.UpdateLayout();

            Assert.NotNull(view.FindName("PanelTitle"));
            var island = Assert.IsType<StackPanel>(view.FindName("CompanionControlIsland"));
            Assert.Equal(Visibility.Visible, island.Visibility);
            var primaryControls = new[] { "ChatEntryButton", "SpeechEntryButton", "MoreEntryButton" }
                .Select(name => Assert.IsType<Button>(view.FindName(name)))
                .ToArray();
            var secondaryControls = new[] { "SettingsEntryButton", "TasksEntryButton", "FocusEntryButton", "ExitEntryButton" }
                .Select(name => Assert.IsType<Button>(view.FindName(name)))
                .ToArray();

            Assert.All(primaryControls.Concat(secondaryControls), control =>
            {
                // Entries either draw an icon geometry through a template or use a Fluent glyph.
                if (control.Content is System.Windows.Media.Geometry)
                {
                    Assert.NotNull(control.ContentTemplate);
                }
                else
                {
                    Assert.False(string.IsNullOrWhiteSpace(control.Content?.ToString()), $"{control.Name} must expose an icon");
                }

                Assert.False(
                    string.IsNullOrWhiteSpace(control.ToolTip?.ToString()),
                    $"{control.Name} must expose a tooltip");
                Assert.False(
                    string.IsNullOrWhiteSpace(System.Windows.Automation.AutomationProperties.GetName(control)),
                    $"{control.Name} must expose an accessible name");
                Assert.True(control.Height >= 34, $"{control.Name} must be at least 34 DIP tall");
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
    public void Active_focus_replaces_the_setup_card_with_a_compact_timer_card()
    {
        StaRun(() =>
        {
            var focus = new FakeFocusService();
            var model = new AttachedPanelViewModel(TimeProvider.System, focus);
            var view = new AttachedPanelView { DataContext = model };
            var setup = Assert.IsType<Grid>(view.FindName("FocusSetupCard"));
            var timer = Assert.IsType<Grid>(view.FindName("CompactTimer"));
            model.PortraitClick();

            // The compact entry shell shows neither the focus setup card nor the timer.
            Assert.Equal(Visibility.Collapsed, setup.Visibility);
            Assert.Equal(Visibility.Collapsed, timer.Visibility);

            focus.Start(FgoPet.App.Panels.FocusPresetCatalog.Short, "servant-mash");
            focus.RaiseChanged();

            Assert.Equal(Visibility.Collapsed, setup.Visibility);
            Assert.Equal(Visibility.Visible, timer.Visibility);
            Assert.NotNull(view.FindName("FocusProgressArc"));
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
            // Probe the rendered chat entry chip; the entry surface must be opaque in both
            // themes so the glyph stays readable over whatever is behind the pet.
            var chip = Assert.IsType<Button>(view.FindName("ChatEntryButton"));
            var origin = chip.TransformToAncestor(view).Transform(new Point(0, 0));
            var pixel = new byte[4];
            bitmap.CopyPixels(
                new Int32Rect(
                    (int)((origin.X + chip.ActualWidth / 2) * 2),
                    (int)((origin.Y + chip.ActualHeight / 2) * 2),
                    1,
                    1),
                pixel,
                4,
                0);
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

    [Theory]
    [InlineData("FgoLight", "#FFFFFFFF", "#FF7050B8")]
    [InlineData("ModernGray", "#FF282332", "#FFC3A8FF")]
    public void Focus_cards_consume_the_theme_surface_and_action_resources(string theme, string content, string action)
    {
        StaRun(() =>
        {
            var model = new AttachedPanelViewModel(TimeProvider.System);
            var view = new AttachedPanelView { DataContext = model };
            view.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"/FgoPet.App;component/Themes/{theme}.xaml", UriKind.Relative),
            });
            model.PortraitClick();
            model.FocusClick();
            view.Measure(new Size(240, 240));
            view.Arrange(new Rect(0, 0, 240, 240));
            view.UpdateLayout();

            var card = Assert.IsType<Border>(view.FindName("CardSurface"));
            var setup = Assert.IsType<Grid>(view.FindName("FocusSetupCard"));
            var surface = Assert.IsType<SolidColorBrush>(card.Background);
            Assert.Equal((Color)ColorConverter.ConvertFromString(content), surface.Color);
            Assert.Equal(
                (Color)ColorConverter.ConvertFromString(action),
                Assert.IsType<SolidColorBrush>(Assert.IsType<Button>(view.FindName("StartFocusButton")).Background).Color);
            Assert.Equal(
                (Color)ColorConverter.ConvertFromString(content),
                Assert.IsType<SolidColorBrush>(Assert.IsType<Ellipse>(setup.Children[0]).Fill).Color);
        });
    }

    [Fact]
    public void Focus_state_text_and_controls_remain_truthful_when_paused()
    {
        StaRun(() =>
        {
            var focus = new FakeFocusService();
            var model = new AttachedPanelViewModel(TimeProvider.System, focus);
            var view = new AttachedPanelView { DataContext = model };
            model.PortraitClick();
            model.SetActiveServant("servant-mash");
            model.FocusClick();
            focus.Start(FgoPet.App.Panels.FocusPresetCatalog.Short, "servant-mash");
            focus.RaiseChanged();
            view.Measure(new Size(240, 240));
            view.Arrange(new Rect(0, 0, 240, 240));
            view.UpdateLayout();

            var phase = Assert.IsType<TextBlock>(view.FindName("CompactPhaseText"));
            var pause = Assert.IsType<Button>(view.FindName("PauseResumeButton"));
            Assert.Equal("专注中", phase.Text);
            Assert.Equal("暂停专注", pause.ToolTip?.ToString());

            focus.Pause();
            focus.RaiseChanged();
            view.UpdateLayout();

            Assert.Equal("已暂停", phase.Text);
            Assert.Equal("继续专注", pause.ToolTip?.ToString());
            Assert.NotEqual(Visibility.Collapsed, pause.Visibility);
        });
    }

    [Fact]
    public void More_entry_exposes_explicit_expand_and_collapse_affordance()
    {
        StaRun(() =>
        {
            var model = new AttachedPanelViewModel(TimeProvider.System);
            var view = new AttachedPanelView { DataContext = model };
            model.PortraitClick();
            var more = Assert.IsType<Button>(view.FindName("MoreEntryButton"));

            Assert.Equal("更多", more.ToolTip?.ToString());
            Assert.Equal("更多", System.Windows.Automation.AutomationProperties.GetName(more));

            more.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            Assert.Equal("收起更多", more.ToolTip?.ToString());
            Assert.Equal("收起更多", System.Windows.Automation.AutomationProperties.GetName(more));
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
        public void Pause() => Current = Current.RestorePaused();
        public void Resume() => Current = Current with
        {
            Status = Current.Status switch
            {
                FgoPet.Core.Focus.FocusStatus.PausedFocus => FgoPet.Core.Focus.FocusStatus.Focusing,
                FgoPet.Core.Focus.FocusStatus.PausedBreak => FgoPet.Core.Focus.FocusStatus.Breaking,
                _ => Current.Status,
            },
        };
        public void Stop() => Current = FocusSession.Idle;
        public void Tick() { }
        public void Restore() { }
        public void RaiseChanged() => SnapshotChanged?.Invoke(this, EventArgs.Empty);
    }
}
