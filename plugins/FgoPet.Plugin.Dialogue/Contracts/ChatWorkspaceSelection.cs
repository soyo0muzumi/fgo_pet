namespace FgoPet.Dialogue.Contracts;

/// <summary>Conversation-owned local authority, chosen through a trusted native picker.</summary>
public interface IChatWorkspaceSelection
{
    bool CanChoose { get; }
    string? Label { get; }
    event EventHandler? Changed;
    bool Choose(Func<string?> nativePicker);
    void Clear();
}
