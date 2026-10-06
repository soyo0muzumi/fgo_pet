using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FgoPet.App.Dialogue;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Portraits;
using FgoPet.Extensibility;
using FgoPet.Infrastructure.Persistence;
using FgoPet.Kernel.Agent;
using FgoPet.Kernel.Conversation;
using FgoPet.Platform.Secrets;
using Microsoft.Data.Sqlite;

namespace FgoPet.Infrastructure.Dialogue;

/// <summary>Durable, protected acceptance and one-transaction publication of native-agent finals.</summary>
public sealed class SqliteAgentFinalDeliveryStore : IAgentFinalDeliveryStore
{
    private const int MaxPayloadBytes = 256 * 1024;
    private const int MaxRows = 256;
    private const long MaxProtectedBytes = 16 * 1024 * 1024;
    private const string SchemaProbeRunId = "native-agent-delivery-schema-probe";
    private static readonly JsonSerializerOptions Json = new()
    {
        MaxDepth = 48,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
    private static readonly Migration[] Migrations = [new(1, """
        CREATE TABLE agent_final_deliveries(
          delivery_id TEXT PRIMARY KEY,
          run_key TEXT NOT NULL UNIQUE REFERENCES agent_runs(run_key) ON DELETE CASCADE,
          conversation_id TEXT NOT NULL REFERENCES conversations(conversation_id) ON DELETE CASCADE,
          root_message_id TEXT NOT NULL REFERENCES chat_messages(message_id) ON DELETE CASCADE,
          role_id TEXT NOT NULL,
          project_id TEXT NULL,
          logical_fingerprint TEXT NOT NULL CHECK(length(logical_fingerprint)=64),
          protected_payload BLOB NOT NULL CHECK(length(protected_payload)>0 AND length(protected_payload)<=262144),
          accepted_at_utc TEXT NOT NULL,
          delivered_at_utc TEXT NULL,
          assistant_message_id TEXT NULL,
          observers_claimed INTEGER NOT NULL DEFAULT 0 CHECK(observers_claimed IN (0,1)),
          CHECK((delivered_at_utc IS NULL AND assistant_message_id IS NULL AND observers_claimed=0)
             OR (delivered_at_utc IS NOT NULL AND assistant_message_id IS NOT NULL)));
        CREATE INDEX ix_agent_final_deliveries_pending
          ON agent_final_deliveries(accepted_at_utc,delivery_id) WHERE delivered_at_utc IS NULL;
        """)];

    private readonly RuntimeDatabase _database;
    private readonly IProtectedStateProtector _protector;
    private readonly IAgentRunStore _runs;
    private readonly object _schemaGate = new();
    private bool _schemaReady;

    public SqliteAgentFinalDeliveryStore(RuntimeDatabase database, IProtectedStateProtector protector, IAgentRunStore runs)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _protector = protector ?? throw new ArgumentNullException(nameof(protector));
        _runs = runs ?? throw new ArgumentNullException(nameof(runs));
    }

    public bool TryAccept(AcceptedAgentFinal accepted)
    {
        ArgumentNullException.ThrowIfNull(accepted);
        return Safe(() => TryAcceptCore(accepted));
    }

    public AgentFinalDelivery? Publish(ToolScope scope, string deliveryId)
    {
        if (!ValidScope(scope) || !ValidId(deliveryId)) return null;
        return Safe(() => PublishCore(scope, deliveryId));
    }

    public IReadOnlyList<AcceptedAgentFinal> ReadPending(int limit = 100)
    {
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        return Safe(() => ReadPendingCore(limit));
    }

    public bool TryClaimObservers(ToolScope scope, string deliveryId)
    {
        if (!ValidScope(scope) || !ValidId(deliveryId)) return false;
        return Safe(() => TryClaimObserversCore(scope, deliveryId));
    }

