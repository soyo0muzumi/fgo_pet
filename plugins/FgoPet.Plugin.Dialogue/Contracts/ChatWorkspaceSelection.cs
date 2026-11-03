namespace FgoPet.Dialogue.Contracts;

/// <summary>One trusted native folder choice sets the project identity and conversation-owned local authority.
/// Removing the choice clears both; restoring history never restores directory authority.</summary>
public interface IChatWorkspaceSelection
{
    bool CanChoose { get; }
    string? Label { get; }
    event EventHandler? Changed;
    bool Choose(Func<string?> nativePicker);
    void Clear();
}
