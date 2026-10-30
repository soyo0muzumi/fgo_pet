using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FgoPet.Core.Dialogue;
using FgoPet.Extensibility;
using FgoPet.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace FgoPet.App.Dialogue;

internal sealed record ExpressionCandidateSource(string ConversationId, string MessageId, string Fingerprint, DateTimeOffset AtUtc);
internal sealed record ExpressionCandidateScope(string RoleId, string? ProjectId);
internal sealed record ExpressionCandidate(string Id, string Situation, string Pattern,
    IReadOnlyList<ExpressionCandidateSource> Source, double Confidence, ExpressionCandidateScope Scope, string ReviewStatus);

/// <summary>Passive derived inbox only; never read by PromptComposer or the active curated library.</summary>
internal sealed class ExpressionCandidateInbox(RuntimeDatabase database) : IDerivedConversationData
{
    private const string Key = "expression:passive-inbox:v02";
    private readonly object gate = new();

    internal string Collect(ConversationSourceSnapshot source, string userText)
    {
        if (source.Kind != ConversationSourceKind.User) return "ignored_non_user";
        if (source.Scope.RoleId != "mash_kyrielight") return "ignored_role";
        if (source.Fingerprint.Length != 64 || source.Fingerprint.Any(c => !Uri.IsHexDigit(c))) return "invalid_source";
        var signal = Discover(userText);
        if (signal is null) return "no_style_signal";
        return Change(rows => {
            var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
                new { source.Scope.RoleId, source.Scope.ProjectId, signal.Value.Situation, signal.Value.Pattern }))));
            var existing = rows.FindIndex(r => r.Id == id);
            var origin = new ExpressionCandidateSource(source.Scope.ConversationId, source.MessageId, source.Fingerprint, source.OccurredAtUtc);
            if (existing >= 0)
            {
                var row = rows[existing];
                if (row.Source.Any(s => s.ConversationId == origin.ConversationId && s.MessageId == origin.MessageId)) return "duplicate_source";
                if (row.Source.Count >= 8) return "source_limit";
                rows[existing] = row with { Source = row.Source.Append(origin).ToArray(), ReviewStatus = "pending" };
                return "deduplicated";
            }
            if (rows.Count >= 256) return "inbox_limit";
            rows.Add(new(id, signal.Value.Situation, signal.Value.Pattern, [origin], 0.9,
                new(source.Scope.RoleId, source.Scope.ProjectId), "pending"));
            return "collected";
        });
    }

    private static (string Situation, string Pattern)? Discover(string text)
    {
        // Explicit user feedback only. No quotation copying, model inference or raw text storage.
        if (text.Length > 12000 || new[] { "引用", "例句", "假设", "不要学", "不要模仿", "```" }.Any(text.Contains)) return null;
        bool Has(params string[] cues) => cues.Any(c => text.Contains(c, StringComparison.Ordinal));
        if (Has("说短一点", "简短一点", "不用说这么多", "别展开", "别长篇")) return ("user_requested_brevity", "按用户本轮所需缩短表达，保留原有语义和限定；不形成长期身份规则。");
        if (Has("不用每次总结", "别总结", "不要总结")) return ("user_requested_no_recap", "本轮避免重复总结用户已知信息，不省略关键条件。");
        if (Has("别总问", "不用反复问", "别每次都问", "不用分这么细致的问")) return ("user_requested_fewer_questions", "减少不必要的延续性问题；必须澄清或授权的边界不能因此省略。");
        if (Has("只是分享", "不用给建议", "不需要建议", "别给建议")) return ("user_requested_receiving", "接住用户分享，不自动附建议或行动计划。");
        return null;
    }

    internal IReadOnlyList<ExpressionCandidate> ReadInbox(ToolScope scope) => Read().Where(r =>
        r.Scope.RoleId == scope.RoleId && r.Scope.ProjectId == scope.ProjectId).ToArray();

    internal bool Review(string id, ToolScope scope, string reviewStatus)
    {
        if (reviewStatus is not ("pending" or "reviewed" or "rejected")) throw new ArgumentException("No active-library promotion exists.");
        var changed = false;
        Change(rows => {
            var index = rows.FindIndex(r => r.Id == id && r.Scope.RoleId == scope.RoleId && r.Scope.ProjectId == scope.ProjectId);
            if (index >= 0) { rows[index] = rows[index] with { ReviewStatus = reviewStatus }; changed = true; }
            return changed ? "reviewed" : "not_found";
        });
        return changed;
    }

    public void ForgetConversation(string conversationId, string roleId) => Change(rows => {
        for (var i = rows.Count - 1; i >= 0; i--)
        {
            if (rows[i].Scope.RoleId != roleId) continue;
            var kept = rows[i].Source.Where(s => s.ConversationId != conversationId).ToArray();
            if (kept.Length == 0) rows.RemoveAt(i);
            else if (kept.Length != rows[i].Source.Count) rows[i] = rows[i] with { Source = kept, ReviewStatus = "pending" };
        }
        return "forgotten";
    });
    public void ForgetAll() => Change(rows => { rows.Clear(); return "forgotten"; });
    public JsonElement Export() => JsonSerializer.SerializeToElement(new { owner = "expression_passive_inbox_v02", candidates = Read() });
    public void NormalizeRestoredCopy(string validatedStagingDatabasePath)
    {
        using var copy = new RuntimeDatabase(validatedStagingDatabasePath, pooling: false).Open();
        using var command = copy.CreateCommand();
        command.CommandText = "DELETE FROM runtime_state WHERE state_key=$key";
        command.Parameters.AddWithValue("$key", Key);
        command.ExecuteNonQuery(); // Derived candidates require fresh source validation after restore.
    }
    private List<ExpressionCandidate> Read()
    {
        lock (gate)
        {
            using var c = database.Open();
            return Load(c, null);
        }
    }
    private static List<ExpressionCandidate> Load(SqliteConnection c, SqliteTransaction? transaction)
    {
        using var cmd = c.CreateCommand(); cmd.Transaction = transaction;
        cmd.CommandText = "SELECT state_value FROM runtime_state WHERE state_key=$key"; cmd.Parameters.AddWithValue("$key", Key);
        var raw = cmd.ExecuteScalar() as string;
        if (raw is null) return [];
        if (raw.Length > 1_048_576) throw new InvalidOperationException("EXPRESSION_INBOX_LIMIT");
        return JsonSerializer.Deserialize<List<ExpressionCandidate>>(raw) ?? throw new JsonException("EXPRESSION_INBOX_INVALID");
    }
    private string Change(Func<List<ExpressionCandidate>, string> action)
    {
        lock (gate)
        {
            using var c = database.Open(); using var tx = c.BeginTransaction();
            var rows = Load(c, tx); var result = action(rows);
            var payload = JsonSerializer.Serialize(rows);
            if (payload.Length > 1_048_576) throw new InvalidOperationException("EXPRESSION_INBOX_LIMIT");
            using var cmd = c.CreateCommand(); cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO runtime_state(state_key,state_value,updated_at_utc) VALUES($key,$value,$at) ON CONFLICT(state_key) DO UPDATE SET state_value=excluded.state_value,updated_at_utc=excluded.updated_at_utc";
            cmd.Parameters.AddWithValue("$key", Key); cmd.Parameters.AddWithValue("$value", payload);
            cmd.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O")); cmd.ExecuteNonQuery(); tx.Commit();
            return result;
        }
    }
}
