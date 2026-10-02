using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using FgoPet.App.Settings;
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
    private readonly DialogueWindowViewModel _viewModel;
    private readonly ChatWebSurfaceFactory? _factory;
    private readonly ChatWebHostActions _actions;
    private readonly Func<string, WorkspaceNavigation, bool>? _workspace;
    private readonly string? _defaultWorkspace;
    private readonly CompanionPresentation? _presentation;
    private WebView2SurfaceHost? _surface;
    private bool _disposed;
    internal Action? PresentationRequestedHandler { get; set; }
    public event Action? Hidden;

    public DialogueWindow(DialogueWindowViewModel viewModel,
        IAttachedPanelLauncher? panel = null, ISettingsNavigator? settingsNavigation = null,
        IWorkspaceCatalog? workspaces = null, CompanionPresentation? presentation = null,
        IClipboardWriter? clipboard = null, ChatWebSurfaceFactory? webFactory = null,
        Func<string, WorkspaceNavigation, bool>? workspaceNavigation = null)
    {
        _viewModel = viewModel; _factory = webFactory; _workspace = workspaceNavigation;
        _defaultWorkspace = workspaces?.Workspaces.FirstOrDefault()?.Id; _presentation = presentation;
        InitializeComponent(); DataContext = viewModel;
        _actions = new(viewModel, this, clipboard ?? new SystemClipboardWriter(), panel, workspaces, workspaceNavigation);
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
        stack.Children.Add(new TextBlock { Text = "聊天页面暂时无法打开。", TextWrapping = TextWrapping.Wrap });
        var retry = new Button { Content = "重试", Margin = new Thickness(0, 16, 0, 0), IsEnabled = _factory is not null };
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
        else { Show(); Activate(); }
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
        if (_viewModel.Conversation.IsStreaming) _viewModel.ModelSelection.BeginGeneration();
        else _viewModel.ModelSelection.EndGeneration();
    }
    private void OnExpressionRequested(ExpressionSemantic semantic)
    {
        var role = _presentation?.ActiveRole;
        var conversation = _viewModel.Conversation.CurrentConversationId;
        var servant = _viewModel.Conversation.ActiveServantId;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_disposed || role is null || _presentation is null || !ReferenceEquals(role, _presentation.ActiveRole)
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
    internal void SetExpanded(bool expanded)
    {
        Width = expanded ? 940 : 480; Height = expanded ? 720 : 620;
        var area = System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(this).Handle).WorkingArea;
        var dpi = VisualTreeHelper.GetDpi(this);
        var width = Math.Min(Width, area.Width / dpi.DpiScaleX);
        var height = Math.Min(Height, area.Height / dpi.DpiScaleY);
        MinWidth = Math.Min(320, width); MinHeight = Math.Min(400, height);
        Width = width; Height = height;
        Left = Math.Clamp(Left, area.Left / dpi.DpiScaleX, area.Right / dpi.DpiScaleX - width);
        Top = Math.Clamp(Top, area.Top / dpi.DpiScaleY, area.Bottom / dpi.DpiScaleY - height);
    }
    private void DisposePresentation()
    {
        if (_disposed) return; _disposed = true;
        _viewModel.StopSpeech();
        _viewModel.OpenRequested -= OnOpenRequested; _viewModel.NavigationRequested -= OnNavigationRequested;
        _viewModel.Conversation.NewWorkspaceItemRequested -= OnNewWorkspaceItem;
        _viewModel.Conversation.ExpressionRequested -= OnExpressionRequested;
        _viewModel.Conversation.PropertyChanged -= OnConversationChanged;
        Loaded -= OnLoaded; Closing -= OnClosing; IsVisibleChanged -= OnVisibleChanged;
        Activated -= OnActivated; Deactivated -= OnDeactivated; Closed -= OnClosed;
        Dispatcher.ShutdownStarted -= OnShutdown; PresentationRequestedHandler = null;
        _surface?.Dispose(); _actions.Dispose();
    }
    public void Dispose() { DisposePresentation(); if (IsLoaded && !Dispatcher.HasShutdownStarted) Close(); }
}
