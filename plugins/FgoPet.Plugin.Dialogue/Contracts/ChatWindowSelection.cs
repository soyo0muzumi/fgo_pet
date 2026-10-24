using FgoPet.Core.Dialogue;
using FgoPet.Platform.WindowText;

namespace FgoPet.Dialogue.Contracts;

/// <summary>Native local preview and explicit one-message attachment. Web sees metadata only.</summary>
public interface IChatWindowSelection
{
    bool CanChoose { get; }
    bool IsReading { get; }
    WindowTextSnapshot? Current { get; }
    WindowTextSnapshot? Preview { get; }
    SelectedWindowContent? CurrentContent { get; }
    event EventHandler? Changed;
    IReadOnlyList<WindowTextCandidate> ListCandidates();
    Task<WindowTextSnapshot?> PreviewAsync(string candidateToken, CancellationToken cancellationToken);
    Task<WindowTextSnapshot?> RereadAsync(CancellationToken cancellationToken);
    bool AttachPreview();
    void CancelPreview();
    void Clear();
}
