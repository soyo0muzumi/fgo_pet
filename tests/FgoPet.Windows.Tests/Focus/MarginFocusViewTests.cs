using System.Windows;
using System.Windows.Controls;
using FgoPet.App.Focus;
using FgoPet.Core.Focus;
using FgoPet.Plugin.Focus.Desktop;
using Xunit;

namespace FgoPet.Windows.Tests.Focus;

public sealed class MarginFocusViewTests
{
    [Fact]
    public void Production_Margin_does_not_restore_legacy_buttons_after_focus_back() => StaRunner.Run(() =>
    {
        using var compact = new FocusCompactViewModel(new FocusOwner());
        using var panel = new FgoPet.App.Panels.AttachedPanelViewModel(TimeProvider.System, compact);
        var portrait = new FgoPet.App.Main.PortraitWindow(panel);
        using var tray = new FgoPet.App.Tray.TrayService();
        using var ui = new FgoPet.App.Bootstrap.DesktopAppUi(tray, null!, new FgoPet.App.Settings.SettingsViewModel(),
            portrait, null!, null!, null!, null!, attachedPanel: panel);
        try
        {
            panel.PortraitClick(); panel.FocusClick(); panel.Escape();
            Assert.Equal(FgoPet.Core.Panels.AttachedPanelState.Compact, panel.State);
            Assert.False(portrait.IsAttachedPanelVisible);
            panel.FocusClick();
            var shell = Assert.IsType<FgoPet.App.Panels.AttachedPanelView>(
                Assert.IsType<ContentControl>(portrait.FindName("PanelHost")).Content);
            var close = Assert.IsType<Button>(shell.FindName("CloseCompactButton"));
            Assert.Equal(Visibility.Visible, close.Visibility);
            close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(FgoPet.Core.Panels.AttachedPanelState.Compact, panel.State);
            Assert.False(portrait.IsAttachedPanelVisible);
            Assert.False(compact.IsActive);
            panel.FocusClick();
            Assert.Equal(Visibility.Visible, close.Visibility);
            portrait.HandleEscape();
            Assert.False(portrait.IsAttachedPanelVisible);
        }
        finally { portrait.Close(); }
    });

