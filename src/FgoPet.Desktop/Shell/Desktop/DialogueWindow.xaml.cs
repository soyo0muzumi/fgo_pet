using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using FgoPet.App.Settings;
using FgoPet.App.Windowing;
using FgoPet.Core.Geometry;
using FgoPet.Core.Windowing;
using FgoPet.Dialogue.Contracts;
using FgoPet.Core.Panels;
using FgoPet.Core.Portraits;
using FgoPet.Kernel.Companion;
using FgoPet.UiSdk;

namespace FgoPet.App.Dialogue;
public interface IClipboardWriter { void SetText(string text); }
internal sealed class SystemClipboardWriter : IClipboardWriter
{ public void SetText(string text) => Clipboard.SetText(text); }

/// <summary>One Web chat surface over the original shared conversation.</summary>
public partial class DialogueWindow : Window, IDisposable
{
    // Temporary acceptance diagnostics, observed only by the target test. Remove before release.
    private static readonly System.Diagnostics.ActivitySource OperationTiming = new("FgoPet.TestTiming.DialogueWindow");
    private readonly DialogueWindowViewModel _viewModel;
    private readonly ChatWebSurfaceFactory? _factory;
    private readonly ChatWebHostActions _actions;
    private readonly Func<string, WorkspaceNavigation, bool>? _workspace;
    private readonly string? _defaultWorkspace;
    private readonly CompanionPresentation? _presentation;
    private WebView2SurfaceHost? _surface;
    private bool _disposed;
    private long _expressionGeneration;
    // This Window survives Hide/Show; the existing portrait/menu entry restores this presentation.
    private ChatWebWindowPresentation _windowPresentation = new("composer", false);
    internal ChatWebWindowPresentation WindowPresentation => _windowPresentation;
    internal Action? PresentationRequestedHandler { get; set; }
    internal Action? LayoutRequestedHandler { get; set; }
    public event Action? Hidden;

