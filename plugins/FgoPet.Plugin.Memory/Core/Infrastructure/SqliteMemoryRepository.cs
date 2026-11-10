using System.Globalization;
using FgoPet.App.Dialogue;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Memory;
using FgoPet.Extensibility;
using FgoPet.Infrastructure.Persistence;
using FgoPet.Memory.Settings;
using Microsoft.Data.Sqlite;

namespace FgoPet.Infrastructure.Memory;

/// <summary>Receipt, review, quota and version checks share a SQLite write transaction.</summary>
public sealed class SqliteMemoryRepository : IConversationMemory, IMemoryCandidateSink, IMemoryWriteLifetime, IMemorySearchReader,
    IConversationMemoryReviewNotices
{
    private const string EnabledScope = "memories.is_enabled=1 AND (memories.project_id IS NULL OR memories.project_id=$project)";
    private readonly RuntimeDatabase _database;
    private readonly IMemorySettingsStore? _settings;
    private readonly MemoryExtractionSourceReader? _sources;
    public event Action<ToolScope>? CandidatesStaged;
    public SqliteMemoryRepository(RuntimeDatabase database, IMemorySettingsStore? settings = null,
        IConversationSourceReader? sources = null)
    {
        _database = database;
        _settings = settings;
        _sources = sources is null ? null : new(sources);
    }

    private bool AutoRecordEnabled() =>
        _settings?.Load() is { Enabled: true, AutoRecordEpisodic: true };
    public void StartSession() => InvalidateWrites();
    public void InvalidateWrites()
    {
        using var db = _database.Open();
        using var tx = db.BeginTransaction();
        Rotate(db, tx);
        tx.Commit();
    }
    private static void Rotate(SqliteConnection db, SqliteTransaction tx)
    {
        Execute(db, tx, "UPDATE memory_write_state SET generation=$g,revision=revision+1 WHERE singleton_id=1", ("$g", Guid.NewGuid().ToString("N")));
        Execute(db, tx, "UPDATE memory_ingestions SET status='abandoned' WHERE status='pending'");
    }
    private static (string Generation, long Revision) State(SqliteConnection db, SqliteTransaction tx)
    {
        using var cmd = Command(db, tx, "SELECT generation,revision FROM memory_write_state WHERE singleton_id=1");
        using var r = cmd.ExecuteReader();
        if (!r.Read()) throw new InvalidOperationException("Memory write state unavailable.");
        return (r.GetString(0), r.GetInt64(1));
    }
    public MemoryWriteTicket Begin(MemorySource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        using var db = _database.Open();
        using var tx = db.BeginTransaction();
        var state = State(db, tx);
        Execute(db, tx, "INSERT OR IGNORE INTO memory_ingestions VALUES($key,$servant,$project,$id,$g,'pending',$now)",
            ("$key", source.Key), ("$servant", source.Scope.ServantId), ("$project", source.Scope.ProjectId),
            ("$id", Guid.NewGuid().ToString("N")), ("$g", state.Generation), ("$now", DateTimeOffset.UtcNow.ToString("O")));
        using var cmd = Command(db, tx, "SELECT ticket_id,generation FROM memory_ingestions WHERE source_key=$key", ("$key", source.Key));
        using var r = cmd.ExecuteReader();
        r.Read();
        var ticket = new MemoryWriteTicket(r.GetString(0), r.GetString(1), state.Revision, source)
        {
            AutoRecordAuthorized = source.Kind == MemoryEvidenceKind.UserStatement && AutoRecordEnabled(),
        };
        r.Close();
        tx.Commit();
        return ticket;
    }
    public void Abandon(MemoryWriteTicket ticket)
    {
        using var db = _database.Open();
        Execute(db, null, "UPDATE memory_ingestions SET status='abandoned' WHERE source_key=$key AND ticket_id=$id AND generation=$g AND status='pending'",
            ("$key", ticket.Source.Key), ("$id", ticket.TicketId), ("$g", ticket.Generation));
    }
    public MemoryWriteTicket? Refresh(MemoryWriteTicket ticket)
    {
        using var db = _database.Open();
        var revision = Scalar(db, null, """
            SELECT s.revision FROM memory_write_state s JOIN memory_ingestions i ON i.generation=s.generation
            WHERE s.singleton_id=1 AND i.source_key=$key AND i.ticket_id=$id AND i.generation=$g AND i.status='pending'
            """, ("$key", ticket.Source.Key), ("$id", ticket.TicketId), ("$g", ticket.Generation));
        return revision is long current ? ticket with { MemoryRevision = current } : null;
    }

    public MemoryStageResult Stage(MemoryWriteTicket ticket, IReadOnlyList<MemoryProposal> proposals)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        using var db = _database.Open();
        using var tx = db.BeginTransaction();
        var s = ticket.Source;
        var state = State(db, tx);
        var receipt = Scalar(db, tx, "SELECT status FROM memory_ingestions WHERE source_key=$key AND ticket_id=$id AND generation=$g",
            ("$key", s.Key), ("$id", ticket.TicketId), ("$g", ticket.Generation)) as string;
        if (receipt is "staged" or "deleted" or "rejected")
            return new(MemoryStageStatus.Duplicate, Candidates(db, tx, s.Scope.ServantId).Where(c => c.Source?.Key == s.Key).Select(c => c.CandidateId).ToArray());
        if (receipt != "pending" || state.Generation != ticket.Generation || state.Revision != ticket.MemoryRevision ||
            Convert.ToInt64(Scalar(db, tx, """
                SELECT COUNT(*) FROM chat_messages m JOIN conversations c ON c.conversation_id=m.conversation_id
                WHERE m.message_id=$message AND m.conversation_id=$conversation AND m.servant_id=$servant
                  AND c.servant_id=$servant AND c.project_id IS $project AND m.status='completed'
                """, ("$message", s.MessageId), ("$conversation", s.ConversationId),
                ("$servant", s.Scope.ServantId), ("$project", s.Scope.ProjectId))) != 1)
            return new(MemoryStageStatus.Stale, []);
        if (proposals is null || proposals.Count > 3 || proposals.Any(p => p is null)) return new(MemoryStageStatus.Invalid, []);
        var memories = Memories(db, tx, s.Scope.ServantId);
        var candidates = Candidates(db, tx, s.Scope.ServantId);
        var pending = new Dictionary<MemoryProposal, IReadOnlyList<VerifiedEvidence>>();
        var normalized = new HashSet<string>(StringComparer.Ordinal);
        var recorded = false;
        foreach (var proposal in proposals)
        {
            var operation = proposal.Lifecycle?.Operation;
            var target = memories.SingleOrDefault(m => m.MemoryId == proposal.ReplacesMemoryId &&
                m.ProjectId == s.Scope.ProjectId && m.Version == proposal.ExpectedMemoryVersion);
            if (proposal.ReplacesMemoryId is not null && target is null)
                return new(MemoryStageStatus.Invalid, []);

            var identity = MemoryTextIdentity.Normalize(proposal.Text);
            if (!normalized.Add(identity) ||
                (operation != MemoryOperation.Transition && memories.Any(m =>
                    m.ProjectId == s.Scope.ProjectId && MemoryTextIdentity.Normalize(m.Text) == identity)) ||
                candidates.Any(c => c.ProjectId == s.Scope.ProjectId && c.Status == MemoryCandidateStatus.Pending &&
                    MemoryTextIdentity.Normalize(c.Text) == identity))
                continue;

            var canAutomate = ticket.AutoRecordAuthorized && AutoRecordEnabled() &&
                proposal.Lifecycle is { SupportsAutomaticWrite: true, ContentType: MemoryContentType.Episodic } &&
                (operation == MemoryOperation.Create && target is null ||
                    operation is MemoryOperation.Duplicate or MemoryOperation.Extend or MemoryOperation.Transition &&
                    target is { IsEnabled: true, SourceAvailable: true, HasNewerState: false });
            var evidence = proposal.Lifecycle is { } assessment
                ? VerifyEvidence(db, tx, s, assessment.Evidence) : [];
            if (proposal.Lifecycle is not null && evidence.Count == 0)
                return new(MemoryStageStatus.Invalid, []);
            if (!canAutomate)
            {
                pending.Add(proposal, evidence);
                continue;
            }
            if (operation == MemoryOperation.Duplicate) continue;

            var extend = operation == MemoryOperation.Extend && target?.Verification == MemoryVerification.Recorded;
            if (!FitsCapacity(memories, extend ? 0 : 1, proposal.Text.Length - (extend ? target!.Text.Length : 0)))
                return new(MemoryStageStatus.CapacityExceeded, []);

            var id = extend ? target!.MemoryId : "memory-" + Guid.NewGuid().ToString("N");
            var now = DateTimeOffset.UtcNow;
            if (extend)
            {
                Execute(db, tx, """
                    UPDATE memories SET memory_text=$text,version=version+1,updated_at_utc=$now WHERE memory_id=$id
                    """, ("$text", proposal.Text), ("$now", now.ToString("O")), ("$id", id));
            }
            else
            {
                InsertMemory(db, tx, id, s.Scope, proposal.Text, null, now, s,
                    MemoryContentType.Episodic, MemoryVerification.Recorded);
                if (target is not null)
                    Execute(db, tx, "UPDATE memories SET previous_memory_id=$previous,relation=$relation WHERE memory_id=$id",
                        ("$previous", target.MemoryId), ("$relation", operation!.Value.ToString()), ("$id", id));
            }
            InsertEvidence(db, tx, evidence, memoryId: id);
            recorded = true;
            memories = Memories(db, tx, s.Scope.ServantId);
        }
        if (!FitsCapacity(memories, pending.Keys.Count(p => p.ReplacesMemoryId is null ||
                p.Lifecycle?.Operation is MemoryOperation.Duplicate or MemoryOperation.Extend or MemoryOperation.Transition),
            pending.Keys.Sum(p => p.Text.Length - (p.Lifecycle is null || p.Lifecycle.Operation == MemoryOperation.Correction
                ? memories.FirstOrDefault(m => m.MemoryId == p.ReplacesMemoryId)?.Text.Length ?? 0 : 0))))
            return new(MemoryStageStatus.CapacityExceeded, []);
        var ids = new List<string>();
        foreach (var (p, evidence) in pending)
        {
            var correction = p.Lifecycle is null || p.Lifecycle.Operation == MemoryOperation.Correction;
            var candidate = new MemoryCandidate("candidate-" + Guid.NewGuid().ToString("N"), s.Scope.ServantId, s.ConversationId,
                p.Text, DateTimeOffset.UtcNow, s.MessageId, projectId: s.Scope.ProjectId, source: s,
                replacesMemoryId: correction ? p.ReplacesMemoryId : null,
                expectedMemoryVersion: correction ? p.ExpectedMemoryVersion : null,
                contentType: p.Lifecycle?.ContentType ?? MemoryContentType.Semantic);
            InsertCandidate(db, tx, candidate);
            InsertEvidence(db, tx, evidence, candidateId: candidate.CandidateId);
            ids.Add(candidate.CandidateId);
        }
        Receipt(db, tx, s.Key, "staged");
        if (recorded) Bump(db, tx);
        tx.Commit();
        if (ids.Count > 0 && CandidatesStaged is { } listeners)
        {
            var scope = new ToolScope(s.ConversationId, s.Scope.ServantId, s.Scope.ProjectId);
            foreach (Action<ToolScope> listener in listeners.GetInvocationList())
            {
                try { listener(scope); }
                catch (Exception) { /* A presentation listener cannot fail a committed write. */ }
            }
        }
        return new(recorded ? MemoryStageStatus.Recorded :
            ids.Count == 0 && proposals.Count > 0 ? MemoryStageStatus.Duplicate : MemoryStageStatus.Staged, ids);
    }

    private IReadOnlyList<VerifiedEvidence> VerifyEvidence(
        SqliteConnection db, SqliteTransaction tx, MemorySource primary, IReadOnlyList<MemoryEvidence> evidence)
    {
        if (_sources is null || primary.Kind != MemoryEvidenceKind.UserStatement ||
            evidence is null || evidence.Count is < 1 or > 3 || evidence.Any(e => e is null) ||
            !evidence.Any(e => e.MessageId == primary.MessageId) || !_sources.IsCurrent(primary))
            return [];

        var verified = new List<VerifiedEvidence>();
        foreach (var citation in evidence)
        {
            if (string.IsNullOrWhiteSpace(citation.MessageId) || string.IsNullOrWhiteSpace(citation.Quote) ||
                citation.Quote.Length is < 2 or > 1600)
                return [];

            var text = Scalar(db, tx, """
                SELECT m.text FROM chat_messages m
                JOIN chat_messages current ON current.message_id=$current
                WHERE m.message_id=$id AND m.conversation_id=current.conversation_id AND m.servant_id=current.servant_id
                  AND m.role='user' AND m.status='completed'
                  AND (m.message_id=$current OR m.sequence=(
                    SELECT MAX(previous.sequence) FROM chat_messages previous
                    WHERE previous.conversation_id=current.conversation_id
                      AND previous.sequence<current.sequence AND previous.role='user' AND previous.status='completed'))
                """, ("$id", citation.MessageId), ("$current", primary.MessageId)) as string;
            var offset = text?.IndexOf(citation.Quote, StringComparison.Ordinal) ?? -1;
            if (offset < 0) return [];
            var source = citation.MessageId == primary.MessageId ? primary : _sources.Read(
                new(primary.ConversationId, primary.Scope.ServantId, primary.Scope.ProjectId),
                citation.MessageId, MemoryEvidenceKind.UserStatement);
            verified.Add(new(source, offset, citation.Quote.Length));
        }
        return verified;
    }

    private sealed record VerifiedEvidence(MemorySource Source, int Offset, int Length);

    private static void InsertEvidence(SqliteConnection db, SqliteTransaction tx, IReadOnlyList<VerifiedEvidence> evidence,
        string? memoryId = null, string? candidateId = null)
    {
        foreach (var (source, offset, length) in evidence)
            Execute(db, tx, """
                INSERT OR IGNORE INTO memory_evidence(memory_id,candidate_id,message_id,conversation_id,source_key,
                  source_fingerprint,quote_start,quote_length)
                VALUES($memory,$candidate,$message,$conversation,$key,$fingerprint,$offset,$length)
                """, ("$memory", memoryId), ("$candidate", candidateId), ("$message", source.MessageId),
                ("$conversation", source.ConversationId), ("$key", source.Key), ("$fingerprint", source.SourceFingerprint),
                ("$offset", offset), ("$length", length));
    }

    // Trusted local/import compatibility. No model-facing contract exposes this method.
    public void AddCandidate(MemoryCandidate candidate)
    {
        if (candidate.Status != MemoryCandidateStatus.Pending) throw new ArgumentException("Only pending candidates may be added.");
        using var db = _database.Open();
        using var tx = db.BeginTransaction();
        if (Convert.ToInt64(Scalar(db, tx, "SELECT COUNT(*) FROM conversations WHERE conversation_id=$id AND servant_id=$servant AND project_id IS $project",
            ("$id", candidate.ConversationId), ("$servant", candidate.ServantId), ("$project", candidate.ProjectId))) != 1)
            throw new ArgumentException("Candidate scope does not match its source.");
        InsertCandidate(db, tx, candidate);
        Bump(db, tx);
        tx.Commit();
    }
    private static void InsertCandidate(SqliteConnection db, SqliteTransaction tx, MemoryCandidate c) => Execute(db, tx, """
        INSERT INTO memory_candidates(candidate_id,conversation_id,source_message_id,servant_id,appearance_id,candidate_text,status,created_at_utc,
          project_id,source_fingerprint,evidence_kind,origin_at_utc,replaces_memory_id,expected_memory_version,source_project_label,content_type)
        VALUES($id,$conversation,$message,$servant,$appearance,$text,'pending',$created,$project,$fingerprint,$kind,$origin,$replacement,$version,$label,$type)
        """, ("$id", c.CandidateId), ("$conversation", c.ConversationId), ("$message", c.SourceMessageId), ("$servant", c.ServantId),
        ("$appearance", c.AppearanceId), ("$text", c.Text), ("$created", c.CreatedAtUtc.ToString("O")), ("$project", c.ProjectId),
        ("$fingerprint", c.Source?.SourceFingerprint), ("$kind", (c.Source?.Kind ?? MemoryEvidenceKind.UnknownLegacy).ToString()),
        ("$origin", c.Source?.OccurredAtUtc.ToString("O")), ("$replacement", c.ReplacesMemoryId), ("$version", c.ExpectedMemoryVersion), ("$label", c.Source?.ProjectLabel),
        ("$type", c.ContentType.ToString()));

    private static void InsertMemory(SqliteConnection db, SqliteTransaction tx, string id, MemoryScope scope,
        string text, string? candidateId, DateTimeOffset now, MemorySource? source,
        MemoryContentType contentType, MemoryVerification verification) => Execute(db, tx, """
        INSERT INTO memories(memory_id,servant_id,memory_text,is_enabled,source_candidate_id,created_at_utc,updated_at_utc,project_id,
          version,source_key,source_fingerprint,origin_conversation_id,origin_message_id,origin_at_utc,evidence_kind,origin_project_label,
          content_type,verification)
        VALUES($id,$servant,$text,1,$candidate,$now,$now,$project,1,$key,$fingerprint,$conversation,$message,$origin,$kind,$label,$type,$verification)
        """, ("$id", id), ("$servant", scope.ServantId), ("$text", text), ("$candidate", candidateId), ("$now", now.ToString("O")),
        ("$project", scope.ProjectId), ("$key", source?.Key), ("$fingerprint", source?.SourceFingerprint),
        ("$conversation", source?.ConversationId), ("$message", source?.MessageId), ("$origin", source?.OccurredAtUtc.ToString("O")),
        ("$kind", (source?.Kind ?? MemoryEvidenceKind.UnknownLegacy).ToString()), ("$label", source?.ProjectLabel),
        ("$type", contentType.ToString()), ("$verification", verification.ToString()));

    public IReadOnlyList<MemoryCandidate> ListCandidates(string servantId)
    {
        using var db = _database.Open();
        return Candidates(db, null, servantId);
    }
    public IReadOnlyList<StoredMemory> ListEnabledMemories(string servantId) => ListMemories(servantId).Where(m => m.IsEnabled).ToArray();
    public string ScopeLabel(string servantId, string? projectId)
    {
        if (projectId is null) return "角色通用";
        using var db = _database.Open();
        return Scalar(db, null, """
            SELECT origin_project_label FROM memories WHERE servant_id=$servant AND project_id=$project AND origin_project_label IS NOT NULL
            UNION ALL SELECT source_project_label FROM memory_candidates WHERE servant_id=$servant AND project_id=$project AND source_project_label IS NOT NULL LIMIT 1
            """,
            ("$servant", servantId), ("$project", projectId)) is string label && !string.IsNullOrWhiteSpace(label)
            ? "项目：" + label : "项目记忆（项目不可用）";
    }
    public IReadOnlyList<StoredMemory> ListMemories(string servantId)
    {
        using var db = _database.Open();
        return Memories(db, null, servantId);
    }
    public MemoryRecallSnapshot ReadSnapshot(MemoryScope scope)
    {
        using var db = _database.Open();
        using var tx = db.BeginTransaction(deferred: true);
        var result = ScopedSnapshot(db, tx, scope);
        tx.Commit();
        return result;
    }

    (MemoryRecallSnapshot Snapshot, IReadOnlyDictionary<string, double> Ranks) IMemorySearchReader.ReadSearchSnapshot(
        MemoryScope scope, IReadOnlyList<string> indexTerms)
    {
        using var db = _database.Open();
        using var tx = db.BeginTransaction(deferred: true);
        var snapshot = ScopedSnapshot(db, tx, scope);
        var ranks = new Dictionary<string, double>(StringComparer.Ordinal);
        if (indexTerms.Count > 0 && snapshot.Items.Count > 0)
        {
            var match = string.Join(" OR ", indexTerms.Select(term => "\"" + term.Replace("\"", "\"\"") + "\""));
            using var cmd = Command(db, tx, $"""
                SELECT memories.memory_id,bm25(memory_search)
                FROM memory_search JOIN memory_search_keys k ON k.search_id=memory_search.rowid
                JOIN memories ON memories.memory_id=k.memory_id
                WHERE memory_search MATCH $match AND memories.servant_id=$servant AND {EnabledScope}
                """, ("$match", match), ("$servant", scope.ServantId), ("$project", scope.ProjectId));
            using var rows = cmd.ExecuteReader();
            while (rows.Read()) ranks.Add(rows.GetString(0), rows.GetDouble(1));
        }
        tx.Commit();
        return (snapshot, ranks);
    }

    private static MemoryRecallSnapshot ScopedSnapshot(SqliteConnection db, SqliteTransaction tx, MemoryScope scope) =>
        new(State(db, tx).Revision, Memories(db, tx, scope.ServantId, scope));
    public void SetReplacement(string candidateId, string servantId, string memoryId, int expectedVersion)
    {
        using var db = _database.Open();
        using var tx = db.BeginTransaction();
        var c = Candidates(db, tx, servantId).SingleOrDefault(c => c.CandidateId == candidateId && c.Status == MemoryCandidateStatus.Pending);
        var memory = Memories(db, tx, servantId).SingleOrDefault(m => m.MemoryId == memoryId);
        if (c is null || memory is null || memory.ProjectId != c.ProjectId || memory.Version != expectedVersion)
            throw new MemoryReviewException("请选择同一范围内的最新记忆再确认更正。");
        Execute(db, tx, "UPDATE memory_candidates SET replaces_memory_id=$memory,expected_memory_version=$version WHERE candidate_id=$id",
            ("$memory", memoryId), ("$version", expectedVersion), ("$id", candidateId));
        Bump(db, tx);
        tx.Commit();
    }
    public StoredMemory? ReviewCandidate(string candidateId, string servantId, MemoryReviewAction action, string? editedText, DateTimeOffset reviewedAtUtc)
    {
        using var db = _database.Open();
        using var tx = db.BeginTransaction();
        var candidate = Candidates(db, tx, servantId).SingleOrDefault(c => c.CandidateId == candidateId);
        if (candidate is null) return null;
        var memories = Memories(db, tx, servantId);
        if (candidate.Status == MemoryCandidateStatus.Approved && action == MemoryReviewAction.Approve)
            return memories.FirstOrDefault(m => m.SourceCandidateId == candidateId || m.MemoryId == candidate.ReplacesMemoryId);
        if (candidate.Status != MemoryCandidateStatus.Pending && action != MemoryReviewAction.Delete) return null;
        var text = new MemoryProposal(editedText ?? candidate.Text).Text;
        string? resultId = null;
        switch (action)
        {
            case MemoryReviewAction.Approve:
                var old = memories.SingleOrDefault(m => m.MemoryId == candidate.ReplacesMemoryId);
                if (candidate.ReplacesMemoryId is not null && (old is null || old.ProjectId != candidate.ProjectId || old.Version != candidate.ExpectedMemoryVersion))
                    throw new MemoryReviewException("这条记忆已被修改，请重新确认。");
                if (memories.Any(m => m.MemoryId != old?.MemoryId && m.ProjectId == candidate.ProjectId && MemoryTextIdentity.Normalize(m.Text) == MemoryTextIdentity.Normalize(text)))
                    throw new MemoryReviewException("同一范围中已有相同记忆，请拒绝重复候选。");
                if (!FitsCapacity(memories, old is null ? 1 : 0, text.Length - (old?.Text.Length ?? 0)))
                    throw new MemoryReviewException("记忆容量已满，请先管理或合并现有记忆。");
                resultId = old?.MemoryId ?? "memory-" + Guid.NewGuid().ToString("N");
                var source = candidate.Source;
                var args = new (string, object?)[] { ("$id", resultId), ("$servant", servantId), ("$text", text), ("$candidate", candidateId),
                    ("$now", reviewedAtUtc.ToString("O")), ("$project", candidate.ProjectId), ("$key", source?.Key), ("$fingerprint", source?.SourceFingerprint),
                    ("$conversation", source?.ConversationId), ("$message", source?.MessageId), ("$origin", source?.OccurredAtUtc.ToString("O")),
                    ("$kind", (source?.Kind ?? MemoryEvidenceKind.UnknownLegacy).ToString()), ("$version", old?.Version), ("$label", source?.ProjectLabel) };
                if (old is null)
                    InsertMemory(db, tx, resultId, new(servantId, candidate.ProjectId), text, candidateId,
                        reviewedAtUtc, source, candidate.ContentType, MemoryVerification.Confirmed);
                else if (Execute(db, tx, """
                    UPDATE memories SET memory_text=$text,version=version+1,updated_at_utc=$now,source_candidate_id=$candidate,verification='Confirmed',
                      source_key=$key,source_fingerprint=$fingerprint,origin_conversation_id=$conversation,origin_message_id=$message,
                      origin_at_utc=$origin,evidence_kind=$kind,origin_project_label=$label
                    WHERE memory_id=$id AND servant_id=$servant AND project_id IS $project AND version=$version
                    """, args) != 1) throw new MemoryReviewException("这条记忆已被修改，请重新确认。");
                Execute(db, tx, "DELETE FROM memory_evidence WHERE memory_id=$id", ("$id", resultId));
                Execute(db, tx, "UPDATE memory_evidence SET memory_id=$id,candidate_id=NULL WHERE candidate_id=$candidate",
                    ("$id", resultId), ("$candidate", candidateId));
                UpdateCandidate(db, tx, candidateId, "approved", text, reviewedAtUtc);
                break;
            case MemoryReviewAction.Edit:
                if (editedText is null) throw new ArgumentException("Edited text is required.");
                UpdateCandidate(db, tx, candidateId, "pending", text, reviewedAtUtc);
                break;
            case MemoryReviewAction.Reject:
                UpdateCandidate(db, tx, candidateId, "rejected", candidate.Text, reviewedAtUtc);
                if (candidate.Source is { } rejected) Receipt(db, tx, rejected.Key, "rejected");
                break;
            case MemoryReviewAction.Delete:
                if (candidate.Source is { } deleted) Receipt(db, tx, deleted.Key, "deleted");
                Execute(db, tx, "DELETE FROM memory_candidates WHERE candidate_id=$id", ("$id", candidateId));
                Rotate(db, tx);
                break;
            default: throw new ArgumentOutOfRangeException(nameof(action));
        }
        Bump(db, tx);
        var result = resultId is null ? null : Memories(db, tx, servantId).Single(m => m.MemoryId == resultId);
        tx.Commit();
        return result;
    }
    public void ReviewMemory(string memoryId, string servantId, MemoryReviewAction action, string? editedText, DateTimeOffset updatedAtUtc, int? expectedVersion = null)
    {
        using var db = _database.Open();
        using var tx = db.BeginTransaction();
        var memories = Memories(db, tx, servantId);
        var old = memories.SingleOrDefault(m => m.MemoryId == memoryId);
        if (old is null)
        {
            if (action == MemoryReviewAction.Confirm)
                throw new MemoryReviewException("这条自动记录已不存在，请刷新列表。");
            return;
        }
        if (expectedVersion is not null && old.Version != expectedVersion) throw new MemoryReviewException("这条记忆已被修改，请重新确认。");
        if (action == MemoryReviewAction.Delete)
        {
            var key = Scalar(db, tx, "SELECT source_key FROM memories WHERE memory_id=$id", ("$id", memoryId)) as string;
            if (key is not null) Receipt(db, tx, key, "deleted");
            Execute(db, tx, """
                UPDATE memory_ingestions SET status='deleted' WHERE source_key IN (
                    SELECT source_key FROM memory_evidence WHERE memory_id=$id)
                """, ("$id", memoryId));
            Execute(db, tx, "DELETE FROM memories WHERE memory_id=$id", ("$id", memoryId));
            Rotate(db, tx);
        }
        else if (action == MemoryReviewAction.Confirm)
        {
            if (expectedVersion is null || old.Verification != MemoryVerification.Recorded || !old.SourceAvailable)
                throw new MemoryReviewException("请刷新并核对这条记录及其来源后再确认。");
            Execute(db, tx, "UPDATE memories SET verification='Confirmed',version=version+1,updated_at_utc=$now WHERE memory_id=$id",
                ("$now", updatedAtUtc.ToString("O")), ("$id", memoryId));
        }
        else
        {
            if (action == MemoryReviewAction.Edit && old.Verification == MemoryVerification.Recorded)
                throw new MemoryReviewException("请先确认这条自动记录，再编辑正文。");
            var text = action == MemoryReviewAction.Edit ? new MemoryProposal(editedText ?? "").Text : old.Text;
            if (action is not (MemoryReviewAction.Edit or MemoryReviewAction.Approve or MemoryReviewAction.Disable)) throw new ArgumentOutOfRangeException(nameof(action));
            if (!FitsCapacity(memories, 0, text.Length - old.Text.Length)) throw new MemoryReviewException("记忆容量已满，请缩短或合并内容。");
            if (memories.Any(m => m.MemoryId != old.MemoryId && m.ProjectId == old.ProjectId && MemoryTextIdentity.Normalize(m.Text) == MemoryTextIdentity.Normalize(text)))
                throw new MemoryReviewException("同一范围中已有相同记忆。");
            Execute(db, tx, "UPDATE memories SET memory_text=$text,is_enabled=$enabled,version=version+1,updated_at_utc=$now WHERE memory_id=$id",
                ("$text", text), ("$enabled", action == MemoryReviewAction.Approve || (action == MemoryReviewAction.Edit && old.IsEnabled) ? 1 : 0),
                ("$now", updatedAtUtc.ToString("O")), ("$id", memoryId));
            if (action == MemoryReviewAction.Disable) Rotate(db, tx);
        }
        Bump(db, tx);
        tx.Commit();
    }
    private static bool FitsCapacity(IReadOnlyList<StoredMemory> items, int addedItems, int addedChars) =>
        (addedItems <= 0 || items.Count + addedItems <= MemorySettings.MaxItemsPerServant) &&
        (addedChars <= 0 || items.Sum(m => m.Text.Length) + addedChars <= MemorySettings.MaxCharsPerServant);
    private static void Bump(SqliteConnection db, SqliteTransaction tx) => Execute(db, tx, "UPDATE memory_write_state SET revision=revision+1 WHERE singleton_id=1");
    private static void Receipt(SqliteConnection db, SqliteTransaction tx, string key, string status) => Execute(db, tx,
        "UPDATE memory_ingestions SET status=$status,updated_at_utc=$now WHERE source_key=$key", ("$status", status), ("$key", key), ("$now", DateTimeOffset.UtcNow.ToString("O")));
    private static void UpdateCandidate(SqliteConnection db, SqliteTransaction tx, string id, string status, string text, DateTimeOffset now) => Execute(db, tx,
        "UPDATE memory_candidates SET candidate_text=$text,status=$status,reviewed_at_utc=$now WHERE candidate_id=$id", ("$id", id), ("$status", status), ("$text", text), ("$now", now.ToString("O")));
    private static IReadOnlyList<MemoryCandidate> Candidates(SqliteConnection db, SqliteTransaction? tx, string servant)
    {
        using var cmd = Command(db, tx, """
            SELECT candidate_id,conversation_id,candidate_text,created_at_utc,source_message_id,appearance_id,status,project_id,
              source_fingerprint,evidence_kind,origin_at_utc,replaces_memory_id,expected_memory_version,source_project_label,content_type
            FROM memory_candidates WHERE servant_id=$servant ORDER BY created_at_utc,candidate_id
            """, ("$servant", servant));
        using var r = cmd.ExecuteReader();
        var items = new List<MemoryCandidate>();
        while (r.Read())
        {
            var project = Optional(r, 7);
            var source = Optional(r, 8) is { } fingerprint && Optional(r, 4) is { } message && Optional(r, 10) is { } at
                ? new MemorySource(new(servant, project), r.GetString(1), message, fingerprint, Utc(at), Enum.Parse<MemoryEvidenceKind>(r.GetString(9)), Optional(r, 13)) : null;
            items.Add(new(r.GetString(0), servant, r.GetString(1), r.GetString(2), Utc(r.GetString(3)), Optional(r, 4), Optional(r, 5),
                Enum.Parse<MemoryCandidateStatus>(r.GetString(6), true), project, source, Optional(r, 11), r.IsDBNull(12) ? null : r.GetInt32(12),
                Enum.Parse<MemoryContentType>(r.GetString(14))));
        }
        return items;
    }
    private static IReadOnlyList<StoredMemory> Memories(SqliteConnection db, SqliteTransaction? tx, string servant, MemoryScope? scope = null)
    {
        using var cmd = Command(db, tx, $"""
            SELECT memory_id,memory_text,is_enabled,created_at_utc,updated_at_utc,source_candidate_id,project_id,version,
              origin_conversation_id,origin_message_id,source_fingerprint,origin_at_utc,evidence_kind,
              EXISTS(SELECT 1 FROM chat_messages m WHERE m.message_id=memories.origin_message_id AND m.conversation_id=memories.origin_conversation_id AND m.servant_id=memories.servant_id)
                AND NOT EXISTS(SELECT 1 FROM memory_evidence e LEFT JOIN chat_messages m ON m.message_id=e.message_id
                  AND m.conversation_id=e.conversation_id AND m.servant_id=memories.servant_id AND m.status='completed'
                  WHERE e.memory_id=memories.memory_id AND m.message_id IS NULL),
              origin_project_label,content_type,verification,previous_memory_id,relation,
              EXISTS(SELECT 1 FROM memories next WHERE next.previous_memory_id=memories.memory_id AND next.relation='Transition'
                AND next.is_enabled=1 AND next.servant_id=memories.servant_id AND next.project_id IS memories.project_id
                AND (next.verification='Confirmed' OR EXISTS(SELECT 1 FROM chat_messages m WHERE m.message_id=next.origin_message_id)))
            FROM memories WHERE servant_id=$servant AND ($scoped=0 OR ({EnabledScope})) ORDER BY updated_at_utc DESC,memory_id
            """, ("$servant", servant), ("$scoped", scope is null ? 0 : 1), ("$project", scope?.ProjectId));
        using var r = cmd.ExecuteReader();
        var items = new List<StoredMemory>();
        while (r.Read())
        {
            var project = Optional(r, 6);
            var source = Optional(r, 8) is { } conversation && Optional(r, 9) is { } message && Optional(r, 10) is { } fingerprint && Optional(r, 11) is { } at
                ? new MemorySource(new(servant, project), conversation, message, fingerprint, Utc(at), Enum.Parse<MemoryEvidenceKind>(r.GetString(12)), Optional(r, 14)) : null;
            items.Add(new StoredMemory(r.GetString(0), servant, r.GetString(1), r.GetInt32(2) == 1, Utc(r.GetString(3)), Utc(r.GetString(4)),
                Optional(r, 5), project, r.GetInt32(7), source, r.GetInt32(13) == 1,
                Enum.Parse<MemoryContentType>(r.GetString(15)), Enum.Parse<MemoryVerification>(r.GetString(16)))
            {
                PreviousMemoryId = Optional(r, 17),
                Relation = r.IsDBNull(18) ? null : Enum.Parse<MemoryOperation>(r.GetString(18)),
                HasNewerState = r.GetInt32(19) == 1,
            });
        }
        return items;
    }
    private static string? Optional(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    private static DateTimeOffset Utc(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    private static SqliteCommand Command(SqliteConnection db, SqliteTransaction? tx, string sql, params (string Key, object? Value)[] args)
    {
        var cmd = db.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = sql;
        foreach (var (key, value) in args) cmd.Parameters.AddWithValue(key, value ?? DBNull.Value);
        return cmd;
    }
    private static int Execute(SqliteConnection db, SqliteTransaction? tx, string sql, params (string Key, object? Value)[] args)
    { using var cmd = Command(db, tx, sql, args); return cmd.ExecuteNonQuery(); }
    private static object? Scalar(SqliteConnection db, SqliteTransaction? tx, string sql, params (string Key, object? Value)[] args)
    { using var cmd = Command(db, tx, sql, args); return cmd.ExecuteScalar(); }
}
