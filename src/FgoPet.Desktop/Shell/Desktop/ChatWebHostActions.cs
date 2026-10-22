using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FgoPet.App.Settings;
using FgoPet.Core.Panels;
using FgoPet.Dialogue.Contracts;
using FgoPet.UiSdk;
using FgoPet.Extensibility;

namespace FgoPet.App.Dialogue;

/// <summary>Maps finite Web actions to the existing native owners.</summary>
internal sealed class ChatWebHostActions : IChatWebHostActions, IDisposable
{
    private readonly DialogueWindowViewModel _vm;
    private readonly DialogueWindow _window;
    private readonly IAttachedPanelLauncher? _panel;
    private readonly IClipboardWriter _clipboard;
    private readonly Func<string, WorkspaceNavigation, bool>? _workspace;
    private readonly string? _defaultWorkspace;
    private readonly IChatWorkspaceSelection? _localWorkspace;
    private readonly Func<string?> _chooseDirectory;
    private readonly IChatImageSelection? _selectedImage;
    private readonly Func<string?> _chooseImage;
    private readonly IBrowserVisualAccess? _browserImages;
    private string? _avatarPath, _avatar;
    private bool _disposed;
    private NativeApprovalDialog? _approvalDialog;
    public event EventHandler? Changed;
    public async ValueTask<WebSurfaceCommandResult> ImportImageAsync(byte[] bytes, string mimeType, CancellationToken token)
    {
        if (_disposed || !_window.IsVisible || _selectedImage?.CanChoose != true)
            return new(false, ErrorCode: "CHAT_NOT_AVAILABLE");
        return await _selectedImage.ImportAsync(bytes, mimeType, token)
            ? new(true) : new(false, ErrorCode: "CHAT_IMAGE_INVALID");
    }
    public ChatWebHostActions(DialogueWindowViewModel vm, DialogueWindow window, IClipboardWriter clipboard,
        IAttachedPanelLauncher? panel, IWorkspaceCatalog? catalog, Func<string, WorkspaceNavigation, bool>? workspace,
        IChatWorkspaceSelection? localWorkspace = null, Func<string?>? chooseDirectory = null,
        IChatImageSelection? selectedImage = null, Func<string?>? chooseImage = null, IBrowserVisualAccess? browserImages = null)
    {
        _vm = vm; _window = window; _clipboard = clipboard; _panel = panel; _workspace = workspace;
        _defaultWorkspace = catalog?.Workspaces.FirstOrDefault()?.Id;
        _localWorkspace = localWorkspace;
        _selectedImage = selectedImage;
        _browserImages = browserImages;
        _chooseImage = chooseImage ?? (() =>
        {
            var picker = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select an image to share with the currently disclosed model when you Send",
                Filter = "Images (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg", Multiselect = false,
                CheckFileExists = true, CheckPathExists = true,
            };
            return picker.ShowDialog(_window) == true ? picker.FileName : null;
        });
        if (_selectedImage is not null) _selectedImage.Changed += OnChanged;
        _chooseDirectory = chooseDirectory ?? (() =>
        {
            var picker = new Microsoft.Win32.OpenFolderDialog
            {
                Title = MarginStrings.Get("chat.choose.workspace"), Multiselect = false,
            };
            return picker.ShowDialog(_window) == true ? picker.FolderName : null;
        });
        if (_localWorkspace is not null) _localWorkspace.Changed += OnChanged;
        vm.PropertyChanged += OnChanged;
        vm.ModelSelection.PropertyChanged += OnChanged;
        vm.ProjectSelection.PropertyChanged += OnChanged;
    }
    private void OnChanged(object? sender, EventArgs args) => Changed?.Invoke(this, EventArgs.Empty);
    public ChatWebHostPresentation ReadPresentation()
    {
        if (_avatarPath != _vm.SelectedServant?.PreviewSource)
        { _avatarPath = _vm.SelectedServant?.PreviewSource; _avatar = Avatar(_avatarPath); }
        var streaming = _vm.Conversation.IsStreaming;
        var roleName = _vm.SelectedServant is not null ? _vm.ActiveServantDisplayName
            : MarginStrings.Get(string.IsNullOrWhiteSpace(_vm.Conversation.ActiveServantId) ? "chat.noCharacter" : "chat.currentCharacter");
        return new(roleName, _avatar,
            _vm.ProjectSelection.Projects.Select(p => new ChatWebChoice(p.Id, p.Label,
                p.Id == _vm.Conversation.SessionContext.ProjectId, !streaming)).ToArray(),
            _vm.ModelSelection.Models.Select(m => new ChatWebChoice(m.Id, m.DisplayName,
                m.Id == _vm.ModelSelection.SelectedModelId, !_vm.ModelSelection.IsGenerating)).ToArray(),
            _vm.ProjectSelection.IsLoading, _vm.ProjectSelection.StatusText,
            new(_panel is not null, _workspace is not null && _defaultWorkspace is not null,
                _workspace is not null && _defaultWorkspace is not null, true, true, _vm.Conversation.CanOpenModelSettings,
                _vm.ProjectSelection.IsAvailable && !streaming,
                _localWorkspace?.CanChoose == true,
                _localWorkspace?.Label is not null && !streaming,
                _selectedImage?.CanChoose == true,
                _selectedImage?.Label is not null && !streaming && !_vm.Conversation.HasPendingRun,
                _browserImages?.IsAvailable == true && (_browserImages.Enabled || (!streaming && !_vm.Conversation.HasPendingRun))),
            _localWorkspace?.Label, _selectedImage?.Label, _selectedImage?.Disclosure ?? "");
    }
    public ValueTask<ChatApprovalDecision> ReviewApprovalAsync(ChatWebApproval approval, CancellationToken cancellationToken)
    {
        if (_disposed || cancellationToken.IsCancellationRequested || !_window.IsVisible
            || _window.Dispatcher.HasShutdownStarted || !_window.Dispatcher.CheckAccess())
            return ValueTask.FromResult(ChatApprovalDecision.Cancel);
        var dialog = new NativeApprovalDialog(approval) { Owner = _window };
        _approvalDialog = dialog;
        void CloseInvalidReview(object? sender, EventArgs args) { if (!_window.IsVisible || _disposed) dialog.Close(); }
        void OwnerClosed(object? sender, EventArgs args) => dialog.Close();
        _window.IsVisibleChanged += VisibilityChanged;
        _window.Closed += OwnerClosed;
        void VisibilityChanged(object sender, DependencyPropertyChangedEventArgs args) => CloseInvalidReview(sender, EventArgs.Empty);
        using var registration = cancellationToken.Register(() =>
        {
            if (!dialog.Dispatcher.HasShutdownStarted)
                _ = dialog.Dispatcher.BeginInvoke(new Action(() => dialog.Close()));
        });
        try
        {
            dialog.ShowDialog();
            return ValueTask.FromResult(!_disposed && _window.IsVisible && !cancellationToken.IsCancellationRequested
                ? dialog.Decision : ChatApprovalDecision.Cancel);
        }
        finally
        {
            if (ReferenceEquals(_approvalDialog, dialog)) _approvalDialog = null;
            _window.IsVisibleChanged -= VisibilityChanged;
            _window.Closed -= OwnerClosed;
        }
    }

    public async ValueTask<WebSurfaceCommandResult> HandleAsync(ChatWebHostRequest request, CancellationToken cancellationToken)
    {
        if (_disposed || cancellationToken.IsCancellationRequested || _window.Dispatcher.HasShutdownStarted)
            return new(false, ErrorCode: "CHAT_HOST_CLOSED");
        if (!_window.Dispatcher.CheckAccess()) return new(false, ErrorCode: "CHAT_HOST_THREAD");
        var matches = _vm.Conversation.Turns.Where(t => t.MessageId == request.TargetId).Take(2).ToArray();
        var turn = matches.Length == 1 ? matches[0] : null;
        switch (request.Action)
        {
            case ChatWebHostAction.ConfigureBrowserImages when _window.IsVisible && _browserImages?.IsAvailable == true
                && (_browserImages.Enabled || (!_vm.Conversation.IsStreaming && !_vm.Conversation.HasPendingRun)):
                var enable = !_browserImages.Enabled;
                var question = enable
                    ? "允许 FGO Pet 在对话需要时读取网页图片吗？\n\n页面截图（可能包含登录后可见的内容）会发送给当前选择的模型。每次任务最多一张；不会自动发送整页或后台截图。\n\n使用本机 Buddy 时，截图可能落盘、重新编码并转发给腾讯 CodeBuddy CN；代理可能重试或换号。同一张图可能随任务多次发送，停止无法撤回已发送的图片。\n\n此权限用于之后的对话，可在此处关闭。Chrome 的连接批准仍然保留。"
                    : "关闭网页读图权限吗？关闭后，模型将不能获取新的网页截图。";
                if (MessageBox.Show(_window, question, "网页读图权限", MessageBoxButton.YesNo,
                    MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return new(true);
                if (cancellationToken.IsCancellationRequested || !_window.IsVisible || _disposed
                    || (enable && (_vm.Conversation.IsStreaming || _vm.Conversation.HasPendingRun))) return new(false, ErrorCode: "CHAT_NOT_AVAILABLE");
                if (!_browserImages.TrySetEnabled(enable)) return new(false, ErrorCode: "CHAT_HOST_ACTION_FAILED");
                Changed?.Invoke(this, EventArgs.Empty);
                break;
            case ChatWebHostAction.ChooseImage when _window.IsVisible && _selectedImage?.CanChoose == true:
                if (!await _selectedImage.ChooseAsync(() =>
                {
                    var image = _chooseImage();
                    return cancellationToken.IsCancellationRequested || !_window.IsVisible ? null : image;
                }, cancellationToken)) return new(false, ErrorCode: "CHAT_HOST_ACTION_FAILED");
                break;
            case ChatWebHostAction.ClearImage when _window.IsVisible && !_vm.Conversation.IsStreaming
                && !_vm.Conversation.HasPendingRun && _selectedImage?.Label is not null:
                _selectedImage.Clear(); break;
            case ChatWebHostAction.ChooseWorkspace when _window.IsVisible && _localWorkspace?.CanChoose == true:
                if (!_localWorkspace.Choose(() =>
                {
                    var directory = _chooseDirectory();
                    return cancellationToken.IsCancellationRequested || !_window.IsVisible ? null : directory;
                })) return new(false, ErrorCode: "CHAT_HOST_ACTION_FAILED");
                break;
            case ChatWebHostAction.ClearWorkspace when _window.IsVisible && !_vm.Conversation.IsStreaming && _localWorkspace?.Label is not null:
                _localWorkspace.Clear(); break;
            case ChatWebHostAction.Hide: _window.Hide(); break;
            case ChatWebHostAction.SetExpanded when request.Expanded is { } expanded: _window.SetExpanded(expanded); break;
            case ChatWebHostAction.CopyTurn when turn?.Actions.CanCopy == true && _window.IsVisible:
                try { _clipboard.SetText(turn.Text); }
                catch (ExternalException) { return new(false, ErrorCode: "CHAT_CLIPBOARD_UNAVAILABLE"); }
                break;
            case ChatWebHostAction.ReadTurn when turn?.CanReadAloud == true && _window.IsVisible:
                if (turn.SpeechNeedsConfiguration) _vm.NavigateToSettings(SettingsSection.Speech);
                else _ = ReadAsync(turn);
                break;
            case ChatWebHostAction.OpenWorkspace when turn?.CanOpenWorkspace == true && _workspace is not null:
                if (!_workspace(turn.WorkspaceId!, new(WorkspaceNavigationKind.ExistingItem, turn.CreatedItemId))) return new(false, ErrorCode: "CHAT_TARGET_UNAVAILABLE"); break;
            case ChatWebHostAction.OpenWorkspaceOverview when _workspace is not null && _defaultWorkspace is not null:
                if (!_workspace(_defaultWorkspace, new(WorkspaceNavigationKind.Overview))) return new(false, ErrorCode: "CHAT_TARGET_UNAVAILABLE"); break;
            case ChatWebHostAction.NewWorkspaceItem when _workspace is not null && _defaultWorkspace is not null:
                if (!_workspace(_defaultWorkspace, new(WorkspaceNavigationKind.NewItem))) return new(false, ErrorCode: "CHAT_TARGET_UNAVAILABLE"); break;
            case ChatWebHostAction.OpenFocus when _panel is not null:
                if (_panel.State == FgoPet.Core.Panels.AttachedPanelState.Collapsed) _panel.PortraitClick();
                if (_panel.State != FgoPet.Core.Panels.AttachedPanelState.ExpandedFocus) _panel.FocusClick();
                _window.Hide(); break;
            case ChatWebHostAction.OpenPersonalizationSettings: _vm.NavigateToSettings(SettingsSection.Personalization); break;
            case ChatWebHostAction.OpenSpeechSettings: _vm.NavigateToSettings(SettingsSection.Speech); break;
            case ChatWebHostAction.OpenModelSettings when _vm.Conversation.CanOpenModelSettings:
                _vm.NavigateToSettings(SettingsSection.ModelConnection); break;
            case ChatWebHostAction.RefreshProjects when !_vm.Conversation.IsStreaming && _vm.ProjectSelection.IsAvailable && !_vm.ProjectSelection.IsLoading:
                await _vm.ProjectSelection.RefreshAsync(cancellationToken); break;
            case ChatWebHostAction.SelectProject when !_vm.Conversation.IsStreaming:
                var projects = _vm.ProjectSelection.Projects.Where(p => p.Id == request.TargetId).Take(2).ToArray();
                var project = projects.Length == 1 ? projects[0] : null;
                if (project is null || !_vm.SelectProject(project)) return new(false, ErrorCode: "CHAT_TARGET_UNAVAILABLE");
                break;
            case ChatWebHostAction.SelectModel when !_vm.ModelSelection.IsGenerating:
                if (!_vm.ModelSelection.TrySelect(request.TargetId ?? "")) return new(false, ErrorCode: "CHAT_TARGET_UNAVAILABLE");
                break;
            default: return new(false, ErrorCode: "CHAT_TARGET_UNAVAILABLE");
        }
        return new(true);
    }
    private async Task ReadAsync(ConversationTurnViewModel turn)
    {
        try { await _vm.ReadAloudAsync(turn); }
        catch (Exception) { /* Speech owner controls all presentation, including late completion. */ }
    }
    internal static string? Avatar(string? path)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !Path.IsPathFullyQualified(path) || path.StartsWith("\\\\", StringComparison.Ordinal)) return null;
            var file = new FileInfo(path);
            if (!file.Exists || file.Length is <= 0 or > 12_582_912) return null;
            using var input = file.OpenRead();
            var frame = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnDemand).Frames.FirstOrDefault();
            if (frame is null || frame.PixelWidth <= 0 || frame.PixelHeight <= 0 || (long)frame.PixelWidth * frame.PixelHeight > 8_388_608) return null;
            var scale = Math.Min(1d, Math.Min(128d / frame.PixelWidth, 128d / frame.PixelHeight));
            BitmapSource image = scale < 1 ? new TransformedBitmap(frame, new ScaleTransform(scale, scale)) : frame;
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            using var output = new MemoryStream(); encoder.Save(output);
            return output.Length <= 524_288 ? "data:image/png;base64," + Convert.ToBase64String(output.ToArray()) : null;
        }
        catch (Exception) { return null; }
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        _approvalDialog?.Close();
        _vm.PropertyChanged -= OnChanged; _vm.ModelSelection.PropertyChanged -= OnChanged;
        _vm.ProjectSelection.PropertyChanged -= OnChanged;
        if (_localWorkspace is not null) _localWorkspace.Changed -= OnChanged;
        if (_selectedImage is not null) _selectedImage.Changed -= OnChanged;
    }
}

