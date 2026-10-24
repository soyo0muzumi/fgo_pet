using System.Text.Json;
using FgoPet.Dialogue.Contracts;

namespace FgoPet.App.Dialogue;

public sealed partial class ConversationViewModel
{
    private IChatWindowSelection? _windowSelection;

    public bool CanChooseWindowContext => !_disposed && !IsStreaming && !HasPendingRun
        && !string.IsNullOrWhiteSpace(ActiveServantId);

    // Only native composition binds this owner; no Web or model argument can supply a reader.
    public void BindWindowSelection(IChatWindowSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (_windowSelection is not null) throw new InvalidOperationException("WINDOW_SELECTION_ALREADY_BOUND");
        _windowSelection = selection;
        SessionChanged += ClearWindowSelection;
        RequestCancelled += CancelDesktopWindowSelection;
        DesktopMessagePersisted += ConsumeWindowSelection;
    }

    public string WindowContextScopeKey() => JsonSerializer.Serialize(new
    {
        Generation = _sessionGeneration, Role = ActiveServantId, Conversation = CurrentConversationId,
        Project = SessionContext.ProjectId, Route = _settings.Load().ModelConnection,
    });

    private void ClearWindowSelection() => _windowSelection?.Clear();
    // Request context was captured before persistence; this snapshot belongs to that
    // admitted user message even when later budget/provider preparation fails.
    private void ConsumeWindowSelection(string _) => ClearWindowSelection();
    private void CancelDesktopWindowSelection() { if (!IsExternalRequest) ClearWindowSelection(); }
}
