using System.ComponentModel;
using System.IO;
using FgoPet.Dialogue.Contracts;
using FgoPet.Extensibility;

namespace FgoPet.App.Dialogue;

/// <summary>In-memory grants never transfer to another role, project, history item or new conversation.</summary>
internal sealed class ChatWorkspaceSelection : IChatWorkspaceSelection, IDisposable
{
    private readonly ConversationViewModel _conversation;
    private readonly IWorkspaceAuthorizationManager? _authority;
    private string? _root;
    private ToolScope? _grant;
    private long _epoch;
    private bool _disposed;
    public ChatWorkspaceSelection(ConversationViewModel conversation, IWorkspaceAuthorizationManager? authority = null)
    {
        _conversation = conversation;
        _authority = authority;
        conversation.PropertyChanged += OnChanged;
        conversation.SessionChanged += OnSessionChanged;
    }
    public bool CanChoose => !_disposed && _authority is not null && !_conversation.IsStreaming
        && !string.IsNullOrWhiteSpace(_conversation.ActiveServantId);
    public string? Label => _root is null ? null : Path.GetFileName(Path.TrimEndingDirectorySeparator(_root)) is { Length: > 0 } name ? name : _root;
    public event EventHandler? Changed;
    public bool Choose(Func<string?> nativePicker)
    {
        if (!CanChoose) return false;
        var epoch = _epoch;
        var identity = Scope();
        var path = nativePicker();
        if (path is null) return true;
        if (!CanChoose || epoch != _epoch || identity != Scope()) return false;
        try
        {
            var canonical = _authority!.ValidateDirectory(path);
            if (!CanChoose || epoch != _epoch || identity != Scope()) return false;
            // Empty Chat has no persisted conversation yet. Bind synchronously at its first
            // UserMessagePersisted update, before the orchestrator dispatches any native tools.
            if (!string.IsNullOrWhiteSpace(identity.ConversationId))
            {
                _authority.GrantValidated(identity, canonical);
                _grant = identity;
            }
            _root = canonical;
            Changed?.Invoke(this, EventArgs.Empty);
            return true;
        }
        catch (WorkspaceAccessException) { return false; }
    }
    private ToolScope Scope() => new(_conversation.CurrentConversationId,
        _conversation.ActiveServantId, string.IsNullOrWhiteSpace(_conversation.SessionContext.ProjectId)
            ? null : _conversation.SessionContext.ProjectId);
    private void OnChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (_disposed) return;
        if (_root is not null && _grant is null && !string.IsNullOrWhiteSpace(_conversation.CurrentConversationId))
        {
            try { _grant = _authority!.GrantValidated(Scope(), _root).Scope; }
            catch (WorkspaceAccessException) { _root = null; }
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }
    private void OnSessionChanged() { _epoch++; Clear(); }
    public void Clear()
    {
        if (_grant is not null) _authority?.Revoke(_grant);
        _grant = null;
        _root = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _conversation.PropertyChanged -= OnChanged;
        _conversation.SessionChanged -= OnSessionChanged;
        Clear();
    }
}