    private bool TryAcceptCore(AcceptedAgentFinal accepted)
    {
        if (!ValidAcceptedShape(accepted)) return false;
        var key = RunKey(accepted.Identity.RunId);
        var checkpoint = LoadRun(accepted.Identity.RunId);
        if (!MatchesCompletedRun(checkpoint, accepted) || !TryBuildProjection(accepted, out _)) return false;

        var plaintext = JsonSerializer.SerializeToUtf8Bytes(accepted, Json);
        try
        {
            if (plaintext.Length > MaxPayloadBytes) return false;
            var logicalFingerprint = LogicalFingerprint(accepted);
            using var connection = _database.Open();
            using var transaction = connection.BeginTransaction(deferred: false);

            if (!ScopeIsCurrent(connection, transaction, accepted, key)) return false;

            var existing = ReadByRunKey(connection, transaction, key);
            if (existing is not null)
            {
                var saved = Decode(existing);
                return existing.DeliveryId == accepted.DeliveryId
                    && existing.LogicalFingerprint == logicalFingerprint
                    && LogicalFingerprint(saved) == logicalFingerprint;
            }

            if (ReadByDeliveryId(connection, transaction, accepted.DeliveryId) is not null) return false;

            var protectedPayload = _protector.Protect(plaintext, Purpose(accepted.DeliveryId));
            try
            {
                if (protectedPayload.Length is < 1 or > MaxPayloadBytes) return false;
                using (var quota = connection.CreateCommand())
                {
                    quota.Transaction = transaction;
                    quota.CommandText = "SELECT COUNT(*),COALESCE(SUM(length(protected_payload)),0) FROM agent_final_deliveries";
                    using var reader = quota.ExecuteReader();
                    reader.Read();
                    if (reader.GetInt64(0) >= MaxRows || reader.GetInt64(1) + protectedPayload.Length > MaxProtectedBytes) return false;
                }

                using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO agent_final_deliveries(
                      delivery_id,run_key,conversation_id,root_message_id,role_id,project_id,
                      logical_fingerprint,protected_payload,accepted_at_utc)
                    VALUES($delivery,$key,$conversation,$root,$role,$project,$fingerprint,$payload,$accepted)
                    """;
                insert.Parameters.AddWithValue("$delivery", accepted.DeliveryId);
                insert.Parameters.AddWithValue("$key", key);
                insert.Parameters.AddWithValue("$conversation", accepted.Identity.Scope.ConversationId);
                insert.Parameters.AddWithValue("$root", accepted.Identity.RootUserMessageId);
                insert.Parameters.AddWithValue("$role", accepted.Identity.Scope.RoleId);
                insert.Parameters.AddWithValue("$project", (object?)accepted.Identity.Scope.ProjectId ?? DBNull.Value);
                insert.Parameters.AddWithValue("$fingerprint", logicalFingerprint);
                insert.Parameters.AddWithValue("$payload", protectedPayload);
                insert.Parameters.AddWithValue("$accepted", accepted.AcceptedAt.ToString("O"));
                try { insert.ExecuteNonQuery(); }
                catch (SqliteException error) when (error.SqliteErrorCode == 19) { return false; }
                transaction.Commit();
                return true;
            }
            finally { CryptographicOperations.ZeroMemory(protectedPayload); }
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    private AgentFinalDelivery? PublishCore(ToolScope scope, string deliveryId)
    {
        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        var row = ReadByDeliveryId(connection, transaction, deliveryId);
        if (row is null || row.ConversationId != scope.ConversationId || row.RoleId != scope.RoleId || row.ProjectId != scope.ProjectId)
            return null;
        var accepted = Decode(row);
        var checkpoint = LoadRun(accepted.Identity.RunId);
        if (!MatchesCompletedRun(checkpoint, accepted)
            || accepted.Identity.Scope != scope
            || !ScopeIsCurrent(connection, transaction, accepted, row.RunKey)) return null;

        if (row.DeliveredAtUtc is not null)
        {
            var prior = ReadPublishedAssistant(connection, transaction, row, accepted);
            return prior is null ? null : new AgentFinalDelivery(accepted, prior);
        }

        if (!TryBuildProjection(accepted, out var clarificationMessages)) return null;
        using (var next = connection.CreateCommand())
        {
            next.Transaction = transaction;
            next.CommandText = "SELECT COALESCE(MAX(sequence),0) FROM chat_messages WHERE conversation_id=$conversation";
            next.Parameters.AddWithValue("$conversation", scope.ConversationId);
            var maximum = Convert.ToInt64(next.ExecuteScalar(), CultureInfo.InvariantCulture);
            if (maximum > int.MaxValue - clarificationMessages.Length - 1) return null;
            var sequence = (int)maximum + 1;
            using var latest = connection.CreateCommand();
            latest.Transaction = transaction;
            latest.CommandText = "SELECT MAX(created_at_utc) FROM chat_messages WHERE conversation_id=$conversation";
            latest.Parameters.AddWithValue("$conversation", scope.ConversationId);
            var latestStored = latest.ExecuteScalar() as string;
            var publishedAt = DateTimeOffset.UtcNow;
            if (accepted.AcceptedAt > publishedAt) publishedAt = accepted.AcceptedAt;
            if (latestStored is not null && ParseUtc(latestStored) > publishedAt) publishedAt = ParseUtc(latestStored);
            foreach (var projection in clarificationMessages)
            {
                SqliteConversationRepository.Append(connection, transaction, new ChatMessage(
                    projection.MessageId, scope.ConversationId, scope.RoleId, projection.Role,
                    projection.Text, ChatMessageStatus.Completed, publishedAt, accepted.ContentContext, sequence++));
            }

            var assistant = new ChatMessage(StableMessageId(deliveryId, "assistant"), scope.ConversationId,
                scope.RoleId, ChatMessageRole.Assistant, accepted.Output.Text, ChatMessageStatus.Completed,
                publishedAt, accepted.ContentContext, sequence);
            SqliteConversationRepository.Append(connection, transaction, assistant);

            using var delivered = connection.CreateCommand();
            delivered.Transaction = transaction;
            delivered.CommandText = """
                UPDATE agent_final_deliveries SET delivered_at_utc=$delivered,assistant_message_id=$assistant
                WHERE delivery_id=$delivery AND delivered_at_utc IS NULL AND observers_claimed=0
                """;
            delivered.Parameters.AddWithValue("$delivered", publishedAt.ToString("O"));
            delivered.Parameters.AddWithValue("$assistant", assistant.MessageId);
            delivered.Parameters.AddWithValue("$delivery", deliveryId);
            if (delivered.ExecuteNonQuery() != 1) return null;
            transaction.Commit();
            return new AgentFinalDelivery(accepted, assistant);
        }
    }

    private IReadOnlyList<AcceptedAgentFinal> ReadPendingCore(int limit)
    {
        List<DeliveryRow> rows;
        using (var connection = _database.Open())
        using (var transaction = connection.BeginTransaction())
        using (var query = connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = """
                SELECT delivery_id,run_key,conversation_id,root_message_id,role_id,project_id,
                       logical_fingerprint,protected_payload,accepted_at_utc,delivered_at_utc,
                       assistant_message_id,observers_claimed
                FROM agent_final_deliveries WHERE delivered_at_utc IS NULL
                ORDER BY accepted_at_utc,delivery_id LIMIT $scanLimit
                """;
            query.Parameters.AddWithValue("$scanLimit", MaxRows + 1);
            using var reader = query.ExecuteReader();
            rows = [];
            while (reader.Read()) rows.Add(ReadRow(reader));
            if (rows.Count > MaxRows) throw new AgentStateException("AGENT_FINAL_DELIVERY_UNREADABLE");

            var result = new List<AcceptedAgentFinal>(rows.Count);
            foreach (var row in rows)
            {
                var accepted = Decode(row);
                if (!MatchesCompletedRun(LoadRun(accepted.Identity.RunId), accepted)
                    || !TryBuildProjection(accepted, out _))
                    throw new AgentStateException("AGENT_FINAL_DELIVERY_UNREADABLE");
                if (ScopeIsCurrent(connection, transaction, accepted, row.RunKey)) result.Add(accepted);
            }
            return result.Take(limit).ToList().AsReadOnly();
        }
    }

    private bool TryClaimObserversCore(ToolScope scope, string deliveryId)
    {
        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        var row = ReadByDeliveryId(connection, transaction, deliveryId);
        if (row?.DeliveredAtUtc is null || row.ConversationId != scope.ConversationId
            || row.RoleId != scope.RoleId || row.ProjectId != scope.ProjectId) return false;
        var accepted = Decode(row);
        if (!MatchesCompletedRun(LoadRun(accepted.Identity.RunId), accepted)
            || accepted.Identity.Scope != scope
            || !ScopeIsCurrent(connection, transaction, accepted, row.RunKey)
            || ReadPublishedAssistant(connection, transaction, row, accepted) is null) return false;

        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE agent_final_deliveries SET observers_claimed=1
            WHERE delivery_id=$delivery AND delivered_at_utc IS NOT NULL AND observers_claimed=0
            """;
        update.Parameters.AddWithValue("$delivery", deliveryId);
        if (update.ExecuteNonQuery() != 1) return false;
        transaction.Commit();
        return true;
    }

    private AgentRunCheckpoint? LoadRun(string runId) =>
        _runs.LoadAsync(runId, CancellationToken.None).AsTask().GetAwaiter().GetResult();

    private static bool MatchesCompletedRun(AgentRunCheckpoint? checkpoint, AcceptedAgentFinal accepted)
    {
        if (checkpoint is null || checkpoint.Snapshot is null
            || checkpoint.Snapshot.Status != AgentRunStatus.Completed
            || checkpoint.Snapshot.Identity != accepted.Identity
            || checkpoint.Snapshot.CompletedAt is null
            || checkpoint.FinalDeliveryId != accepted.DeliveryId
            || string.IsNullOrWhiteSpace(checkpoint.FinalText)
            || checkpoint.FinalText.Length > 12_000
            || !ValidUnicode(checkpoint.FinalText)
            || checkpoint.ResolvedInputs.IsDefault
            || accepted.Source.UserMessageId != accepted.Identity.RootUserMessageId
            || !SameClarifications(checkpoint.ResolvedInputs, accepted.Clarifications)) return false;

        var raw = checkpoint.FinalText;
        if (raw.TrimStart().StartsWith('{'))
        {
            try
            {
                using var document = JsonDocument.Parse(raw);
                if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
            }
            catch (JsonException) { return false; }
        }

        try
        {
            var output = StructuredOutputValidator.Validate(raw, ExpressionSemanticKeys.Core.ToHashSet(StringComparer.Ordinal));
            return output == accepted.Output;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or JsonException)
        {
            return false;
        }
    }

    private bool ScopeIsCurrent(SqliteConnection connection, SqliteTransaction transaction,
        AcceptedAgentFinal accepted, string runKey)
    {
        var identity = accepted.Identity;
        if (accepted.ContentContext.ServantId != identity.Scope.RoleId
            || accepted.Source.UserMessageId != identity.RootUserMessageId
            || !UpperHash(accepted.Source.Fingerprint)) return false;

        using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = """
            SELECT r.conversation_id,r.root_message_id,r.role_id,r.project_id,r.status,r.completed_at_utc,
                   c.servant_id,c.project_id,c.status,
                   b.servant_id,b.package_id,b.package_version,b.appearance_id,b.persona_version,b.knowledge_version,
                   m.servant_id,m.role,m.status,m.text,m.created_at_utc
            FROM agent_runs r
            JOIN conversations c ON c.conversation_id=r.conversation_id
            JOIN content_bindings b ON b.binding_id=c.current_binding_id
            JOIN chat_messages m ON m.message_id=r.root_message_id AND m.conversation_id=r.conversation_id
            WHERE r.run_key=$key
            """;
        query.Parameters.AddWithValue("$key", runKey);
        using var reader = query.ExecuteReader();
        if (!reader.Read()
            || reader.GetString(0) != identity.Scope.ConversationId
            || reader.GetString(1) != identity.RootUserMessageId
            || reader.GetString(2) != identity.Scope.RoleId
            || ReadNullableString(reader, 3) != identity.Scope.ProjectId
            || reader.GetInt32(4) != (int)AgentRunStatus.Completed || reader.IsDBNull(5)
            || reader.GetString(6) != identity.Scope.RoleId
            || ReadNullableString(reader, 7) != identity.Scope.ProjectId
            || reader.GetString(8) != "active"
            || reader.GetString(9) != accepted.ContentContext.ServantId
            || reader.GetString(10) != accepted.ContentContext.PackageId
            || reader.GetString(11) != accepted.ContentContext.PackageVersion
            || reader.GetString(12) != accepted.ContentContext.AppearanceId
            || reader.GetString(13) != accepted.ContentContext.PersonaVersion
            || reader.GetString(14) != accepted.ContentContext.KnowledgeVersion
            || reader.GetString(15) != identity.Scope.RoleId
            || !string.Equals(reader.GetString(16), "user", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(reader.GetString(17), "completed", StringComparison.OrdinalIgnoreCase)) return false;

        var text = reader.GetString(18);
        if (text.Length is < 1 or > 12_000) return false;
        var created = DateTimeOffset.Parse(reader.GetString(19), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        var fingerprint = SourceFingerprint(identity.RootUserMessageId, ChatMessageRole.User, text, created);
        return string.Equals(fingerprint, accepted.Source.Fingerprint, StringComparison.Ordinal);
    }

    private static ChatMessage? ReadPublishedAssistant(SqliteConnection connection, SqliteTransaction transaction,
        DeliveryRow row, AcceptedAgentFinal accepted)
    {
        if (row.AssistantMessageId is null) return null;
        var assistant = SqliteConversationRepository.LoadMessages(connection, transaction, row.ConversationId, row.RoleId)
            .SingleOrDefault(message => message.MessageId == row.AssistantMessageId);
        if (assistant is null || assistant.Role != ChatMessageRole.Assistant
            || assistant.Status != ChatMessageStatus.Completed || assistant.Text != accepted.Output.Text
            || assistant.ContentContext != accepted.ContentContext) return null;
        return assistant;
    }

    private AcceptedAgentFinal Decode(DeliveryRow row)
    {
        byte[]? plaintext = null;
        try
        {
            if (row.ProtectedPayload.Length is < 1 or > MaxPayloadBytes)
                throw new AgentStateException("AGENT_FINAL_DELIVERY_UNREADABLE");
            plaintext = _protector.Unprotect(row.ProtectedPayload, Purpose(row.DeliveryId));
            if (plaintext.Length is < 1 or > MaxPayloadBytes)
                throw new AgentStateException("AGENT_FINAL_DELIVERY_UNREADABLE");
            var accepted = JsonSerializer.Deserialize<AcceptedAgentFinal>(plaintext, Json)
                ?? throw new AgentStateException("AGENT_FINAL_DELIVERY_UNREADABLE");
            if (!ValidAcceptedShape(accepted)
                || accepted.DeliveryId != row.DeliveryId
                || RunKey(accepted.Identity.RunId) != row.RunKey
                || accepted.Identity.Scope.ConversationId != row.ConversationId
                || accepted.Identity.RootUserMessageId != row.RootMessageId
                || accepted.Identity.Scope.RoleId != row.RoleId
                || accepted.Identity.Scope.ProjectId != row.ProjectId
                || accepted.AcceptedAt.ToString("O") != row.AcceptedAtUtc.ToString("O")
                || LogicalFingerprint(accepted) != row.LogicalFingerprint
                || row.DeliveredAtUtc is null && (row.AssistantMessageId is not null || row.ObserversClaimed)
                || row.DeliveredAtUtc is not null && (row.AssistantMessageId is null
                    || row.AssistantMessageId != StableMessageId(row.DeliveryId, "assistant")))
                throw new AgentStateException("AGENT_FINAL_DELIVERY_UNREADABLE");
            return accepted;
        }
        catch (AgentStateException) { throw; }
        catch (Exception) { throw new AgentStateException("AGENT_FINAL_DELIVERY_UNREADABLE"); }
        finally { if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext); }
    }

    private static DeliveryRow? ReadByRunKey(SqliteConnection connection, SqliteTransaction transaction, string key)
    {
        using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = SelectRow + " WHERE run_key=$key";
        query.Parameters.AddWithValue("$key", key);
        using var reader = query.ExecuteReader();
        return reader.Read() ? ReadRow(reader) : null;
    }

    private static DeliveryRow? ReadByDeliveryId(SqliteConnection connection, SqliteTransaction transaction, string id)
    {
        using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = SelectRow + " WHERE delivery_id=$delivery";
        query.Parameters.AddWithValue("$delivery", id);
        using var reader = query.ExecuteReader();
        return reader.Read() ? ReadRow(reader) : null;
    }

    private const string SelectRow = """
        SELECT delivery_id,run_key,conversation_id,root_message_id,role_id,project_id,
               logical_fingerprint,protected_payload,accepted_at_utc,delivered_at_utc,
               assistant_message_id,observers_claimed
        FROM agent_final_deliveries
        """;

    private static DeliveryRow ReadRow(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
        ReadNullableString(reader, 5), reader.GetString(6), (byte[])reader.GetValue(7), ParseUtc(reader.GetString(8)),
        reader.IsDBNull(9) ? null : ParseUtc(reader.GetString(9)), ReadNullableString(reader, 10), ReadClaimed(reader, 11));

    private static bool ReadClaimed(SqliteDataReader reader, int ordinal)
    {
        var claimed = reader.GetInt32(ordinal);
        if (claimed is not (0 or 1)) throw new AgentStateException("AGENT_FINAL_DELIVERY_UNREADABLE");
        return claimed == 1;
    }

    private static string? ReadNullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static bool ValidAcceptedShape(AcceptedAgentFinal accepted)
    {
        var identity = accepted.Identity;
        return identity is not null && identity.Scope is not null
            && ValidId(identity.RunId) && ValidId(identity.RootUserMessageId)
            && ValidId(identity.ModelRevision) && ValidScope(identity.Scope)
            && identity.AuthorizationRevision >= 0
            && ValidId(accepted.DeliveryId) && accepted.Source is not null
            && ValidId(accepted.Source.UserMessageId) && UpperHash(accepted.Source.Fingerprint)
            && accepted.ContentContext is not null && accepted.Output is not null
            && accepted.Output.Text is { Length: > 0 and <= 12_000 }
            && ValidUnicode(accepted.Output.Text)
            && (accepted.Output.SuggestedFact is null || ValidSemanticText(accepted.Output.SuggestedFact.Text, 2_000))
            && Enum.IsDefined(accepted.Output.Expression)
            && accepted.AcceptedAt.Offset == TimeSpan.Zero
            && !accepted.Clarifications.IsDefault && accepted.Clarifications.Length <= 16;
    }

    private static bool TryBuildProjection(AcceptedAgentFinal accepted, out ImmutableArray<Projection> projection)
    {
        var builder = ImmutableArray.CreateBuilder<Projection>();
        var requestIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var resolved in accepted.Clarifications)
        {
            if (resolved is null || resolved.Request is null || resolved.Reply is null) return Fail(out projection);
            var request = resolved.Request;
            var reply = resolved.Reply;
            var binding = request.Binding;
            if (binding is null || binding.Identity != accepted.Identity || binding.StepNumber < 1
                || !ValidId(binding.CallId) || !ValidId(binding.RequestId) || !requestIds.Add(binding.RequestId)
                || binding.WaitingRevision < 0 || binding.ExpiresAt.Offset != TimeSpan.Zero
                || reply.RunId != accepted.Identity.RunId || reply.RequestId != binding.RequestId
                || reply.ExpectedRevision != binding.WaitingRevision || request.Questions.IsDefaultOrEmpty
                || request.Questions.Length > 3 || reply.Answers.IsDefault
                || reply.Answers.Length != request.Questions.Length) return Fail(out projection);

            var answers = new Dictionary<string, QuestionAnswer>(StringComparer.Ordinal);
            var answerLength = 0L;
            foreach (var answer in reply.Answers)
            {
                if (answer is null || !ValidQuestionId(answer.QuestionId) || !answers.TryAdd(answer.QuestionId, answer)
                    || answer.SelectedOptionIndices.IsDefault
                    || answer.Text is { } text && (text.Length > 12_000 || !ValidUnicode(text))) return Fail(out projection);
                answerLength += answer.Text?.Length ?? 0;
                if (answerLength > 12_000) return Fail(out projection);
            }
            var questionIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var question in request.Questions)
            {
                if (question is null || !ValidQuestionId(question.Id) || !questionIds.Add(question.Id)
                    || !ValidSemanticText(question.Text, 2_000) || question.Options.IsDefault
                    || question.Options.Length > 8 || !answers.TryGetValue(question.Id, out var answer)
                    || answer.SelectedOptionIndices.Length > question.Options.Length
                    || !question.AllowMultiple && answer.SelectedOptionIndices.Length > 1
                    || question.AllowMultiple && question.Options.Length == 0
                    || answer.SelectedOptionIndices.Any(index => index < 0 || index >= question.Options.Length)
                    || answer.SelectedOptionIndices.Distinct().Count() != answer.SelectedOptionIndices.Length
                    || answer.SelectedOptionIndices.Length == 0 && string.IsNullOrWhiteSpace(answer.Text))
                    return Fail(out projection);

                var optionLabels = new HashSet<string>(StringComparer.Ordinal);
                foreach (var option in question.Options)
                    if (!ValidSemanticText(option, 200) || !optionLabels.Add(option)) return Fail(out projection);
                var selectedLabels = answer.SelectedOptionIndices.Select(index => question.Options[index]).ToArray();
                answerLength += selectedLabels.Sum(label => (long)label.Length);
                if (answerLength > 12_000) return Fail(out projection);
                var answerParts = new List<string>();
                if (selectedLabels.Length > 0) answerParts.Add("选择：" + string.Join("、", selectedLabels));
                if (!string.IsNullOrEmpty(answer.Text))
                {
                    if (!ValidUnicode(answer.Text)) return Fail(out projection);
                    answerParts.Add("补充：" + answer.Text);
                }
                var answerText = string.Join('\n', answerParts);
                if (answerText.Length is < 1 or > 12_000 || !ValidUnicode(answerText)) return Fail(out projection);
                builder.Add(new(StableMessageId(accepted.DeliveryId, "question", binding.RequestId, question.Id),
                    ChatMessageRole.Assistant, question.Text));
                builder.Add(new(StableMessageId(accepted.DeliveryId, "answer", binding.RequestId, question.Id),
                    ChatMessageRole.User, answerText));
            }
        }
        if (builder.Select(item => item.MessageId).Distinct(StringComparer.Ordinal).Count() != builder.Count)
            return Fail(out projection);
        projection = builder.ToImmutable();
        return true;
    }

    private static bool Fail(out ImmutableArray<Projection> projection)
    {
        projection = [];
        return false;
    }

    private static bool SameClarifications(ImmutableArray<ResolvedUserInput> left, ImmutableArray<ResolvedUserInput> right)
    {
        if (left.IsDefault || right.IsDefault) return false;
        return JsonSerializer.SerializeToUtf8Bytes(left, Json).AsSpan()
            .SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(right, Json));
    }

