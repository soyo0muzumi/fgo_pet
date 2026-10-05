using System.Windows.Threading;
using FgoPet.App.Dialogue;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Portraits;
using FgoPet.Extensibility;
using FgoPet.Kernel.Companion;
using FgoPet.Kernel.Lifecycle;

namespace FgoPet.App.Portraits.Live2D;

/// <summary>Projects existing role and conversation events into the renderer's private state.</summary>
public sealed class Live2DPresentationPlugin(
    CompanionPresentation presentation, Func<ConversationOrchestrator> resolveConversations,
    Live2DPortraitController portrait) : IFgoPetPlugin, IDisposable
{
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private CancellationToken _stopping;
    private bool _started;
    private bool _disposed;
    private string? _conversation;
    private ConversationOrchestrator? _conversations;
    public PluginManifest Manifest { get; } = new("firstparty.portrait-live2d", "1.0.0", 1, []);
    public PluginContributions Contributions => PluginContributions.Empty;

    public ValueTask StartAsync(CancellationToken stoppingToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started) return ValueTask.CompletedTask;
        _conversations = resolveConversations();
        _started = true;
        _stopping = stoppingToken;
        presentation.Changed += OnPresentation;
        _conversations.Updated += OnConversation;
        OnPresentation(presentation.Current);
        return ValueTask.CompletedTask;
    }

    private void OnPresentation(CompanionPresentationState? state)
    {
        if (state is null) { _conversation = null; return; }
        Dispatch(() =>
        {
            if (presentation.IsCurrent(state) && portrait.MatchesRole(state.Role)) portrait.SetExpression(state.Expression);
        });
    }

    private void OnConversation(ConversationUpdate update)
    {
        var role = presentation.ActiveRole;
        if (role is null || update.ServantId != role.ServantId) return;
        Dispatch(() =>
        {
            if (!ReferenceEquals(role, presentation.ActiveRole) || !portrait.MatchesRole(role)) return;
            if (update.Type == ConversationUpdateType.UserMessagePersisted)
            {
                _conversation = update.ConversationId;
                portrait.SetExpression(ExpressionSemantic.Neutral);
                portrait.SetThinking(true);
            }
            else if (update.ConversationId == _conversation)
            {
                var finished = update.Type is ConversationUpdateType.AssistantCompleted
                    or ConversationUpdateType.Cancelled or ConversationUpdateType.Failed;
                var answering = update.Type == ConversationUpdateType.AssistantDelta
                    || update.RequestStage == ConversationRequestStage.StreamingAnswer;
                if (finished || answering) portrait.SetThinking(false);
                if (update.Type == ConversationUpdateType.AssistantCompleted && update.Expression is { } expression)
                    portrait.SetExpression(expression);
                if (finished) _conversation = null;
            }
        });
    }

    private void Dispatch(Action action)
    {
        if (_disposed || _stopping.IsCancellationRequested || _dispatcher.HasShutdownStarted) return;
        _dispatcher.BeginInvoke(() =>
        {
            if (!_disposed && !_stopping.IsCancellationRequested) action();
        });
    }

    public ValueTask StopAsync(CancellationToken cancellationToken) { Dispose(); return ValueTask.CompletedTask; }
    public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        presentation.Changed -= OnPresentation;
        if (_conversations is not null) _conversations.Updated -= OnConversation;
        _conversation = null;
    }
}
