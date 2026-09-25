using System.Windows.Threading;

namespace FgoPet.App.Dialogue;

// The host constructs and disposes this desktop model on its owning UI thread.
// Hiding a window is not disposal: its live session and subscriptions are reused.
public sealed partial class DialogueWindowViewModel : IDisposable
{
    private readonly Dispatcher? _speechDispatcher;
    private volatile bool _speechDisposed;

    private void OnSpeechStateChanged(object? sender, EventArgs e)
    {
        if (_speechDisposed || _speech is null || _speechDispatcher is null) return;
        var turn = _activeSpeechTurn;
        if (turn is null) return;
        var requestId = Interlocked.Read(ref _speechRequestId);
        var state = _speech.State;

        void Apply()
        {
            // A posted notification can outlive a stop, a different message,
            // a session switch, or disposal. Never apply it to the next turn.
            if (_speechDisposed || _speechDispatcher.HasShutdownStarted ||
                requestId != _speechRequestId || !ReferenceEquals(turn, _activeSpeechTurn)) return;
            ApplySpeechState(turn, state);
        }

        if (_speechDispatcher.CheckAccess())
        {
            Apply();
            return;
        }

        if (_speechDispatcher.HasShutdownStarted || _speechDispatcher.HasShutdownFinished) return;
        try
        {
            _speechDispatcher.BeginInvoke(DispatcherPriority.DataBind, (Action)Apply);
        }
        catch (InvalidOperationException) when (_speechDispatcher.HasShutdownStarted || _speechDispatcher.HasShutdownFinished)
        {
            // Shutdown raced with posting; there is no live UI left to update.
        }
    }

    public void Dispose()
    {
        if (_speechDisposed) return;
        _speechDisposed = true;
        _windowActive = false;
        if (_speech is not null) _speech.StateChanged -= OnSpeechStateChanged;
        _conversation.Turns.CollectionChanged -= OnTurnsChanged;
        _conversation.AssistantReplyCompleted -= OnAssistantReplyCompleted;
        _conversation.SessionChanged -= OnSessionChanged;
        _conversation.PropertyChanged -= OnConversationPropertyChanged;
        if (_library is not null) _library.PropertyChanged -= OnLibraryPropertyChanged;
        ToolDrawer.ToolSelected -= OnToolSelected;
        ModelSelection.ModelSelected -= OnModelSelected;
        StopSpeechCore();
        // Do not dispose the shared playback service or the conversation here.
        // Their owning composition root may have other consumers (e.g. preview).
    }
}
