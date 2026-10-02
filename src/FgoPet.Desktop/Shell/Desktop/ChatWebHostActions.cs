using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FgoPet.App.Settings;
using FgoPet.Core.Panels;
using FgoPet.Dialogue.Contracts;
using FgoPet.UiSdk;

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
    private string? _avatarPath, _avatar;
    private bool _disposed;
    public event EventHandler? Changed;
    public ChatWebHostActions(DialogueWindowViewModel vm, DialogueWindow window, IClipboardWriter clipboard,
        IAttachedPanelLauncher? panel, IWorkspaceCatalog? catalog, Func<string, WorkspaceNavigation, bool>? workspace)
    {
        _vm = vm; _window = window; _clipboard = clipboard; _panel = panel; _workspace = workspace;
        _defaultWorkspace = catalog?.Workspaces.FirstOrDefault()?.Id;
        vm.PropertyChanged += OnChanged;
        vm.ModelSelection.PropertyChanged += OnChanged;
        vm.ProjectSelection.PropertyChanged += OnChanged;
    }
    private void OnChanged(object? sender, PropertyChangedEventArgs args) => Changed?.Invoke(this, EventArgs.Empty);
    public ChatWebHostPresentation ReadPresentation()
    {
        if (_avatarPath != _vm.SelectedServant?.PreviewSource)
        { _avatarPath = _vm.SelectedServant?.PreviewSource; _avatar = Avatar(_avatarPath); }
        var streaming = _vm.Conversation.IsStreaming;
        return new(_vm.ActiveServantDisplayName, _avatar,
            _vm.ProjectSelection.Projects.Select(p => new ChatWebChoice(p.Id, p.Label,
                p.Id == _vm.Conversation.SessionContext.ProjectId, !streaming)).ToArray(),
            _vm.ModelSelection.Models.Select(m => new ChatWebChoice(m.Id, m.DisplayName,
                m.Id == _vm.ModelSelection.SelectedModelId, !_vm.ModelSelection.IsGenerating)).ToArray(),
            _vm.ProjectSelection.IsLoading, _vm.ProjectSelection.StatusText,
            new(_panel is not null, _workspace is not null && _defaultWorkspace is not null,
                _workspace is not null && _defaultWorkspace is not null, true, true, _vm.Conversation.CanOpenModelSettings,
                _vm.ProjectSelection.IsAvailable && !streaming));
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
        _vm.PropertyChanged -= OnChanged; _vm.ModelSelection.PropertyChanged -= OnChanged;
        _vm.ProjectSelection.PropertyChanged -= OnChanged;
    }
}
