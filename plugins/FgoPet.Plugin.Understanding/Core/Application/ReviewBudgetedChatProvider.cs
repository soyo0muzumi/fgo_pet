using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FgoPet.App.Dialogue;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Settings;
using FgoPet.Understanding.Contracts;

namespace FgoPet.Understanding;

public sealed class ReviewBudgetedChatProvider(IChatProviderResolver resolver, ModelConnectionSettings connection,
    IRequestTokenMeter meter, ReviewBudgetUse budget, string purpose, UnderstandingMode mode, long generation,
    IChatDispatchGate? admission = null, Func<bool>? connectionCurrent = null,Action<UnderstandingDiagnostic>? diagnostics=null) : IChatProvider,IChatInputLimit
{
    public static string RouteHash(ModelRouteKey route) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(route))));
    public string ProviderId => connection.ProviderId;
    public string ModelId => connection.ModelId;
    public ReviewRequestLimits RequestLimits=>budget.RequestLimits??ReviewRequestLimits.Legacy;
    public int MaxInputTokens=>RequestLimits.MaxInputTokens;
    public Task<IReadOnlyList<ProviderModel>> ListModelsAsync(CancellationToken token) =>
        throw new InvalidOperationException("REVIEW_MODEL_LIST_UNAVAILABLE");
    public async IAsyncEnumerable<ChatStreamChunk> StreamAsync(ChatRequest request,[EnumeratorCancellation] CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var limits=RequestLimits;
        if(limits.MaxInputTokens<=0||limits.MaxOutputTokens<=0)
            throw new InvalidOperationException("UNDERSTANDING_REQUEST_LIMITS_INVALID");
        ReviewCallReservation? reservation = null;
        var route = ModelRouteKey.From(connection);
        var dispatch = new DispatchGate((candidate, actualRoute, start, cancellation) =>
        {
            cancellation.ThrowIfCancellationRequested();
            if (actualRoute != route || connectionCurrent?.Invoke() == false)
                throw new InvalidOperationException("UNDERSTANDING_MODEL_CHANGED");
            if (candidate.MaxOutputTokens is null or < 1 || candidate.MaxOutputTokens>limits.MaxOutputTokens)
                throw new InvalidOperationException("UNDERSTANDING_OUTPUT_LIMIT");
            var measurement = meter.Measure(route, candidate);
            if (measurement.InputTokens < 1 || measurement.InputTokens>limits.MaxInputTokens)
                throw new InvalidOperationException("UNDERSTANDING_INPUT_LIMIT");
            object? Handoff()
            {
                cancellation.ThrowIfCancellationRequested();
                if(connectionCurrent?.Invoke()==false)throw new InvalidOperationException("UNDERSTANDING_MODEL_CHANGED");
                reservation = budget.Budget.TryReserve(new(budget.Batch, budget.TrialId, budget.ScopeKey,
                    Guid.NewGuid().ToString("N"), RouteHash(route), purpose, mode, generation,
                    measurement.Fingerprint, measurement.InputTokens))
                    ?? throw new InvalidOperationException("UNDERSTANDING_BUDGET_LIMITED");
                cancellation.ThrowIfCancellationRequested();
                if(connectionCurrent?.Invoke()==false)throw new InvalidOperationException("UNDERSTANDING_MODEL_CHANGED");
                return start();
            }
            Log("review_dispatch_received");
            var started=admission is null ? Handoff() : admission.Dispatch(candidate, route, Handoff, cancellation);
            Log("review_dispatch_started");return started;
        });
        var provider = resolver.Resolve(connection, dispatch);
        var chunks = provider.StreamAsync(request, token).GetAsyncEnumerator(token);
        Exception? primary = null;
        ChatUsage? usage = null;
        var finished = false; var complete = false;
        try
        {
            while (true)
            {
                bool moved;
                try { token.ThrowIfCancellationRequested(); moved = await chunks.MoveNextAsync().ConfigureAwait(false); }
                catch (Exception error) { primary = error; throw; }
                if (!moved) break;
                if (reservation is null) throw new InvalidOperationException("REVIEW_TRANSPORT_GATE_NOT_ENTERED");
                var chunk = chunks.Current;
                finished |= chunk.FinishReason is "stop" or "tool_calls";
                complete |= chunk.IsComplete;
                if ((finished || chunk.IsComplete) && chunk.Usage is not null) usage = chunk.Usage;
                yield return chunk;
            }
        }
        finally
        {
            try { await chunks.DisposeAsync().ConfigureAwait(false); }
            catch (Exception) when (primary is not null) { /* The original failure remains authoritative. */ }
            finally
            {
                if (reservation is not null)
                {
                    try
                    {
                        budget.Budget.Complete(reservation.RequestHash,
                            primary is null && finished && complete && usage is not null
                                ? ReviewCallOutcome.Completed : ReviewCallOutcome.Unknown,
                            usage?.InputTokens, usage?.OutputTokens);
                        Log("review_dispatch_finished",primary is null&&finished&&complete?null:"UNDERSTANDING_DISPATCH_UNKNOWN");
                    }
                    catch (Exception) when (primary is not null) { /* Durable precharge survives a failed result update. */ }
                }
            }
        }
        void Log(string stage,string? code=null)
        {
            try{diagnostics?.Invoke(new(DateTimeOffset.UtcNow,(reservation?.RequestHash??RouteHash(route))[..16],stage,code));}
            catch(Exception){/* Diagnostic sinks cannot grant admission or hide the durable precharge. */}
        }
    }

    private sealed class DispatchGate(Func<ChatRequest, ModelRouteKey, Func<object?>, CancellationToken, object?> dispatch) : IChatDispatchGate
    {
        public T Dispatch<T>(ChatRequest request, ModelRouteKey route, Func<T> start, CancellationToken token) =>
            (T)dispatch(request, route, () => start(), token)!;
    }
}