/// <summary>Native-only user decision. Model text is inert and the default is always refusal.</summary>
internal sealed class NativeApprovalDialog : Window
{
    public ChatApprovalDecision Decision { get; private set; } = ChatApprovalDecision.Cancel;
    internal TextBox Details { get; }
    internal TextBlock Summary { get; }
    internal Expander DetailsSection { get; }
    internal Button AllowButton { get; }
    internal Button DenyButton { get; }
    internal Button CancelButton { get; }
    public NativeApprovalDialog(ChatWebApproval approval)
    {
        Title = "Review operation";
        Width = 620; Height = 560; MinWidth = 360; MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        var panel = new DockPanel { Margin = new Thickness(20) };
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        AllowButton = new Button { Content = "Allow once", Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(4) };
        DenyButton = new Button { Content = "Deny operation", IsDefault = true, Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(4) };
        CancelButton = new Button { Content = "Cancel task", IsCancel = true, Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(4) };
        AllowButton.Click += (_, _) => { Decision = ChatApprovalDecision.Allow; Close(); };
        DenyButton.Click += (_, _) => { Decision = ChatApprovalDecision.Deny; Close(); };
        CancelButton.Click += (_, _) => { Decision = ChatApprovalDecision.Cancel; Close(); };
        actions.Children.Add(AllowButton); actions.Children.Add(DenyButton); actions.Children.Add(CancelButton);
        DockPanel.SetDock(actions, Dock.Bottom); panel.Children.Add(actions);
        var body = new StackPanel();
        body.Children.Add(new TextBlock { Text = approval.ActionTitle ?? "Review proposed operation", FontSize = 21,
            FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14) });
        Summary = new TextBlock { Text = approval.PlainTextSummary ?? "A concise summary is unavailable. Read the complete frozen details below.",
            TextWrapping = TextWrapping.Wrap, FontSize = 14, Margin = new Thickness(0, 0, 0, 14) };
        body.Children.Add(Summary);
        body.Children.Add(new TextBlock { Text = $"Confirm before {approval.ExpiresAt.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        body.Children.Add(new TextBlock { Text = "Allow once applies only to this exact operation and version. Deny skips this operation; Cancel task ends the request. Closing this window cancels the task.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14) });
        Details = new TextBox { Text = approval.PlainTextDetails, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = 340, Margin = new Thickness(0, 8, 0, 0) };
        DetailsSection = new Expander { Header = "Full frozen details & arguments", Content = Details,
            IsExpanded = string.IsNullOrWhiteSpace(approval.PlainTextSummary) };
        body.Children.Add(DetailsSection);
        panel.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        Content = panel;
        Loaded += (_, _) => DenyButton.Focus();
    }
}
