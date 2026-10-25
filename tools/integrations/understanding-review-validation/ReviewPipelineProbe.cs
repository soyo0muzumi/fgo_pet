// Non-production preparation probe. Compiled only into the offline App test project.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FgoPet.Core.Dialogue;
using FgoPet.Extensibility;
using FgoPet.Kernel.Agent;
using FgoPet.Understanding;
using FgoPet.Understanding.Contracts;

namespace FgoPet.Validation.Understanding;

internal sealed record ReviewProbeSelection(ConversationSourceSnapshot Source, HistoryAnchor Anchor);
internal sealed record ReviewProbeMaterial(ConversationSourceSnapshot Source, string Text);
internal sealed record ReviewProbeTargetSelection(ConversationScope Scope, string Id, int Revision);
internal sealed record ReviewReadProof(UnderstandingSourceCoverage Coverage, string TextHash);
internal sealed record ReviewProbeRequest(IReadOnlyList<ReviewProbeMaterial> Material,
    Action<CancellationToken> EnsureCurrent)
{
    public string OperationHash { get; init; } = "";
    public IReadOnlyList<UnderstandingEntry> Targets { get; init; } = [];
    public IReadOnlyList<ReviewReadProof> ReadProof { get; init; } = [];
}
internal sealed record ReviewCommitEvidence(IReadOnlyList<ConversationSourceSnapshot> Roots,
    IReadOnlyList<UnderstandingSourceCoverage> Coverage, IReadOnlyList<UnderstandingEntry> Targets);
internal static class ReviewCommitContractProbe
{
    internal static ReviewReadProof SealRead(ReviewProbeMaterial material)
    {
        var count = material.Text.EnumerateRunes().Count();
        return new(new(material.Source, 0, count, count), TextHash(material.Text));
    }
    internal static void ValidateReadProof(ReviewProbeRequest request)
    {
        if (request.Material.Count is < 1 or > 8 || request.ReadProof.Count != request.Material.Count ||
            request.Material.Where((m, i) => m is null || m.Text is null || SealRead(m) != request.ReadProof[i] ||
                request.ReadProof[i].Coverage.TotalScalars < 1).Any()) throw Invalid();
    }
    public static ReviewCommitEvidence Validate(ReviewProbeRequest request, ReviewExchangeResult exchange)
    {
        ValidateReadProof(request);
        if (request.OperationHash.Length != 64 || request.OperationHash.Any(c => !char.IsAsciiHexDigit(c)) ||
            exchange.RequestHash != ReviewModelExchangeProbe.RequestBinding(request))
            throw new AgentProtocolException("REVIEW_PROBE_REQUEST_MISMATCH");
        var roots = request.Material.Select(m => m.Source).ToArray();
        var coverage = request.ReadProof.Select(p => p.Coverage).ToArray();
        if (!coverage.SequenceEqual(exchange.SuppliedCoverage) || exchange.Proposals.Any(p => p.Sources.Any(s => !roots.Contains(s))))
            throw Invalid();
        if (exchange.Proposals.Any(p => (p.TargetId is null) != (p.TargetRevision is null) ||
            p.TargetId is not null && !request.Targets.Any(t => t.Id == p.TargetId && t.Revision == p.TargetRevision)))
            throw new AgentProtocolException("REVIEW_PROBE_TARGET_CHANGED");
        // This is a draft for the owner's coverage-aware Begin port, never an alternate commit path.
        return new(Array.AsReadOnly(roots), Array.AsReadOnly(coverage), request.Targets);
    }
    private static AgentProtocolException Invalid() => new("REVIEW_PROBE_COVERAGE_INVALID");
    private static string TextHash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}

