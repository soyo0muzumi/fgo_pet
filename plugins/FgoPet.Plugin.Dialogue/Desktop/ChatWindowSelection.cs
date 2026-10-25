using FgoPet.Core.Dialogue;
using FgoPet.Dialogue.Contracts;
using FgoPet.Platform.WindowText;

namespace FgoPet.App.Dialogue;

/// <summary>UI-thread owner. Parent clears on session, role, route, cancellation, and admitted send.</summary>
public sealed class ChatWindowSelection(IWindowTextReader reader, Func<string> scopeKey, Func<bool>? canChoose = null)
    : IChatWindowSelection, IDisposable
{
    private WindowTextSnapshot? current, preview;
    private string? currentToken, previewToken, selectedScope;
    private CancellationTokenSource? pending;
    private long epoch;
    private bool disposed;
    public bool IsReading { get; private set; }
    public bool CanChoose => !disposed && !IsReading && (canChoose?.Invoke() ?? true);
    public WindowTextSnapshot? Current { get { EnsureScope(); return current; } }
    public WindowTextSnapshot? Preview { get { EnsureScope(); return preview; } }
    public SelectedWindowContent? CurrentContent => Current is { CanAttach: true } value
        ? new(value.SourceTitle, value.Application, value.Text, value.ReadAtUtc, value.Status == WindowTextReadStatus.Partial) : null;
    public event EventHandler? Changed;

    public IReadOnlyList<WindowTextCandidate> ListCandidates() => CanChoose ? reader.ListCandidates() : [];

    public async Task<WindowTextSnapshot?> PreviewAsync(string candidateToken, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!CanChoose) return null;
        EnsureScope();
        CancelPreview();
        var capturedEpoch = epoch;
        var capturedScope = scopeKey();
        selectedScope = capturedScope;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        pending = operation;
        IsReading = true;
        Changed?.Invoke(this, EventArgs.Empty);
        try
        {
            var result = await reader.ReadAsync(candidateToken, operation.Token);
            if (disposed || operation.IsCancellationRequested || capturedEpoch != epoch || capturedScope != scopeKey()
                || !(canChoose?.Invoke() ?? true)) return null;
            preview = result;
            previewToken = candidateToken;
            return result;
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested) { return null; }
        finally
        {
            if (ReferenceEquals(pending, operation))
            {
                pending = null;
                IsReading = false;
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public Task<WindowTextSnapshot?> RereadAsync(CancellationToken cancellationToken)
    {
        EnsureScope();
        var token = currentToken ?? previewToken;
        if (token is null || !CanChoose) return Task.FromResult<WindowTextSnapshot?>(null);
        Clear(); // A failed reread must not leave a stale attachment available to send.
        return PreviewAsync(token, cancellationToken);
    }

    public bool AttachPreview()
    {
        EnsureScope();
        if (!CanChoose || preview?.CanAttach != true) return false;
        current = preview;
        currentToken = previewToken;
        preview = null;
        previewToken = null;
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void CancelPreview()
    {
        epoch++;
        pending?.Cancel();
        pending = null;
        IsReading = false;
        preview = null;
        previewToken = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        current = null;
        currentToken = null;
        selectedScope = null;
        CancelPreview();
    }

    private void EnsureScope()
    {
        if (selectedScope is not null && selectedScope != scopeKey()) Clear();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Clear();
        if (reader is IDisposable disposable) disposable.Dispose();
    }
}