    public DialogueWindow(DialogueWindowViewModel viewModel,
        IAttachedPanelLauncher? panel = null, ISettingsNavigator? settingsNavigation = null,
        IWorkspaceCatalog? workspaces = null, CompanionPresentation? presentation = null,
        IClipboardWriter? clipboard = null, ChatWebSurfaceFactory? webFactory = null,
        Func<string, WorkspaceNavigation, bool>? workspaceNavigation = null,
        FgoPet.Dialogue.Contracts.IChatWorkspaceSelection? localWorkspace = null,
        FgoPet.Dialogue.Contracts.IChatImageSelection? selectedImage = null,
        FgoPet.Extensibility.IBrowserVisualAccess? browserImages = null)
    {
        _viewModel = viewModel; _factory = webFactory; _workspace = workspaceNavigation;
        _defaultWorkspace = workspaces?.Workspaces.FirstOrDefault()?.Id; _presentation = presentation;
        InitializeComponent(); DataContext = viewModel;
        _actions = new(viewModel, this, clipboard ?? new SystemClipboardWriter(), panel, workspaces, workspaceNavigation, localWorkspace, selectedImage: selectedImage, browserImages: browserImages);
        viewModel.OpenRequested += OnOpenRequested;
        viewModel.NavigationRequested += OnNavigationRequested;
        viewModel.Conversation.NewWorkspaceItemRequested += OnNewWorkspaceItem;
        viewModel.Conversation.ExpressionRequested += OnExpressionRequested;
        viewModel.Conversation.PropertyChanged += OnConversationChanged;
        Loaded += OnLoaded;
        Closing += OnClosing;
        IsVisibleChanged += OnVisibleChanged;
        Activated += OnActivated; Deactivated += OnDeactivated;
        Closed += OnClosed;
        Dispatcher.ShutdownStarted += OnShutdown;
        CreateSurface();
    }
    private void CreateSurface()
    {
        _surface?.Dispose(); _surface = null;
        if (_factory is null) { ShowFailure(); return; }
        try { _surface = _factory.CreateView(_actions); ChatSurface.Content = _surface; }
        catch (Exception) { ShowFailure(); }
    }
    private void ShowFailure()
    {
        var stack = new StackPanel { Margin = new Thickness(24), VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(new TextBlock { Text = MarginStrings.Get("chat.unavailable"), TextWrapping = TextWrapping.Wrap });
        var retry = new Button { Content = MarginStrings.Get("action.retry"), Margin = new Thickness(0, 16, 0, 0), IsEnabled = _factory is not null };
        retry.Click += async (_, _) => { CreateSurface(); await InitializeSurfaceAsync(); };
        stack.Children.Add(retry); ChatSurface.Content = stack;
    }
    private async void OnLoaded(object sender, RoutedEventArgs args)
    { await _viewModel.EnsureRoleInfoAsync(); if (!_disposed) await InitializeSurfaceAsync(); }
    private async Task InitializeSurfaceAsync()
    {
        var surface = _surface;
        if (surface is null) return;
        await surface.InitializeAsync();
        if (!_disposed && ReferenceEquals(_surface, surface) && surface.State == WebSurfaceState.Failed)
            ShowFailure();
    }
    private void OnOpenRequested()
    {
        if (_disposed) return;
        if (PresentationRequestedHandler is { } present) present();
        else { RestorePresentation(); }
    }
    internal void RestorePresentation()
    {
        if (_disposed) return;
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Show(); Activate();
    }
    private void OnNavigationRequested(object? sender, MainNavigationTarget target)
    {
        if (target is not (MainNavigationTarget.Schedule or MainNavigationTarget.CapabilityDetail) || _defaultWorkspace is null) return;
        // NavigateTo fills the target context after notifying; read it once that call returns.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_disposed || _viewModel.CurrentTarget != target) return;
            var itemId = _viewModel.CurrentContext.SelectedId;
            _workspace?.Invoke(_defaultWorkspace, new(string.IsNullOrEmpty(itemId)
                ? WorkspaceNavigationKind.Overview : WorkspaceNavigationKind.ExistingItem, itemId));
        }));
    }
    private void OnNewWorkspaceItem()
    { if (_defaultWorkspace is not null) _workspace?.Invoke(_defaultWorkspace, new(WorkspaceNavigationKind.NewItem)); }
    private void OnConversationChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(ConversationViewModel.IsStreaming)) return;
        if (_viewModel.Conversation.IsStreaming)
        {
            Interlocked.Increment(ref _expressionGeneration);
            _viewModel.ModelSelection.BeginGeneration();
        }
        else _viewModel.ModelSelection.EndGeneration();
    }
    private void OnExpressionRequested(ExpressionSemantic semantic)
    {
        var generation = Volatile.Read(ref _expressionGeneration);
        var role = _presentation?.ActiveRole;
        var conversation = _viewModel.Conversation.CurrentConversationId;
        var servant = _viewModel.Conversation.ActiveServantId;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_disposed || generation != Volatile.Read(ref _expressionGeneration)
                || role is null || _presentation is null || !ReferenceEquals(role, _presentation.ActiveRole)
                || conversation != _viewModel.Conversation.CurrentConversationId || servant != _viewModel.Conversation.ActiveServantId) return;
            _presentation.Apply(role, semantic);
        }));
    }
    private void OnClosing(object? sender, CancelEventArgs args)
    {
        if (_disposed || Dispatcher.HasShutdownStarted) return;
        args.Cancel = true; Hide();
    }
    private async void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs args)
    {
        if (_disposed) return;
        if (IsVisible)
        {
            _viewModel.NotifyActivated();
            if (_surface?.State == WebSurfaceState.Closed) { CreateSurface(); await InitializeSurfaceAsync(); }
            _surface?.PostEvent(new { type = "chat.visibility", visible = true });
        }
        else
        {
            _surface?.PostEvent(new { type = "chat.visibility", visible = false });
            _viewModel.NotifyWindowHidden(); _viewModel.StopSpeech(); Hidden?.Invoke();
        }
    }
    private void OnActivated(object? sender, EventArgs args) => _viewModel.NotifyActivated();
    private void OnDeactivated(object? sender, EventArgs args) => _viewModel.NotifyDeactivated();
    private void OnClosed(object? sender, EventArgs args) => DisposePresentation();
    private void OnShutdown(object? sender, EventArgs args) => DisposePresentation();
    private void OnDragHandleMouseDown(object sender, MouseButtonEventArgs args)
    {
        // This handler belongs only to the native strip, outside the WebView's input and text.
        args.Handled = true;
        if (args.ChangedButton != MouseButton.Left || args.ButtonState != MouseButtonState.Pressed
            || args.ClickCount != 1 || WindowState != WindowState.Normal) return;
        DragMove();
        LayoutRequestedHandler?.Invoke();
    }

    internal void SetExpanded(bool expanded)
    {
        // Keep the old action available for legacy pages without claiming it is a composer.
        _windowPresentation = new("conversation", expanded, _windowPresentation.ComposerHeight);
        ChatDragHandle.Visibility = expanded ? Visibility.Collapsed : Visibility.Visible;
        WindowStyle = expanded ? WindowStyle.SingleBorderWindow : WindowStyle.None;
        System.Windows.Shell.WindowChrome.SetWindowChrome(this, expanded ? null : new System.Windows.Shell.WindowChrome
        { CaptionHeight = 0, CornerRadius = new CornerRadius(10), GlassFrameThickness = new Thickness(0), ResizeBorderThickness = new Thickness(6) });
        MinWidth = 328; MinHeight = 388;
        Width = expanded ? 824 : 376; Height = expanded ? 740 : 500;
        if (LayoutRequestedHandler is { } place) { place(); return; }
        var area = System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(this).Handle).WorkingArea;
        var dpi = VisualTreeHelper.GetDpi(this);
        var width = Math.Min(Width, area.Width / dpi.DpiScaleX);
        var height = Math.Min(Height, area.Height / dpi.DpiScaleY);
        MinWidth = Math.Min(328, width); MinHeight = Math.Min(388, height);
        Width = width; Height = height;
        Left = Math.Clamp(Left, area.Left / dpi.DpiScaleX, area.Right / dpi.DpiScaleX - width);
        Top = Math.Clamp(Top, area.Top / dpi.DpiScaleY, area.Bottom / dpi.DpiScaleY - height);
    }
    internal void SetPresentation(string view, bool wide, double? composerHeight = null)
    {
        if (view is not ("composer" or "conversation")) throw new ArgumentOutOfRangeException(nameof(view));
        var contentHeight = composerHeight ?? _windowPresentation.ComposerHeight;
        if (!double.IsFinite(contentHeight) || contentHeight is < 160 or > 360)
            throw new ArgumentOutOfRangeException(nameof(composerHeight));
        if (_disposed) return;
        if (WindowState != WindowState.Normal) WindowState = WindowState.Normal;

        var area = System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(this).Handle).WorkingArea;
        var scale = VisualTreeHelper.GetDpi(this);
        var dpi = new Dpi2(scale.DpiScaleX, scale.DpiScaleY);
        var width = view == "conversation" && wide ? 760d : 440d;
        var height = view == "composer" ? contentHeight + 22d : wide ? 740d : 630d;
        var widthPx = (int)Math.Ceiling(width * dpi.X);
        var heightPx = (int)Math.Ceiling(height * dpi.Y);
        if (!TransientSurfacePlacement.TryReadNativeBounds(this, out var previous))
        {
            // Commands normally arrive after the HWND is created. Keep the fallback finite.
            var left = double.IsFinite(Left) ? (int)Math.Round(Left * dpi.X) : area.Right - widthPx;
            var top = double.IsFinite(Top) ? (int)Math.Round(Top * dpi.Y) : area.Bottom - heightPx;
            previous = new DeviceRect(left, top, widthPx, heightPx);
        }
        var proposed = new DeviceRect(previous.Right - widthPx, previous.Bottom - heightPx, widthPx, heightPx);
        var visible = TransientSurfacePlacement.Constrain(proposed,
            new DeviceRect(area.Left, area.Top, area.Width, area.Height), dpi, width, height, marginDip: 16);

        ChatDragHandle.Visibility = Visibility.Visible;
        WindowStyle = WindowStyle.None;
        System.Windows.Shell.WindowChrome.SetWindowChrome(this, new System.Windows.Shell.WindowChrome
        { CaptionHeight = 0, CornerRadius = new CornerRadius(10), GlassFrameThickness = new Thickness(0), ResizeBorderThickness = new Thickness(6) });
        MinWidth = Math.Min(328d, visible.Width / dpi.X);
        MinHeight = Math.Min(view == "conversation" ? 388d : 182d, visible.Height / dpi.Y);
        Width = visible.Width / dpi.X; Height = visible.Height / dpi.Y;
        // WPF dimensions stay in DIP; the final move uses physical pixels, including mixed-DPI desktops.
        Left = visible.X / dpi.X; Top = visible.Y / dpi.Y;
        TransientSurfacePlacement.ApplyNativeBounds(this, visible);
        _windowPresentation = new(view, wide, contentHeight);
        // Refresh the existing placement owner's requested size and retained physical bounds.
        LayoutRequestedHandler?.Invoke();
    }

    private void DisposePresentation()
    {
        if (_disposed) return; _disposed = true;
        using (OperationTiming.StartActivity("dialogue.window.stop_speech"))
            _viewModel.StopSpeech();
        using (OperationTiming.StartActivity("dialogue.window.unsubscribe_view_model"))
        {
            _viewModel.OpenRequested -= OnOpenRequested; _viewModel.NavigationRequested -= OnNavigationRequested;
            _viewModel.Conversation.NewWorkspaceItemRequested -= OnNewWorkspaceItem;
            _viewModel.Conversation.ExpressionRequested -= OnExpressionRequested;
            _viewModel.Conversation.PropertyChanged -= OnConversationChanged;
        }
        using (OperationTiming.StartActivity("dialogue.window.unsubscribe_window"))
        {
            Loaded -= OnLoaded; Closing -= OnClosing; IsVisibleChanged -= OnVisibleChanged;
            Activated -= OnActivated; Deactivated -= OnDeactivated; Closed -= OnClosed;
            Dispatcher.ShutdownStarted -= OnShutdown; PresentationRequestedHandler = null; LayoutRequestedHandler = null;
        }
        using (OperationTiming.StartActivity("dialogue.window.dispose_surface"))
            _surface?.Dispose();
        using (OperationTiming.StartActivity("dialogue.window.dispose_actions"))
            _actions.Dispose();
    }
    public void Dispose()
    {
        using (OperationTiming.StartActivity("dialogue.window.dispose_presentation"))
            DisposePresentation();
        using (OperationTiming.StartActivity("dialogue.window.close_if_loaded"))
            if (IsLoaded && !Dispatcher.HasShutdownStarted) Close();
    }
}
