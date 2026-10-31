using FgoPet.Core.Dialogue;
using FgoPet.Extensibility;
using FgoPet.Understanding.Contracts;
using System.Security.Cryptography;
using System.Text;

namespace FgoPet.Understanding;

/// <summary>One topic, one search and at most one short-lived material; never a World writer.</summary>
public sealed class MaterialResearchActivity(IMaterialResearchHost host, IUnderstandingReader understanding,
    ITopicMaterialStore store, IMaterialResearchModel model, IMaterialResearchSource source,
    IDialogueContextLifetime lifetime, TimeProvider clock, Action<MaterialResearchDiagnostic>? diagnostics = null)
{
    private readonly SemaphoreSlim _serial = new(1, 1);
    private static readonly TimeSpan RefreshAfter = TimeSpan.FromHours(6);
    public string? LastCode { get; private set; }

    public async Task RunOnceAsync(CancellationToken token = default)
    {
        if (!await _serial.WaitAsync(0, token).ConfigureAwait(false)) return;
        var activityId = Guid.NewGuid().ToString("N");
        var correlation = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(activityId)))[..16];
        try
        {
            Log("received");
            var configuration = host.Capture();
            if (configuration is null) return;
            var scope = new ConversationScope(configuration.Scope.RoleId, configuration.Scope.ProjectId);
            var projection = understanding.Query(scope, "");
            if (projection.Code is not null) return;
            var topic = projection.Items.Where(Eligible).OrderBy(e => e.Kind == UnderstandingKind.OpenQuestion ? 0 : 1)
                .ThenByDescending(e => e.ObservedAt).ThenBy(e => e.Id, StringComparer.Ordinal)
                .FirstOrDefault(e => store.CanResearch(scope, e.Id, clock.GetUtcNow(), RefreshAfter, configuration.DailyLimit));
            if (topic is null) return;
            Log("queued");
            using var lease = lifetime.TryAcquireBackground(token);
            if (lease is null) return;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90), clock);
            using var work = CancellationTokenSource.CreateLinkedTokenSource(token, lease.Token, timeout.Token);
            var cancellation = work.Token;
            var request = new MaterialResearchRequest(activityId, configuration.Scope, configuration.Connection,
                topic.Id, topic.Revision, topic.Correction ?? topic.Content,
                topic.Kind == UnderstandingKind.OpenQuestion ? TopicMaterialScope.OpenQuestion : TopicMaterialScope.Interest);
            var phase = 1;
            var calls = 0;
            var charged = false;
            var gate = new DispatchGate((chat, route, start, dispatchToken) =>
            {
                EnsureCurrent(dispatchToken);
                if (route != ModelRouteKey.From(configuration.Connection) || chat.Tools is not null
                    || chat.ServantId != configuration.Scope.RoleId || chat.ConversationId != configuration.Scope.ConversationId
                    || chat.MaxOutputTokens is not > 0 or > 384 || calls + 1 != phase
                    || !chat.Metadata.TryGetValue("fgo_auxiliary", out var purpose) || purpose != "topic_material_research"
                    || !chat.Metadata.TryGetValue("fgo_activity_id", out var id) || id != activityId)
                    throw new InvalidOperationException("MATERIAL_DISPATCH_REJECTED");
                object? result = null;
                var accepted = lease.Commit(() => host.TryCommit(configuration, () =>
                {
                    EnsureTopicCurrent(); dispatchToken.ThrowIfCancellationRequested();
                    if (calls + 1 != phase) throw new InvalidOperationException("MATERIAL_DISPATCH_REJECTED");
                    if (!charged)
                    {
                        if (!store.TryReserveResearch(scope, topic.Id, clock.GetUtcNow(), RefreshAfter, configuration.DailyLimit))
                            throw new InvalidOperationException("MATERIAL_BUDGET_UNAVAILABLE");
                        charged = true;
                    }
                    calls++;
                    Log("model_dispatch");
                    dispatchToken.ThrowIfCancellationRequested();
                    result = start();
                }));
                if (!accepted) throw new OperationCanceledException(cancellation);
                return result;
            });
            EnsureCurrent(cancellation); Log("started");
            var query = await model.GenerateQueryAsync(request, gate, EnsureCurrent, cancellation).ConfigureAwait(false);
            EnsureCurrent(cancellation); Log("query_result");
            if (!charged || calls != 1) throw new InvalidOperationException("MATERIAL_DISPATCH_MISSING");
            if (query is null) { LastCode = "MATERIAL_NO_QUERY"; return; }
            if (string.IsNullOrWhiteSpace(query) || query.Length > 256 || query.Any(char.IsControl))
                throw new InvalidOperationException("MATERIAL_QUERY_INVALID");
            Log("search_dispatch");
            var results = (await source.SearchAsync(configuration.Scope, query, cancellation, activityId).ConfigureAwait(false)).Take(3).ToArray();
            EnsureCurrent(cancellation); Log("search_result");
            if (results.Length == 0) { LastCode = "MATERIAL_NO_RESULTS"; return; }
            phase = 2;
            var draft = await model.SummarizeAsync(request, results, gate, EnsureCurrent, cancellation).ConfigureAwait(false);
            EnsureCurrent(cancellation); Log("summary_result");
            if (calls != 2) throw new InvalidOperationException("MATERIAL_DISPATCH_MISSING");
            if (draft is null) { LastCode = "MATERIAL_NO_CHANGE"; return; }
            if (draft.SourceIndex < 0 || draft.SourceIndex >= results.Length)
                throw new InvalidOperationException("MATERIAL_SOURCE_INVALID");
            var selected = results[draft.SourceIndex];
            var now = clock.GetUtcNow();
            var material = new TopicMaterial(Guid.NewGuid().ToString("N"), topic.Id, topic.Revision, request.Kind,
                draft.Summary, draft.RelevanceReason, selected.Title, selected.Url, now, now.AddDays(2));
            if (!lease.Commit(() => host.TryCommit(configuration, () =>
            {
                EnsureTopicCurrent(); cancellation.ThrowIfCancellationRequested(); store.Add(scope, material);
            }))) throw new OperationCanceledException(cancellation);
            LastCode = null; Log("saved");

            bool Eligible(UnderstandingEntry entry) => entry.Scope == scope && entry.Life == UnderstandingLife.Active
                && entry.ActiveUntil > clock.GetUtcNow() && entry.Review != UnderstandingReview.Rejected
                && entry.Effect != UnderstandingEffect.Pending
                && entry.Kind is UnderstandingKind.CurrentInterest or UnderstandingKind.OpenQuestion;
            void EnsureTopicCurrent()
            {
                var current = understanding.Query(scope, "");
                if (current.Code is not null || current.Revision != projection.Revision
                    || !current.Items.Any(e => Eligible(e) && e.Id == topic.Id && e.Revision == topic.Revision
                        && e.Content == topic.Content && e.Correction == topic.Correction))
                    throw new OperationCanceledException("MATERIAL_TOPIC_CHANGED", cancellation);
            }
            void EnsureCurrent(CancellationToken currentToken)
            {
                currentToken.ThrowIfCancellationRequested(); cancellation.ThrowIfCancellationRequested(); lease.CheckCurrent();
                if (!host.TryCommit(configuration, EnsureTopicCurrent))
                    throw new OperationCanceledException("MATERIAL_AUTHORIZATION_CHANGED", cancellation);
            }
        }
        catch (OperationCanceledException) { LastCode = "MATERIAL_CANCELLED"; Log("cancelled", LastCode); }
        catch (Exception error)
        {
            // Never log query, snippet, provider body, user data or exception text.
            LastCode = error is InvalidOperationException && SafeCode(error.Message) ? error.Message : "MATERIAL_UNAVAILABLE";
            Log("failed", LastCode);
        }
        finally { _serial.Release(); }

        void Log(string stage, string? code = null)
        {
            try { diagnostics?.Invoke(new(clock.GetUtcNow(), correlation, stage, code)); }
            catch { /* Diagnostics cannot authorize or interrupt work. */ }
        }
    }
    private static bool SafeCode(string code) => code.StartsWith("MATERIAL_", StringComparison.Ordinal) && code.Length <= 96
        && code.All(c => char.IsAsciiLetterUpper(c) || c == '_' || char.IsAsciiDigit(c));
    private sealed class DispatchGate(Func<ChatRequest, ModelRouteKey, Func<object?>, CancellationToken, object?> dispatch) : IChatDispatchGate
    {
        public T Dispatch<T>(ChatRequest chat, ModelRouteKey route, Func<T> start, CancellationToken token)
            => (T)dispatch(chat, route, () => start(), token)!;
    }
}