    private static string LogicalFingerprint(AcceptedAgentFinal accepted)
    {
        var stable = accepted with { AcceptedAt = DateTimeOffset.UnixEpoch };
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(stable, Json)));
    }

    private static string SourceFingerprint(string messageId, ChatMessageRole role, string text, DateTimeOffset createdAtUtc) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            MessageId = messageId,
            Role = role,
            Text = text,
            CreatedAtUtc = createdAtUtc,
        }))));

    private static string StableMessageId(params string[] parts) =>
        "delivery-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\0', parts)))).ToLowerInvariant();

    private static string RunKey(string runId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(runId)));

    private static string Purpose(string deliveryId) =>
        "native-agent-final-delivery." + RunKey(deliveryId).ToLowerInvariant() + ".v1";

    private static bool ValidScope(ToolScope? scope) => scope is not null
        && ValidId(scope.ConversationId) && ValidId(scope.RoleId)
        && (scope.ProjectId is null || ValidId(scope.ProjectId));

    private static bool ValidId(string? value) => value is { Length: > 0 and <= 128 }
        && !string.IsNullOrWhiteSpace(value) && !value.Any(char.IsControl);

    private static bool UpperHash(string? value) => value is { Length: 64 }
        && value.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'F');

    private static bool ValidQuestionId(string? value) => value is { Length: > 0 and <= 64 }
        && value[0] is >= 'a' and <= 'z'
        && value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-');

    private static bool ValidSemanticText(string? value, int maximum) =>
        value is { Length: > 0 } && value.Length <= maximum && !string.IsNullOrWhiteSpace(value) && ValidUnicode(value);

    private static bool ValidUnicode(string value)
    {
        try { _ = new UTF8Encoding(false, true).GetByteCount(value); return true; }
        catch (EncoderFallbackException) { return false; }
    }

    private static DateTimeOffset ParseUtc(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private void EnsureSchema()
    {
        lock (_schemaGate)
        {
            if (_schemaReady) return;
            // RunStore owns the prerequisite table and delays its migration until its first operation.
            _ = _runs.LoadAsync(SchemaProbeRunId, CancellationToken.None).AsTask().GetAwaiter().GetResult();
            new RuntimeDatabaseMigrator(_database).MigrateModule("native-agent-delivery", Migrations);
            _schemaReady = true;
        }
    }

    private T Safe<T>(Func<T> operation)
    {
        try
        {
            EnsureSchema();
            return operation();
        }
        catch (AgentStateException) { throw; }
        catch (StateProtectionException) { throw new AgentStateException("AGENT_FINAL_DELIVERY_UNREADABLE"); }
        catch (JsonException) { throw new AgentStateException("AGENT_FINAL_DELIVERY_UNREADABLE"); }
        catch (Exception) { throw new AgentStateException("AGENT_FINAL_DELIVERY_FAILED"); }
    }

    private sealed record DeliveryRow(string DeliveryId, string RunKey, string ConversationId, string RootMessageId,
        string RoleId, string? ProjectId, string LogicalFingerprint, byte[] ProtectedPayload,
        DateTimeOffset AcceptedAtUtc, DateTimeOffset? DeliveredAtUtc, string? AssistantMessageId, bool ObserversClaimed);

    private sealed record Projection(string MessageId, ChatMessageRole Role, string Text);
}
