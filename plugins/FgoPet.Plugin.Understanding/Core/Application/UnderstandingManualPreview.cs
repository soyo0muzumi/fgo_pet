using FgoPet.Core.Dialogue;
using FgoPet.Extensibility;
using FgoPet.Understanding.Contracts;

namespace FgoPet.Understanding;

public sealed partial class UnderstandingService
{
    /// <summary>One explicit manual operation over a host-supplied producer. M1 wires no real producer.
    /// The future adapter must use the existing Runtime/provider/metering owner, never a fake user turn.</summary>
    public async Task<UnderstandingReceipt> PreviewAsync(ConversationScope scope, string operationId,
        IReadOnlyList<ConversationSourceSnapshot> roots, Func<CancellationToken, Task<IReadOnlyList<UnderstandingProposal>>> producer,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(producer); token.ThrowIfCancellationRequested();
        lock (_gate)
        {
            EnsureSchema(); using var db = database.Open();
            if (ReadReceipt(db, null, scope, operationId) is { } receipt) return receipt;
        }
        var ticket = Begin(scope, operationId, roots, manual: true, token);
        if (!TryReserveAttempt(scope, operationId, roots, manual: true))
            return new(operationId, [new("read-set", UnderstandingOutcome.NoChange, Code: "UNDERSTANDING_ATTEMPT_NOT_ADMITTED")]);
        try
        {
            token.ThrowIfCancellationRequested();
            Log(operationId, "manual_producer_started");
            var proposals = await producer(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var receipt = Apply(ticket, proposals, token);
            CompleteAttempt(scope, operationId, receipt.Items.Any(i => i.Outcome == UnderstandingOutcome.Failed) ? UnderstandingOutcome.Failed :
                receipt.Items.Any(i => i.Outcome == UnderstandingOutcome.ProposedPending) ? UnderstandingOutcome.ProposedPending :
                receipt.Items.Any(i => i.Outcome == UnderstandingOutcome.ReviewedNotPersisted) ? UnderstandingOutcome.ReviewedNotPersisted :
                proposals.Count == 0 ? UnderstandingOutcome.NoChange : UnderstandingOutcome.Applied);
            return receipt;
        }
        catch (OperationCanceledException)
        { CompleteAttempt(scope, operationId, UnderstandingOutcome.Failed); Log(operationId, "manual_cancelled", "UNDERSTANDING_CANCELLED"); throw; }
        catch (Exception)
        {
            CompleteAttempt(scope, operationId, UnderstandingOutcome.Failed); Log(operationId, "manual_failed", "UNDERSTANDING_PREVIEW_FAILED");
            return new(operationId, [new("read-set", UnderstandingOutcome.Failed, Code: "UNDERSTANDING_PREVIEW_FAILED")]);
        }
    }
}
