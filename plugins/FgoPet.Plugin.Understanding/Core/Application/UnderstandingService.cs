using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FgoPet.Core.Dialogue;
using FgoPet.Extensibility;
using FgoPet.Infrastructure.Persistence;
using FgoPet.Understanding.Contracts;

namespace FgoPet.Understanding;

/// <summary>Local review owner. No model dispatch, background accumulation or dialogue writes.</summary>
public sealed partial class UnderstandingService(RuntimeDatabase database, IConversationSourceReader sources,
    TimeProvider? clock = null, IUnderstandingRoutingPolicy? routing = null,
    Action<UnderstandingDiagnostic>? diagnostics = null) : IUnderstandingReader, IDerivedConversationData
{
    private readonly object _gate = new();
    private readonly Dictionary<string, UnderstandingEntry> _previews = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private bool _schemaReady;
    private void PrunePreviews()
    {
        foreach (var key in _previews.Where(p => p.Value.ObservedAt.AddMinutes(10) <= _clock.GetUtcNow()).Select(p => p.Key).ToArray()) _previews.Remove(key);
    }
    public UnderstandingState State(ConversationScope scope)
    {
        lock (_gate) { PrunePreviews(); EnsureSchema(); using var db = database.Open(); return ReadState(db, null, scope); }
    }

    public UnderstandingState Configure(ConversationScope scope, long revision, UnderstandingPolicy policy, long sourceBoundary = 0, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (!Enum.IsDefined(policy.Mode) || sourceBoundary < 0) throw new ArgumentException("UNDERSTANDING_INVALID_POLICY");
        lock (_gate)
        {
            token.ThrowIfCancellationRequested();
            EnsureSchema(); using var db = database.Open(); using var tx = db.BeginTransaction();
            var current = ReadState(db, tx, scope);
            if (revision != current.Revision) throw new InvalidOperationException("UNDERSTANDING_STALE");
            if (policy == current.Policy) return current;
            // A staged or renewed read grant starts from the host's current source boundary.
            var next = new UnderstandingState(policy, current.Revision + 1, current.Generation + 1,
                policy.Mode != UnderstandingMode.Off && (current.Policy.Mode == UnderstandingMode.Off || !current.Policy.ReadSources && policy.ReadSources)
                    ? sourceBoundary : current.AutoSince);
            // Every change re-evaluates unreviewed effects. Re-enabling never batch-reactivates them.
            foreach (var entry in ReadEntries(db, tx, scope).Where(e => e.Review == UnderstandingReview.Unreviewed))
                Save(db, tx, entry with { Revision = entry.Revision + 1, PolicyGeneration = next.Generation,
                    Life = entry.Effect == UnderstandingEffect.Bounded && entry.Life == UnderstandingLife.Active ? UnderstandingLife.Dormant : entry.Life });
            WriteState(db, tx, scope, next);
            token.ThrowIfCancellationRequested();
            tx.Commit();
            foreach (var key in _previews.Where(p => p.Value.Scope == scope).Select(p => p.Key).ToArray()) _previews.Remove(key);
            Log(scope.Key, "policy_changed"); return next;
        }
    }

    public UnderstandingTicket Begin(ConversationScope scope, string id, IReadOnlyList<ConversationSourceSnapshot> roots, bool manual = false, CancellationToken token = default,
        IReadOnlyList<UnderstandingSourceCoverage>? coverage = null)
    {
        if (!ValidId(id)) throw new ArgumentException("UNDERSTANDING_INVALID_ID");
        lock (_gate)
        {
            token.ThrowIfCancellationRequested();
            var state = State(scope);
            Log(id, "review_received");
            if (!state.Policy.Enabled || !state.Policy.ReadSources || !manual && state.Policy.Mode == UnderstandingMode.Off)
                throw new InvalidOperationException("UNDERSTANDING_NOT_AUTHORIZED");
            var denial = CheckSources(scope, roots, id);
            if (denial is not null) throw new InvalidOperationException(denial);
            if (coverage is not null && (coverage.Count > roots.Count || coverage.Any(c => c?.Source is null) || coverage.Select(c => c.Source.MessageId).Distinct().Count() != coverage.Count ||
                coverage.Any(c => !roots.Contains(c.Source) || c.StartScalar < 0 || c.IncludedScalars < 0 || c.TotalScalars is < 1 or > 12000 ||
                    (long)c.StartScalar + c.IncludedScalars > c.TotalScalars))) throw new ArgumentException("UNDERSTANDING_INVALID_COVERAGE");
            token.ThrowIfCancellationRequested();
            Log(id, "read_set_bound");
            return new(id, scope, state.Generation, state.Revision, manual, roots.ToArray(), coverage?.ToArray());
        }
    }

    public UnderstandingReceipt Apply(UnderstandingTicket ticket, IReadOnlyList<UnderstandingProposal> proposals, CancellationToken token = default,
        IUnderstandingRoutingPolicy? classification = null, Func<bool>? inputCurrent = null)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        if (proposals is null || proposals.Count > 32 || proposals.Any(p => p is null))
            throw new ArgumentException("UNDERSTANDING_INVALID_BATCH");
        lock (_gate)
        {
            token.ThrowIfCancellationRequested();
            PrunePreviews(); Log(ticket.Id, "batch_received");
            EnsureSchema(); using var db = database.Open();
            // Receipt lookup precedes fresh input, source and policy validation.
            if (ReadReceipt(db, null, ticket.Scope, ticket.Id) is { } duplicate) return duplicate;
            var version = DataVersion(db);
            var admitted = ReadState(db, null, ticket.Scope);
            var denial = !admitted.Policy.Enabled || !admitted.Policy.ReadSources ? "UNDERSTANDING_NOT_AUTHORIZED" :
                admitted.Generation != ticket.Generation || admitted.Revision != ticket.InputRevision ? "UNDERSTANDING_STALE" : CheckSources(ticket.Scope, ticket.Sources, ticket.Id);
            if(inputCurrent?.Invoke()==false)denial="UNDERSTANDING_STALE_INPUT";
            using var tx = db.BeginTransaction();
            if (ReadReceipt(db, tx, ticket.Scope, ticket.Id) is { } replay) return replay;
            var state = ReadState(db, tx, ticket.Scope);
            if (version != DataVersion(db, tx) || state.Revision != ticket.InputRevision || state.Generation != ticket.Generation)
                denial = "UNDERSTANDING_STALE";
            if (!state.Policy.Enabled || !state.Policy.ReadSources || !ticket.Manual && state.Policy.Mode == UnderstandingMode.Off)
                denial = "UNDERSTANDING_NOT_AUTHORIZED";
            var existing = ReadEntries(db, tx, ticket.Scope);
            if (proposals.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count() != proposals.Count ||
                proposals.Where(p => p.TargetId is not null).Select(p => p.TargetId).Distinct().Count() != proposals.Count(p => p.TargetId is not null))
                denial = "UNDERSTANDING_INVALID_BATCH";
            var prepared = new List<UnderstandingEntry>();
            if (denial is null)
            foreach (var proposal in proposals)
            {
                denial = ValidateProposal(ticket, proposal, existing);
                if (IsRevoked(db, tx, ticket.Scope.Key, proposal.Id)) denial = "UNDERSTANDING_REJECTED";
                if (denial is not null) break;
                var low = (classification??routing)?.IsLowImpact(proposal, ticket.Sources) ?? false;
                var bounded = state.Policy.Mode == UnderstandingMode.Bounded && low &&
                    (proposal.Domain == UnderstandingDomain.Self && proposal.Subject == "assistant" || proposal.Domain == UnderstandingDomain.World && proposal.Subject == "world") &&
                    proposal.Domain is UnderstandingDomain.Self or UnderstandingDomain.World &&
                    proposal.Kind is UnderstandingKind.CurrentInterest or UnderstandingKind.CurrentStance or UnderstandingKind.OpenQuestion or
                        UnderstandingKind.SelfReflection or UnderstandingKind.ExternalObservation or UnderstandingKind.Interpretation;
                if (proposal.Sources.All(s => existing.Any(e => e.Sources.Contains(s)))) bounded = false;
                // A less restrictive label cannot silently retire an existing high-impact interpretation.
                if (proposal.TargetId is { } targetId && existing.Single(e => e.Id == targetId).Domain is UnderstandingDomain.User or UnderstandingDomain.Relationship)
                    bounded = false;
                var coverage = ticket.Coverage?.Where(c => proposal.Sources.Contains(c.Source)).ToArray();
                if (coverage is null || proposal.Sources.Any(s => !coverage.Any(c => c.Source == s && c.IncludedScalars > 0))) bounded = false;
                var now = _clock.GetUtcNow();
                var assistantOnly = proposal.Sources.All(s => s.Kind == ConversationSourceKind.Assistant);
                if (assistantOnly && proposal.Kind is not (UnderstandingKind.CurrentInterest or UnderstandingKind.CurrentStance)) bounded = false;
                prepared.Add(new(proposal.Id, ticket.Scope, 1, proposal.Domain, proposal.Kind, proposal.Subject,
                    proposal.Content, proposal.Conditions,
                    assistantOnly ? "assistant_stance" : "model_inferred",
                    proposal.Sources.ToArray(), UnderstandingReview.Unreviewed,
                    bounded ? UnderstandingEffect.Bounded : UnderstandingEffect.Pending, UnderstandingLife.Active, now,
                    proposal.Domain == UnderstandingDomain.Self ? now.AddDays(7) : DateTimeOffset.MaxValue,
                    state.Generation, proposal.TargetId, proposal.TargetRevision, Coverage: coverage));
            }
            UnderstandingReceipt receipt;
            if (denial is not null)
                receipt = new(ticket.Id, proposals.Count == 0 ? [new("read-set", UnderstandingOutcome.Failed, Code: denial)] :
                    proposals.Select(p => new UnderstandingItemReceipt(p.Id, UnderstandingOutcome.Failed, Code: denial)).ToArray());
            else
            {
                var persist = state.Policy.Mode != UnderstandingMode.Off && state.Policy.SaveCandidates;
                foreach (var entry in prepared)
                {
                    if (persist)
                    {
                        if (entry.Effect != UnderstandingEffect.Pending && entry.ReplacesId is { } target)
                        {
                            var old = existing.Single(e => e.Id == target);
                            Save(db, tx, old with { Revision = old.Revision + 1, Life = UnderstandingLife.Retracted });
                        }
                        Save(db, tx, entry);
                    }
                }
                receipt = new(ticket.Id, prepared.Select(e => new UnderstandingItemReceipt(e.Id,
                    !persist ? UnderstandingOutcome.ReviewedNotPersisted : e.Effect == UnderstandingEffect.Pending
                        ? UnderstandingOutcome.ProposedPending : UnderstandingOutcome.Applied, e.Revision)).ToArray());
                if (prepared.Count == 0) receipt = new(ticket.Id, [new("read-set", UnderstandingOutcome.NoChange)]);
                if (persist) WriteState(db, tx, ticket.Scope, state with { Revision = state.Revision + 1 });
            }
            WriteReceipt(db, tx, ticket.Scope, receipt);
            Execute(db, tx, "INSERT INTO understanding_coverage VALUES($scope,$id,$hash,$outcomes)",
                ("$scope", ticket.Scope.Key), ("$id", ticket.Id), ("$hash", SourceHash(ticket.Sources)), ("$outcomes", JsonSerializer.Serialize(receipt.Items)));
            token.ThrowIfCancellationRequested();
            tx.Commit();
            // Preview payload lives only in this owner's memory, after the metadata receipt commits.
            if (denial is null && (state.Policy.Mode == UnderstandingMode.Off || !state.Policy.SaveCandidates))
                foreach (var entry in prepared) _previews[PreviewKey(ticket.Scope, entry.Id)] = entry with { Effect = UnderstandingEffect.Pending };
            Log(ticket.Id, "batch_committed", denial); return receipt;
        }
    }

    public IReadOnlyList<UnderstandingEntry> List(ConversationScope scope, bool pending)
    {
        lock (_gate)
        {
            PrunePreviews();
            EnsureSchema(); using var db = database.Open();
            var entries = ReadEntries(db, null, scope).Concat(_previews.Values.Where(e => e.Scope == scope && e.ObservedAt.AddMinutes(10) > _clock.GetUtcNow()));
            return entries.Where(e => pending ? e.Effect == UnderstandingEffect.Pending && e.Review == UnderstandingReview.Unreviewed
                    : e.Effect != UnderstandingEffect.Pending || e.Review == UnderstandingReview.Rejected)
                .Select(NormalizeLife).OrderByDescending(e => e.ObservedAt).ThenBy(e => e.Id, StringComparer.Ordinal).ToArray();
        }
    }

    public UnderstandingReceipt Review(ConversationScope scope, string operationId, string id, int revision,
        UnderstandingAction action, string? correction = null, CancellationToken token = default)
    {
        if (!ValidId(operationId) || !ValidId(id) || !Enum.IsDefined(action)) throw new ArgumentException("UNDERSTANDING_INVALID_REVIEW");
        lock (_gate)
        {
            token.ThrowIfCancellationRequested();
            PrunePreviews(); Log(operationId, "approval_received");
            EnsureSchema(); using var db = database.Open();
            if (ReadReceipt(db, null, scope, operationId) is { } duplicate) return duplicate;
            var version = DataVersion(db);
            var entry = ReadEntries(db, null, scope).SingleOrDefault(e => e.Id == id);
            var preview = entry is null;
            entry ??= _previews.GetValueOrDefault(PreviewKey(scope, id));
            var denial = entry is null || entry.Revision != revision ? "UNDERSTANDING_STALE" : null;
            var admitted = ReadState(db, null, scope);
            if (action is UnderstandingAction.Approve or UnderstandingAction.Restore or UnderstandingAction.Correct &&
                (!admitted.Policy.Enabled || !admitted.Policy.ReadSources)) denial = "UNDERSTANDING_NOT_AUTHORIZED";
            if (denial is null && action is UnderstandingAction.Approve or UnderstandingAction.Restore or UnderstandingAction.Correct)
                denial = CheckSources(scope, entry!.Sources, operationId);
            using var tx = db.BeginTransaction();
            if (ReadReceipt(db, tx, scope, operationId) is { } replay) return replay;
            var state = ReadState(db, tx, scope);
            if (DataVersion(db, tx) != version) denial = "UNDERSTANDING_STALE";
            if (!state.Policy.Enabled && action is not (UnderstandingAction.Delete or UnderstandingAction.Reject or UnderstandingAction.Suspend))
                denial = "UNDERSTANDING_NOT_AUTHORIZED";
            if (entry is not null && preview && (entry.PolicyGeneration != state.Generation || entry.ObservedAt.AddMinutes(10) <= _clock.GetUtcNow()))
                denial = "UNDERSTANDING_STALE";
            if (action == UnderstandingAction.Correct && (string.IsNullOrWhiteSpace(correction) || correction.Length > 2000))
                denial = "UNDERSTANDING_INVALID_CORRECTION";
            if (action is UnderstandingAction.Approve or UnderstandingAction.Restore &&
                (entry?.Review == UnderstandingReview.Rejected || entry?.Life == UnderstandingLife.Retracted)) denial = "UNDERSTANDING_REJECTED";
            if (denial is null && entry?.Life == UnderstandingLife.Retracted && action is UnderstandingAction.Suspend or UnderstandingAction.Resolve)
                denial = "UNDERSTANDING_REJECTED";
            if (entry is not null && action is UnderstandingAction.Approve or UnderstandingAction.Restore && entry.ActiveUntil <= _clock.GetUtcNow())
                denial = "UNDERSTANDING_NEW_EVIDENCE_REQUIRED";
            if (action is UnderstandingAction.Approve or UnderstandingAction.Restore or UnderstandingAction.Correct && !state.Policy.ReadSources)
                denial = "UNDERSTANDING_NOT_AUTHORIZED";
            UnderstandingEntry? replacementTarget = null;
            if (entry?.Effect == UnderstandingEffect.Pending && entry.ReplacesId is { } targetId &&
                action is UnderstandingAction.Approve or UnderstandingAction.Restore)
            {
                replacementTarget = ReadEntries(db, tx, scope).SingleOrDefault(e => e.Id == targetId);
                if (replacementTarget is null || replacementTarget.Revision != entry.ReplacesRevision || replacementTarget.Life == UnderstandingLife.Retracted)
                    denial = "UNDERSTANDING_STALE";
            }
            UnderstandingReceipt receipt;
            if (denial is not null) receipt = new(operationId, [new(id, UnderstandingOutcome.Failed, Code: denial)]);
            else
            {
                var next = entry! with { Revision = entry!.Revision + 1 };
                next = action switch
                {
                    UnderstandingAction.Approve or UnderstandingAction.Restore => next with { Review = UnderstandingReview.Approved,
                        Effect = UnderstandingEffect.Approved, Life = UnderstandingLife.Active, PolicyGeneration = state.Generation },
                    UnderstandingAction.Reject => next with { Review = UnderstandingReview.Rejected, Life = UnderstandingLife.Retracted },
                    UnderstandingAction.Suspend => next with { Life = UnderstandingLife.Dormant },
                    UnderstandingAction.Resolve => next with { Life = UnderstandingLife.Resolved },
                    UnderstandingAction.Correct => next with { Correction = correction },
                    _ => next
                };
                if (replacementTarget is not null)
                    Save(db, tx, replacementTarget with { Revision = replacementTarget.Revision + 1, Life = UnderstandingLife.Retracted });
                if (action == UnderstandingAction.Delete)
                {
                    Execute(db, tx, "DELETE FROM understanding_entries WHERE scope=$scope AND entry_id=$id", ("$scope", scope.Key), ("$id", id));
                    Execute(db, tx, "DELETE FROM understanding_revisions WHERE scope=$scope AND entry_id=$id", ("$scope", scope.Key), ("$id", id));
                }
                else if (!preview || action == UnderstandingAction.Approve) Save(db, tx, next);
                if (action is UnderstandingAction.Delete or UnderstandingAction.Reject) Revoke(db, tx, scope.Key, id);
                // An OFF preview edit remains RAM-only and invalidates its old approval revision.
                WriteState(db, tx, scope, state with { Revision = state.Revision + 1 });
                receipt = new(operationId, [new(id, action == UnderstandingAction.Reject ? UnderstandingOutcome.NoChange : UnderstandingOutcome.Applied, next.Revision)]);
            }
            WriteReceipt(db, tx, scope, receipt); token.ThrowIfCancellationRequested(); tx.Commit();
            if (denial is null)
            {
                var key = PreviewKey(scope, id);
                if (preview && action == UnderstandingAction.Correct) _previews[key] = entry! with { Revision = revision + 1, Correction = correction };
                else _previews.Remove(key);
            }
            Log(operationId, "review_committed", denial); return receipt;
        }
    }

    public UnderstandingProjection Query(ConversationScope scope, string query, bool historical = false, string? entryId = null)
    {
        if (query is null || query.Length > 512 || historical && !ValidId(entryId)) throw new ArgumentException("UNDERSTANDING_INVALID_QUERY");
        lock (_gate)
        {
            EnsureSchema(); using var db = database.Open(); var version = DataVersion(db);
            var state = ReadState(db, null, scope);
            if (!state.Policy.Enabled) return new(state.Revision, [], "UNDERSTANDING_DISABLED");
            if (!state.Policy.UseInDialogue || !state.Policy.ReadSources) return new(state.Revision, [], "UNDERSTANDING_NOT_AUTHORIZED");
            var entries = ReadEntries(db, null, scope).Select(NormalizeLife).Where(e =>
                e.Review != UnderstandingReview.Rejected && e.Effect != UnderstandingEffect.Pending &&
                (e.Effect == UnderstandingEffect.Approved || historical || state.Policy.Mode == UnderstandingMode.Bounded && e.PolicyGeneration == state.Generation) &&
                (historical ? e.Id == entryId && e.Life is UnderstandingLife.Active or UnderstandingLife.Dormant : e.Life == UnderstandingLife.Active) &&
                (query.Length == 0 || e.Content.Contains(query, StringComparison.OrdinalIgnoreCase) || e.Correction?.Contains(query, StringComparison.OrdinalIgnoreCase) == true))
                .OrderByDescending(e => e.ObservedAt).ThenBy(e => e.Id, StringComparer.Ordinal).Take(32).ToArray();
            foreach (var entry in entries)
                if (CheckSources(scope, entry.Sources) is { } code) return new(state.Revision, [], code);
            // No stale projection is returned across another connection's commit.
            if (version != DataVersion(db)) return new(state.Revision, [], "UNDERSTANDING_SOURCE_CHANGED");
            return new(state.Revision, entries);
        }
    }

    private UnderstandingEntry NormalizeLife(UnderstandingEntry entry) => entry.Life == UnderstandingLife.Active && entry.ActiveUntil <= _clock.GetUtcNow()
        ? entry with { Life = UnderstandingLife.Dormant } : entry;
    private string? ValidateProposal(UnderstandingTicket ticket, UnderstandingProposal p, IReadOnlyList<UnderstandingEntry> existing)
    {
        if (!ValidId(p.Id) || !Enum.IsDefined(p.Domain) || !Enum.IsDefined(p.Kind) ||
            string.IsNullOrWhiteSpace(p.Subject) || p.Subject.Length > 128 || string.IsNullOrWhiteSpace(p.Content) || p.Content.Length > 2000 ||
            string.IsNullOrWhiteSpace(p.Conditions) || p.Conditions.Length > 1000 || p.Sources is null || p.Sources.Count == 0 ||
            p.Sources.Any(s => !ticket.Sources.Contains(s))) return "UNDERSTANDING_INVALID_PROPOSAL";
        if (p.Kind == UnderstandingKind.UserFact) return "UNDERSTANDING_MEMORY_OWNER_REQUIRED";
        if (p.Kind is UnderstandingKind.StableIdentity or UnderstandingKind.Permission) return "UNDERSTANDING_DEFINITION_FLOW_REQUIRED";
        if (p.Sources.All(s => s.Kind == ConversationSourceKind.Assistant) &&
            (p.Domain != UnderstandingDomain.Self || p.Kind is not (UnderstandingKind.CurrentInterest or UnderstandingKind.CurrentStance)))
            return "UNDERSTANDING_INSUFFICIENT_ROOTS";
        if (existing.Any(e => e.Id == p.Id) || _previews.ContainsKey(PreviewKey(ticket.Scope, p.Id))) return "UNDERSTANDING_DUPLICATE_ID";
        if ((p.TargetId is null) != (p.TargetRevision is null) || p.TargetId is { } target && !existing.Any(e => e.Id == target && e.Revision == p.TargetRevision && e.Life != UnderstandingLife.Retracted))
            return "UNDERSTANDING_STALE";
        if (p.TargetId is { } replaced && existing.Single(e => e.Id == replaced) is { } prior &&
            NormalizeLife(prior).Life == UnderstandingLife.Dormant && p.Sources.All(s => prior.Sources.Contains(s)))
            return "UNDERSTANDING_NEW_EVIDENCE_REQUIRED";
        return null;
    }
    private string? CheckSources(ConversationScope scope, IReadOnlyList<ConversationSourceSnapshot>? roots, string? correlation = null)
    {
        if (roots is null || roots.Count is < 1 or > 32) return "UNDERSTANDING_SOURCE_INVALID";
        if (roots.Any(s => s?.Scope is null || s.Scope.RoleId != scope.ServantId || s.Scope.ProjectId != scope.ProjectId ||
            !ValidId(s.Scope.ConversationId) || !ValidId(s.MessageId) || !Enum.IsDefined(s.Kind) ||
            s.SourceRevision < 0 || s.Fingerprint is not { Length: 64 } || !s.Fingerprint.All(Uri.IsHexDigit))) return "UNDERSTANDING_SCOPE_DENIED";
        if(roots.Any(s=>s.Kind==ConversationSourceKind.External&&(s.SourceRevision<=0||string.IsNullOrWhiteSpace(s.SourceLabel)||
            s.SourceLabel.Length>128||string.IsNullOrWhiteSpace(s.Author)||s.Author.Length>160)))return "UNDERSTANDING_SOURCE_INVALID";
        if (roots.Select(s => (s.Scope.ConversationId, s.MessageId)).Distinct().Count() != roots.Count) return "UNDERSTANDING_SOURCE_INVALID";
        try
        {
            Log(correlation ?? scope.Key, "source_validation_started");
            foreach (var root in roots)
            {
                var status = sources is IExperienceTimeline timeline ? timeline.ValidateSource(root) :
                    sources.IsCurrent(root) ? ExperienceSourceStatus.Available : ExperienceSourceStatus.Changed;
                if (status != ExperienceSourceStatus.Available)
                {
                    var code = status == ExperienceSourceStatus.Unavailable ? "UNDERSTANDING_SOURCE_UNAVAILABLE" : "UNDERSTANDING_SOURCE_CHANGED";
                    Log(correlation ?? scope.Key, "source_validation_failed", code); return code;
                }
            }
            Log(correlation ?? scope.Key, "source_validation_completed");
            return null;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return "UNDERSTANDING_SOURCE_UNAVAILABLE"; }
    }
    private static bool ValidId(string? value) => value is { Length: > 0 and <= 128 } && !value.Any(c => char.IsControl(c) || char.IsWhiteSpace(c));
    private static string PreviewKey(ConversationScope scope, string id) => scope.Key + ":" + id;
    private void Log(string correlation, string stage, string? code = null)
    {
        try { diagnostics?.Invoke(new(_clock.GetUtcNow(), Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(correlation)))[..16], stage, code)); }
        catch (Exception) { /* Diagnostics cannot change commit or disclose payload. */ }
    }
}