internal sealed class ReviewPipelineProbe(UnderstandingService owner, IExperienceSourceReader originals,
    IConversationSourceReader versions, Action<UnderstandingDiagnostic>? diagnostics = null)
{
    public Task<UnderstandingReceipt> RunManualAsync(ConversationScope scope, string operationId,
        IReadOnlyList<ReviewProbeSelection> selection,
        Func<ReviewProbeRequest, CancellationToken, Task<IReadOnlyList<UnderstandingProposal>>> substitute,
        CancellationToken token = default, IReadOnlyList<ReviewProbeTargetSelection>? targets = null)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(substitute);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        if (token.IsCancellationRequested)
            return Task.FromCanceled<UnderstandingReceipt>(token);
        var frozen = selection.ToArray();
        var selectedTargets = targets?.ToArray() ?? [];
        var roots = frozen.Select(item => item.Source).ToArray();
        var admitted = owner.State(scope);
        Log(operationId, "probe_received");

        // The owner checks an existing receipt before fresh validation. Body reads and the
        // substitute run only inside its admitted, single-attempt manual producer.
        return owner.PreviewAsync(scope, operationId, roots, async cancellation =>
        {
            CheckPolicy();
            if (frozen.Length is < 1 or > 8)
                throw Refuse("REVIEW_PROBE_INVALID_READ_SET");
            if (selectedTargets.Length > 8 || selectedTargets.Any(t => t is null) ||
                selectedTargets.Select(t => t.Id).Distinct(StringComparer.Ordinal).Count() != selectedTargets.Length)
                throw Refuse("REVIEW_PROBE_TARGET_UNAVAILABLE");
            var boundTargets = new List<UnderstandingEntry>();
            if (selectedTargets.Length > 0)
            {
                Log(operationId, "target_read_started");
                foreach (var target in selectedTargets)
                {
                    cancellation.ThrowIfCancellationRequested(); CheckPolicy();
                    var entry = ReadTarget(target);
                    boundTargets.Add(entry with { Sources = Array.AsReadOnly(entry.Sources.ToArray()),
                        Coverage = entry.Coverage is null ? null : Array.AsReadOnly(entry.Coverage.ToArray()) });
                }
                Log(operationId, "target_read_completed");
            }
            var material = new List<ReviewProbeMaterial>();
            var scalars = 0;
            Log(operationId, "material_read_started");
            foreach (var item in frozen)
            {
                cancellation.ThrowIfCancellationRequested();
                CheckPolicy();
                if (item.Source.Scope.RoleId != scope.ServantId || item.Source.Scope.ProjectId != scope.ProjectId ||
                    item.Source.Scope.ConversationId != item.Anchor.ConversationId || item.Source.MessageId != item.Anchor.MessageId ||
                    item.Source.OccurredAtUtc != item.Anchor.CreatedAtUtc || !versions.IsCurrent(item.Source))
                    throw Refuse("REVIEW_PROBE_SOURCE_CHANGED");
                var read = originals.Read(scope, item.Anchor, cancellation);
                if (read.Status != ExperienceSourceStatus.Available || read.Source is null)
                    throw Refuse("REVIEW_PROBE_SOURCE_CHANGED");
                var message = read.Source;
                if (message.Scope != scope || message.Anchor != item.Anchor || message.Kind != item.Source.Kind ||
                    message.SourceVersion != item.Source.Fingerprint)
                    throw Refuse("REVIEW_PROBE_SOURCE_CHANGED");
                scalars += message.Text.EnumerateRunes().Count();
                if (scalars > 6000) throw Refuse("REVIEW_PROBE_MATERIAL_LIMIT");
                material.Add(new(item.Source, message.Text));
            }
            Log(operationId, "material_read_completed");
            var request = new ReviewProbeRequest(Array.AsReadOnly(material.ToArray()), CheckDispatch)
            {
                OperationHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(operationId))),
                Targets = Array.AsReadOnly(boundTargets.ToArray()),
                ReadProof = Array.AsReadOnly(material.Select(ReviewCommitContractProbe.SealRead).ToArray())
            };
            Log(operationId, "substitute_started");
            // Diagnostic callbacks can change access, cancellation or sources. The last
            // callback must precede this gate; no callback or await may follow it before dispatch.
            request.EnsureCurrent(cancellation);
            var proposals = await substitute(request, cancellation).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            Log(operationId, "substitute_completed");
            return proposals;

            void CheckDispatch(CancellationToken dispatchToken)
            {
                dispatchToken.ThrowIfCancellationRequested();
                CheckPolicy();
                if (boundTargets.Count > 0)
                {
                    for (var i = 0; i < boundTargets.Count; i++)
                        if (JsonSerializer.Serialize(ReadTarget(selectedTargets[i])) != JsonSerializer.Serialize(boundTargets[i]))
                            throw Refuse("REVIEW_PROBE_TARGET_CHANGED");
                    // Query's source diagnostics may execute callbacks. Recheck after them.
                    CheckPolicy();
                }
                if (roots.Any(source => !versions.IsCurrent(source)))
                    throw Refuse("REVIEW_PROBE_SOURCE_CHANGED");
                dispatchToken.ThrowIfCancellationRequested();
            }
            UnderstandingEntry ReadTarget(ReviewProbeTargetSelection target)
            {
                if (target.Scope != scope || target.Revision < 1 || string.IsNullOrWhiteSpace(target.Id) ||
                    target.Id.Length > 128 || target.Id.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
                    throw Refuse("REVIEW_PROBE_TARGET_UNAVAILABLE");
                // The ordinary owner projection excludes pending, rejected, retracted and unauthorized records.
                // Historical lookup is explicit because the host selected this exact target.
                var projection = owner.Query(scope, "", historical: true, entryId: target.Id);
                if (projection.Code is not null || projection.Items.Count != 1)
                    throw Refuse("REVIEW_PROBE_TARGET_UNAVAILABLE");
                var current = projection.Items[0];
                if (current.Scope != scope || current.Id != target.Id || current.Revision != target.Revision)
                    throw Refuse("REVIEW_PROBE_TARGET_CHANGED");
                return current;
            }
            void CheckPolicy()
            {
                var current = owner.State(scope);
                if (!current.Policy.Enabled || !current.Policy.ReadSources || !current.Policy.ExternalProcessing ||
                    current.Generation != admitted.Generation || current.Revision != admitted.Revision)
                    throw Refuse("REVIEW_PROBE_NOT_AUTHORIZED");
            }
            InvalidOperationException Refuse(string code)
            {
                Log(operationId, "probe_refused", code);
                return new(code);
            }
        }, token);
    }

    private void Log(string operationId, string stage, string? code = null)
    {
        try
        {
            diagnostics?.Invoke(new(DateTimeOffset.UtcNow,
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(operationId)))[..16], stage, code));
        }
        catch (Exception) { /* A diagnostic sink cannot change read, dispatch or commit behavior. */ }
    }
}