    [Fact]
    public Task Contextual_focus_and_today_entries_route_to_the_existing_owners() => StaRunner.RunAsync(() =>
    {
        var owner = new FocusOwner();
        using var compact = new FocusCompactViewModel(owner);
        compact.SetActiveServant("fixture");
        string? opened = null;
        using var panel = new FgoPet.App.Panels.AttachedPanelViewModel(TimeProvider.System, compact,
            toggleTransient: id => { opened = id; return true; });
        using var tray = new FgoPet.App.Tray.TrayService();
        using var ui = new FgoPet.App.Bootstrap.DesktopAppUi(tray, null!, new FgoPet.App.Settings.SettingsViewModel(),
            null!, null!, null!, null!, null!, attachedPanel: panel);
        var focus = ui.PortraitMenu.Items.OfType<MenuItem>().Single(item => (string)item.Header == "Focus");
        var today = ui.PortraitMenu.Items.OfType<MenuItem>().Single(item => (string)item.Header == "Today");
        focus.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.Equal(FgoPet.Core.Panels.AttachedPanelState.ExpandedFocus, panel.State);
        focus.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.Equal(FgoPet.Core.Panels.AttachedPanelState.Compact, panel.State);
        focus.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        compact.StartQuickFocus(25);
        var session = owner.Current;
        Assert.Equal(FgoPet.Core.Panels.AttachedPanelState.Compact, panel.State);
        focus.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.Equal(FgoPet.Core.Panels.AttachedPanelState.ExpandedFocus, panel.State);
        focus.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.Equal(FgoPet.Core.Panels.AttachedPanelState.Compact, panel.State);
        Assert.True(compact.IsActive);
        focus.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.Equal(1, owner.Starts);
        Assert.Equal(session, owner.Current);
        today.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.Equal("todo.peek", opened);
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public Task Ribbon_keeps_transparent_space_pass_through_and_visible_controls_interactive(double scale) => StaRunner.RunAsync(async () =>
    {
        var owner = new FocusOwner();
        using var compact = new FocusCompactViewModel(owner);
        using var panel = new FgoPet.App.Panels.AttachedPanelViewModel(TimeProvider.System, compact);
        var window = new FgoPet.App.Main.PortraitWindow(panel) { Left = -10000, Top = -10000 };
        try
        {
            window.Show();
            owner.Start(FocusPreset.Create(25, 5, 1), "fixture");
            var dpi = new FgoPet.Core.Geometry.Dpi2(scale, scale);
            var geometry = FgoPet.Core.Geometry.PortraitLayout.Calculate(
                new FgoPet.Core.Geometry.PortraitSourceGeometry(300, 600, 0, 0, 100, 100, 150, 300), 0.5, dpi);
            window.ArrangeOverlayPanel(geometry,
                new FgoPet.Core.Geometry.DeviceRect((int)(-10100 * scale), (int)(-10100 * scale), (int)(1200 * scale), (int)(900 * scale)), dpi);
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
            Assert.True(window.IsAttachedPanelVisible);
            var host = Assert.IsType<ContentControl>(window.FindName("PanelHost"));
            var shell = Assert.IsType<FgoPet.App.Panels.AttachedPanelView>(host.Content);
            var view = Assert.IsType<FocusCompactView>(Assert.IsType<ContentControl>(shell.FindName("CompactContentHost")).Content);
            var ribbon = Assert.IsType<Button>(view.FindName("RibbonButton"));
            Assert.True(window.IsAttachedPanelHit(ribbon.TranslatePoint(new Point(86, 9), window)));
            var ribbonY = window.Top + ribbon.TranslatePoint(new Point(86, 9), window).Y;
            Assert.InRange(ribbonY - window.PortraitScreenBounds.Bottom, 0, 18);
            Assert.False(window.IsAttachedPanelHit(view.TranslatePoint(new Point(86, 10), window)));
            Assert.False(Assert.IsType<Button>(shell.FindName("ChatEntryButton")).IsVisible);
            Assert.Equal(1, owner.Starts);
            owner.Stop();
            Assert.Equal(FgoPet.Core.Panels.AttachedPanelState.Collapsed, panel.State);
            Assert.False(window.IsAttachedPanelVisible);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task Options_and_ribbon_controls_are_progressively_disclosed() => StaRunner.RunAsync(async () =>
    {
        var owner = new FocusOwner();
        using var model = new FocusCompactViewModel(owner);
        model.SetActiveServant("fixture");
        using var view = (FocusCompactView)model.CreateView();
        var paper = new Border { Child = view, Padding = new Thickness(6), CornerRadius = new CornerRadius(10) };
        var window = new Window { Content = paper, Width = 220, Height = 220, Left = -10000, Top = -10000,
            ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize };
        try
        {
            window.Show();
            view.SetExpanded(true);
            await Capture(view, "focus-quick-options", 220, 200);
            Assert.IsType<Button>(view.FindName("SessionOptionsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(Visibility.Collapsed, Assert.IsType<StackPanel>(view.FindName("QuickDurations")).Visibility);
            Assert.Equal(Visibility.Visible, Assert.IsType<StackPanel>(view.FindName("SessionOptions")).Visibility);
            await Capture(view, "focus-session-options", 220, 220);
            window.Height = 150;
            view.MaxHeight = 136;
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            paper.UpdateLayout();
            var scroll = Assert.IsType<ScrollViewer>(view.FindName("SetupScroll"));
            Assert.True(scroll.ScrollableHeight > 0,
                $"Viewport {scroll.ViewportHeight}; extent {scroll.ExtentHeight}; actual {scroll.ActualHeight}; view {view.ActualHeight}");
            scroll.ScrollToEnd();
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            paper.UpdateLayout();
            var start = Assert.IsType<Button>(view.FindName("StartFocusButton"));
            Assert.InRange(start.TranslatePoint(new Point(0, start.ActualHeight), scroll).Y, 0, scroll.ActualHeight);
            await Capture(view, "focus-session-small-workspace", 220, 150);
            model.CustomFocusMinutesText = "4";
            Assert.False(model.CanStartFocus);
            Assert.True(model.CanStartQuickFocus);
            model.StartFocus();
            Assert.Equal(0, owner.Starts);
            model.CustomFocusMinutesText = "25";
            model.StartFocus();
            view.SetExpanded(false);
            await Capture(view, "focus-ribbon", 220, 142);
            var peek = Assert.IsType<StackPanel>(view.FindName("FocusPeek"));
            Assert.Equal(Visibility.Collapsed, peek.Visibility);
            Assert.IsType<Button>(view.FindName("RibbonButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(Visibility.Visible, peek.Visibility);
            view.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0)
                { RoutedEvent = System.Windows.Input.Mouse.MouseEnterEvent });
            Assert.IsType<Button>(view.FindName("RibbonButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(Visibility.Collapsed, peek.Visibility);
            view.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0)
                { RoutedEvent = System.Windows.Input.Mouse.MouseLeaveEvent });
            Assert.IsType<Button>(view.FindName("RibbonButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(Visibility.Visible, peek.Visibility);
            await Capture(view, "focus-hover-controls", 220, 142);
            peek.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice,
                0, System.Windows.Input.MouseButton.Left) { RoutedEvent = System.Windows.Input.Mouse.PreviewMouseDownEvent });
            Assert.Equal(Visibility.Visible, peek.Visibility);
            paper.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice,
                0, System.Windows.Input.MouseButton.Left) { RoutedEvent = System.Windows.Input.Mouse.PreviewMouseDownEvent });
            Assert.Equal(Visibility.Collapsed, peek.Visibility);
            Assert.True(model.IsActive);
            Assert.Equal(1, owner.Starts);
            Assert.IsType<Button>(view.FindName("RibbonButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            view.SetExpanded(true);
            view.SetExpanded(false);
            Assert.Equal(Visibility.Collapsed, peek.Visibility);
            Assert.IsType<Button>(view.FindName("RibbonButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.IsType<Button>(view.FindName("PauseResumeButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(FocusStatus.PausedFocus, owner.Current.Status);
            Assert.IsType<Button>(view.FindName("StopTimerButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(model.IsActive);
        }
        finally { window.Close(); paper.Child = null; }
    });

    private static async Task Capture(FrameworkElement view, string name, int width, int height)
    {
        var paper = Assert.IsType<Border>(view.Parent);
        var window = Assert.IsType<Window>(Window.GetWindow(view));
        var idle = Assert.IsType<Grid>(view.FindName("FocusSetupCard")).Visibility == Visibility.Visible;
        window.Width = width;
        window.Height = height;
        view.MaxHeight = height - (idle ? 14 : 12);
        paper.BorderThickness = idle ? new Thickness(1) : new Thickness(0);
        paper.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(207, 200, 188));
        paper.Background = idle ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(251, 250, 248)) : null;
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        paper.UpdateLayout();
        var directory = Environment.GetEnvironmentVariable("FGO_PET_MARGIN_FEATURE_CAPTURE_DIR");
        if (string.IsNullOrWhiteSpace(directory)) return;
        System.IO.Directory.CreateDirectory(directory);
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(width * 2, height * 2, 192, 192,
            System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(paper);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var file = System.IO.File.Create(System.IO.Path.Combine(directory, name + "-wpf-render.png"));
        encoder.Save(file);
        System.IO.File.AppendAllText(System.IO.Path.Combine(directory, "feature-verification.jsonl"),
            System.Text.Json.JsonSerializer.Serialize(new { at = DateTimeOffset.UtcNow, request = "offline-focus-fixture", stage = name, result = "rendered" }) + "\n");
    }

    [Theory]
    [InlineData(10)]
    [InlineData(25)]
    [InlineData(50)]
    public Task Quick_duration_starts_one_owner_session_and_collapses_to_the_ribbon(int minutes) => StaRunner.RunAsync(() =>
    {
        var owner = new FocusOwner();
        using var model = new FocusCompactViewModel(owner);
        model.SetActiveServant("fixture");
        using var view = (FocusCompactView)model.CreateView();
        view.SetExpanded(true);
        Assert.Equal("1", model.CustomCyclesText);
        var quick = Assert.IsType<Button>(view.FindName($"Quick{minutes}Button"));
        quick.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(1, owner.Starts);
        Assert.Equal(minutes * 60, owner.Current.FocusSeconds);
        Assert.Equal(1, owner.Current.TotalCycles);
        Assert.Equal(Visibility.Collapsed, Assert.IsType<Grid>(view.FindName("FocusSetupCard")).Visibility);
        Assert.Equal(Visibility.Visible, Assert.IsType<Grid>(view.FindName("CompactTimer")).Visibility);
        Assert.Equal(Visibility.Collapsed, Assert.IsType<StackPanel>(view.FindName("FocusPeek")).Visibility);
        quick.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(1, owner.Starts);
        return Task.CompletedTask;
    });

    [Fact]
    public Task Expanded_options_keep_break_cycles_and_recreated_views_do_not_restart_the_owner() => StaRunner.RunAsync(async () =>
    {
        var owner = new FocusOwner();
        using var model = new FocusCompactViewModel(owner);
        model.SetActiveServant("fixture");
        model.SelectCustomPreset();
        model.CustomFocusMinutesText = "50";
        model.CustomBreakMinutesText = "10";
        model.CustomCyclesText = "3";
        model.StartFocus();
        var session = owner.Current;
        Assert.Equal(600, session.BreakSeconds);
        Assert.Equal(3, session.TotalCycles);
        using (var first = (FocusCompactView)model.CreateView()) first.SetExpanded(false);
        using var reopened = (FocusCompactView)model.CreateView();
        Assert.Equal(session, owner.Current);
        Assert.Equal(1, owner.Starts);
        Assert.Equal(Visibility.Visible, Assert.IsType<Grid>(reopened.FindName("CompactTimer")).Visibility);
        model.PauseTimer();
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        Assert.Equal("Paused", Assert.IsType<TextBlock>(reopened.FindName("CompactPhaseText")).Text);
        model.ResumeTimer();
        Assert.Equal(FocusStatus.Focusing, owner.Current.Status);
        model.StopTimer();
        Assert.False(model.IsActive);
    });

    private sealed class FocusOwner : IFocusSessionService
    {
        public FocusSession Current { get; private set; } = FocusSession.Idle;
        public int Starts { get; private set; }
        public event EventHandler? SnapshotChanged;
        public event EventHandler? PersistenceFailed { add { } remove { } }
        public void Start(FocusPreset preset, string servantId)
        {
            Starts++;
            Current = FocusSession.Start("fixture-session", servantId, preset, DateTimeOffset.UtcNow);
            SnapshotChanged?.Invoke(this, EventArgs.Empty);
        }
        public void Pause() { Current = Current.RestorePaused(); SnapshotChanged?.Invoke(this, EventArgs.Empty); }
        public void Resume() { Current = Current with { Status = FocusStatus.Focusing }; SnapshotChanged?.Invoke(this, EventArgs.Empty); }
        public void Stop() { Current = FocusSession.Idle; SnapshotChanged?.Invoke(this, EventArgs.Empty); }
        public void Tick() { }
        public void Restore() { }
    }
}
