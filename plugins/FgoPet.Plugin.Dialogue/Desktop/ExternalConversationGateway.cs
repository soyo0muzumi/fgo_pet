using System.Windows.Threading;
using FgoPet.Dialogue.Contracts;

namespace FgoPet.App.Dialogue;

/// <summary>Marshals one additional input surface onto the existing desktop conversation owner.</summary>
internal sealed class ExternalConversationGateway(ConversationViewModel conversation, Dispatcher dispatcher)
    : IExternalConversationGateway
{
    public Task<ExternalConversationBinding?> CaptureCurrentBindingAsync(CancellationToken cancellationToken = default) =>
        InvokeAsync(() => Task.FromResult(conversation.CaptureExternalBinding()), cancellationToken);

    public Task<ExternalConversationResult> ExchangeTextAsync(ExternalConversationBinding binding, string text,
        CancellationToken cancellationToken = default) =>
        InvokeAsync(() => conversation.ExchangeExternalTextAsync(binding, text, cancellationToken), cancellationToken);

    public Task<ExternalConversationResult> SendAsync(ExternalConversationBinding binding, string text,
        CancellationToken cancellationToken = default) =>
        InvokeAsync(() => conversation.SendExternalAsync(binding, text, cancellationToken), cancellationToken);

    public Task<ExternalConversationResult> AnswerAsync(ExternalConversationBinding binding, ExternalInputReply reply,
        CancellationToken cancellationToken = default) =>
        InvokeAsync(() => conversation.AnswerExternalAsync(binding, reply, cancellationToken), cancellationToken);

    public Task<ExternalConversationResult> CancelAsync(ExternalConversationBinding binding, ExternalInputReference reference,
        CancellationToken cancellationToken = default) =>
        InvokeAsync(() => conversation.CancelExternalAsync(binding, reference, cancellationToken), cancellationToken);

    private Task<T> InvokeAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
            return Task.FromCanceled<T>(new CancellationToken(canceled: true));
        return dispatcher.CheckAccess() ? action() : dispatcher.InvokeAsync(action, DispatcherPriority.Normal,
            cancellationToken).Task.Unwrap();
    }
}
