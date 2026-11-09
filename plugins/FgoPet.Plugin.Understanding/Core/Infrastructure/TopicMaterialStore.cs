using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FgoPet.Core.Dialogue;
using FgoPet.Infrastructure.Persistence;
using FgoPet.Understanding.Contracts;
using Microsoft.Data.Sqlite;

namespace FgoPet.Understanding;

/// <summary>Bounded, exact-scope external context. This owner never writes Understanding entries.</summary>
public sealed class TopicMaterialStore : ITopicMaterialStore, IDerivedConversationData
{
    private readonly RuntimeDatabase _database;
    private readonly TimeProvider _clock;
    private readonly int _capacity;
    private readonly object _gate = new();
    private bool _schemaReady;

    public TopicMaterialStore(RuntimeDatabase database, TimeProvider? clock = null, int capacity = 64)
    {
        ArgumentNullException.ThrowIfNull(database);
        if (capacity is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(capacity));
        _database = database;
        _clock = clock ?? TimeProvider.System;
        _capacity = capacity;
    }

    public void Add(ConversationScope scope, TopicMaterial material)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(material);
        var now = _clock.GetUtcNow();
        Validate(material, now, adding: true);
        lock (_gate)
        {
            EnsureSchema();
            using var db = _database.Open(); using var tx = db.BeginTransaction(deferred: false);
            Prune(db, tx, now);
            RememberScope(db, tx, scope);
            var source = CanonicalUrl(material.SourceUrl);
            var summary = SummaryHash(material.Summary);
            using var duplicate = Command(db, tx, """
                SELECT COUNT(*) FROM topic_materials WHERE scope=$scope AND
                (id=$id OR (topic_key=$topic AND topic_revision=$revision AND (source_key=$source OR summary_hash=$summary)))
                """, ("$scope", scope.Key), ("$id", material.Id), ("$topic", material.TopicKey),
                ("$revision", material.TopicRevision), ("$source", source), ("$summary", summary));
            // A duplicate never renews its lifetime or erases its use signal.
            if ((long)duplicate.ExecuteScalar()! == 0)
                Execute(db, tx, """
                    INSERT INTO topic_materials(scope,id,topic_key,topic_revision,source_key,summary_hash,fetched_at,expires_at,material_json)
                    VALUES($scope,$id,$topic,$revision,$source,$summary,$fetched,$expires,$json)
                    """, ("$scope", scope.Key), ("$id", material.Id), ("$topic", material.TopicKey),
                    ("$revision", material.TopicRevision), ("$source", source), ("$summary", summary), ("$fetched", material.FetchedAt.UtcTicks),
                    ("$expires", material.ExpiresAt.UtcTicks), ("$json", JsonSerializer.Serialize(material)));
            Execute(db, tx, """
                DELETE FROM topic_materials WHERE scope=$scope AND id NOT IN
                  (SELECT id FROM topic_materials WHERE scope=$scope ORDER BY fetched_at DESC,id DESC LIMIT $capacity)
                """, ("$scope", scope.Key), ("$capacity", _capacity));
            tx.Commit();
        }
    }

    public IReadOnlyList<TopicMaterial> GetActive(ConversationScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        lock (_gate)
        {
            EnsureSchema();
            using var db = _database.Open(); using var tx = db.BeginTransaction(deferred: false);
            var now = _clock.GetUtcNow(); Prune(db, tx, now);
            var result = ReadActive(db, tx, scope, now);
            tx.Commit();
            return result;
        }
    }

    public IReadOnlyList<TopicMaterial> ReadEvidence(ConversationScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (!File.Exists(_database.DatabasePath)) return [];
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _database.DatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 5
        }.ToString());
        db.Open();
        using var tx = db.BeginTransaction(deferred: true);
        if (!HasMaterialSchema(db, tx)) return [];
        return ReadActive(db, tx, scope, _clock.GetUtcNow());
    }

    public void PruneExpired()
    {
        if (!File.Exists(_database.DatabasePath)) return;
        lock (_gate)
        {
            using var db = _database.Open(); using var tx = db.BeginTransaction(deferred: false);
            if (!HasMaterialSchema(db, tx)) return;
            Prune(db, tx, _clock.GetUtcNow());
            tx.Commit();
        }
    }

    private static bool HasMaterialSchema(SqliteConnection db, SqliteTransaction tx)
    {
        using var schema = Command(db, tx, """
            SELECT EXISTS(SELECT 1 FROM sqlite_schema WHERE type='table' AND name='topic_materials')
            """);
        return (long)schema.ExecuteScalar()! == 1;
    }

    private IReadOnlyList<TopicMaterial> ReadActive(SqliteConnection db, SqliteTransaction tx, ConversationScope scope, DateTimeOffset now)
    {
        using var command = Command(db, tx, """
            SELECT material_json FROM topic_materials WHERE scope=$scope AND expires_at>$now AND fetched_at<=$now
            ORDER BY fetched_at DESC,id DESC LIMIT $capacity
            """, ("$scope", scope.Key), ("$now", now.UtcTicks), ("$capacity", _capacity));
        var result = new List<TopicMaterial>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            TopicMaterial? material;
            try { material = JsonSerializer.Deserialize<TopicMaterial>(reader.GetString(0)); }
            catch (JsonException) { throw new InvalidOperationException("TOPIC_MATERIAL_STORAGE_INVALID"); }
            if (material is null) throw new InvalidOperationException("TOPIC_MATERIAL_STORAGE_INVALID");
            try { Validate(material, now, adding: false); }
            catch (ArgumentException) { throw new InvalidOperationException("TOPIC_MATERIAL_STORAGE_INVALID"); }
            if (material.FetchedAt <= now && material.ExpiresAt > now) result.Add(material);
        }
        return result;
    }

    public void MarkUsed(ConversationScope scope, string materialId, DateTimeOffset usedAt)
    {
        ArgumentNullException.ThrowIfNull(scope);
        Text(materialId, 128);
        if (usedAt == DateTimeOffset.MinValue || usedAt > _clock.GetUtcNow())
            throw new ArgumentException("TOPIC_MATERIAL_INVALID_USE");
        lock (_gate)
        {
            EnsureSchema();
            using var db = _database.Open(); using var tx = db.BeginTransaction(deferred: false);
            Prune(db, tx, _clock.GetUtcNow());
            using var query = Command(db, tx, "SELECT material_json FROM topic_materials WHERE scope=$scope AND id=$id",
                ("$scope", scope.Key), ("$id", materialId));
            if (query.ExecuteScalar() is string json)
            {
                var material = JsonSerializer.Deserialize<TopicMaterial>(json)
                    ?? throw new InvalidOperationException("TOPIC_MATERIAL_STORAGE_INVALID");
                if (usedAt < material.FetchedAt) throw new ArgumentException("TOPIC_MATERIAL_INVALID_USE");
                if (material.LastUsedAt is null || usedAt > material.LastUsedAt)
                    Execute(db, tx, "UPDATE topic_materials SET material_json=$json WHERE scope=$scope AND id=$id",
                        ("$json", JsonSerializer.Serialize(material with { LastUsedAt = usedAt })),
                        ("$scope", scope.Key), ("$id", materialId));
            }
            tx.Commit();
        }
    }

    public bool TryReserveResearch(ConversationScope scope, string topicKey, DateTimeOffset now,
        TimeSpan refreshAfter, int dailyLimit = 6)
    {
        ValidateReservation(scope, topicKey, now, refreshAfter, dailyLimit);
        lock (_gate)
        {
            EnsureSchema();
            using var db = _database.Open(); using var tx = db.BeginTransaction(deferred: false);
            Prune(db, tx, now);
            if (!Eligible(db, tx, scope, topicKey, now, dailyLimit)) return false;
            RememberScope(db, tx, scope);
            Execute(db, tx, "INSERT INTO topic_material_research VALUES($scope,$topic,$refresh)",
                ("$scope", scope.Key), ("$topic", topicKey), ("$refresh", now.Add(refreshAfter).UtcTicks));
            Execute(db, tx, "INSERT INTO topic_material_research_attempts VALUES($scope,$at)",
                ("$scope", scope.Key), ("$at", now.UtcTicks));
            tx.Commit();
            return true;
        }
    }

    public bool CanResearch(ConversationScope scope, string topicKey, DateTimeOffset now,
        TimeSpan refreshAfter, int dailyLimit = 6)
    {
        ValidateReservation(scope, topicKey, now, refreshAfter, dailyLimit);
        lock (_gate)
        {
            EnsureSchema();
            using var db = _database.Open(); using var tx = db.BeginTransaction(deferred: true);
            // Selection does not charge, prune or renew anything. Only the dispatch reservation writes.
            return Eligible(db, tx, scope, topicKey, now, dailyLimit);
        }
    }

    private bool Eligible(SqliteConnection db, SqliteTransaction tx, ConversationScope scope,
        string topicKey, DateTimeOffset now, int dailyLimit)
    {
        using var recent = Command(db, tx, """
            SELECT COUNT(*),MAX(reserved_at) FROM topic_material_research_attempts WHERE scope=$scope AND reserved_at>$cutoff
            """, ("$scope", scope.Key), ("$cutoff", now.UtcTicks - TimeSpan.FromDays(1).Ticks));
        long attempts; long? last;
        using (var reader = recent.ExecuteReader())
        {
            reader.Read(); attempts = reader.GetInt64(0); last = reader.IsDBNull(1) ? null : reader.GetInt64(1);
        }
        if (attempts >= dailyLimit || last is { } at && now.UtcTicks - at < TimeSpan.FromMinutes(5).Ticks) return false;
        using var cooldown = Command(db, tx, """
            SELECT COUNT(*) FROM topic_material_research WHERE scope=$scope AND topic_key=$topic AND refresh_at>$now
            """, ("$scope", scope.Key), ("$topic", topicKey), ("$now", now.UtcTicks));
        if ((long)cooldown.ExecuteScalar()! != 0) return false;
        using var count = Command(db, tx,
            "SELECT COUNT(*) FROM topic_material_research WHERE scope=$scope AND refresh_at>$now",
            ("$scope", scope.Key), ("$now", now.UtcTicks));
        return (long)count.ExecuteScalar()! < _capacity;
    }

    private static void ValidateReservation(ConversationScope scope, string topicKey, DateTimeOffset now,
        TimeSpan refreshAfter, int dailyLimit)
    {
        ArgumentNullException.ThrowIfNull(scope);
        Text(topicKey, 128);
        if (refreshAfter < TimeSpan.FromHours(2) || refreshAfter > TimeSpan.FromDays(7)
            || dailyLimit is < 1 or > 6 || now < DateTimeOffset.MinValue.AddDays(1)
            || now > DateTimeOffset.MaxValue.AddDays(-7))
            throw new ArgumentException("TOPIC_MATERIAL_RESEARCH_LIMIT_INVALID");
    }

    public void DeleteScope(ConversationScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        lock (_gate)
        {
            EnsureSchema();
            using var db = _database.Open(); using var tx = db.BeginTransaction(deferred: false);
            Execute(db, tx, "DELETE FROM topic_materials WHERE scope=$scope", ("$scope", scope.Key));
            Execute(db, tx, "DELETE FROM topic_material_research WHERE scope=$scope", ("$scope", scope.Key));
            // Daily charges remain independent of forget/research data deletion, preventing budget reset.
            tx.Commit();
        }
    }

    private void EnsureSchema()
    {
        if (_schemaReady) return;
        new RuntimeDatabaseMigrator(_database).MigrateModule("topic-material", [new(1, """
            CREATE TABLE topic_material_scopes(scope TEXT PRIMARY KEY,role_id TEXT NOT NULL);
            CREATE TABLE topic_materials(scope TEXT NOT NULL,id TEXT NOT NULL,topic_key TEXT NOT NULL,topic_revision INTEGER NOT NULL,
              source_key TEXT NOT NULL,summary_hash TEXT NOT NULL,fetched_at INTEGER NOT NULL,expires_at INTEGER NOT NULL,
              material_json TEXT NOT NULL,PRIMARY KEY(scope,id));
            CREATE INDEX ix_topic_material_expiry ON topic_materials(expires_at);
            CREATE TABLE topic_material_research(scope TEXT NOT NULL,topic_key TEXT NOT NULL,refresh_at INTEGER NOT NULL,
              PRIMARY KEY(scope,topic_key));
            CREATE TABLE topic_material_research_attempts(scope TEXT NOT NULL,reserved_at INTEGER NOT NULL,
              PRIMARY KEY(scope,reserved_at));
            """)]);
        _schemaReady = true;
    }

    private static void Prune(SqliteConnection db, SqliteTransaction tx, DateTimeOffset now)
    {
        Execute(db, tx, "DELETE FROM topic_materials WHERE expires_at<=$now", ("$now", now.UtcTicks));
        Execute(db, tx, "DELETE FROM topic_material_research WHERE refresh_at<=$now", ("$now", now.UtcTicks));
        Execute(db, tx, "DELETE FROM topic_material_research_attempts WHERE reserved_at<=$cutoff",
            ("$cutoff", now.UtcTicks - TimeSpan.FromDays(1).Ticks));
        Execute(db, tx, """
            DELETE FROM topic_material_scopes WHERE scope NOT IN (SELECT scope FROM topic_materials)
              AND scope NOT IN (SELECT scope FROM topic_material_research)
              AND scope NOT IN (SELECT scope FROM topic_material_research_attempts)
            """);
    }

    private static void Validate(TopicMaterial material, DateTimeOffset now, bool adding)
    {
        Text(material.Id, 128); Text(material.TopicKey, 128); Text(material.Summary, 1200, multiline: true);
        Text(material.RelevanceReason, 600, multiline: true); Text(material.SourceTitle, 256); Text(material.SourceUrl, 2048);
        if (material.TopicRevision < 1 || !Enum.IsDefined(material.Scope)
            || material.FetchedAt == DateTimeOffset.MinValue || material.ExpiresAt == DateTimeOffset.MaxValue
            || material.ExpiresAt <= material.FetchedAt || material.ExpiresAt - material.FetchedAt > TimeSpan.FromDays(7)
            || adding && (material.FetchedAt > now || material.ExpiresAt <= now)
            || material.LastUsedAt is { } used && (used < material.FetchedAt || adding && used > now))
            throw new ArgumentException("TOPIC_MATERIAL_INVALID");
        _ = CanonicalUrl(material.SourceUrl);
    }

    private static void Text(string? value, int maximum, bool multiline = false)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum
            || value.Any(c => char.IsControl(c) && !(multiline && c is '\r' or '\n' or '\t'))
            || !ValidUnicode(value)) throw new ArgumentException("TOPIC_MATERIAL_INVALID_TEXT");
    }

    private static bool ValidUnicode(string value)
    {
        for (var index = 0; index < value.Length; index++)
            if (char.IsSurrogate(value[index]) && (!char.IsHighSurrogate(value[index])
                || ++index >= value.Length || !char.IsLowSurrogate(value[index]))) return false;
        return true;
    }

    private static string CanonicalUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
            || uri.UserInfo.Length != 0 || uri.IsLoopback || !PublicHost(uri.DnsSafeHost))
            throw new ArgumentException("TOPIC_MATERIAL_INVALID_SOURCE");
        return uri.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped);
    }

    private static bool PublicHost(string host)
    {
        if (IPAddress.TryParse(host, out var address))
        {
            if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
            var bytes = address.GetAddressBytes();
            if (bytes.Length == 4)
                return bytes[0] is > 0 and < 224 && bytes[0] is not (10 or 127)
                    && !(bytes[0] == 169 && bytes[1] == 254) && !(bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
                    && !(bytes[0] == 192 && bytes[1] == 168) && !(bytes[0] == 100 && bytes[1] is >= 64 and <= 127);
            return !address.IsIPv6LinkLocal && !address.IsIPv6Multicast && !address.IsIPv6SiteLocal
                && !IPAddress.IsLoopback(address) && !address.Equals(IPAddress.IPv6Any)
                && (bytes[0] & 0xfe) != 0xfc;
        }
        var normalized = host.TrimEnd('.').ToLowerInvariant();
        return normalized.Contains('.') && !normalized.EndsWith(".localhost", StringComparison.Ordinal)
            && !normalized.EndsWith(".local", StringComparison.Ordinal) && !normalized.EndsWith(".internal", StringComparison.Ordinal)
            && !normalized.EndsWith(".lan", StringComparison.Ordinal)
            && Uri.CheckHostName(normalized) == UriHostNameType.Dns;
    }

    private static string SummaryHash(string summary) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        string.Join(' ', summary.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant())));
    /// <summary>Material has no conversation ownership, so forgetting a root conservatively clears the role's cache.</summary>
    public void ForgetConversation(string conversationId, string roleId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(roleId);
        Forget(roleId);
    }
    public void ForgetAll() => Forget(null);
    private void Forget(string? roleId)
    {
        lock (_gate)
        {
            EnsureSchema();
            using var db = _database.Open(); using var tx = db.BeginTransaction(deferred: false);
            Execute(db, tx, """
                DELETE FROM topic_materials WHERE $role IS NULL OR scope IN
                  (SELECT scope FROM topic_material_scopes WHERE role_id=$role)
                """, ("$role", roleId));
            Execute(db, tx, """
                DELETE FROM topic_material_research WHERE $role IS NULL OR scope IN
                  (SELECT scope FROM topic_material_scopes WHERE role_id=$role)
                """, ("$role", roleId));
            // Hash-only daily accounting survives forget. It contains no material or topic content.
            Prune(db, tx, _clock.GetUtcNow());
            tx.Commit();
        }
    }
    public JsonElement Export() => JsonSerializer.SerializeToElement(Array.Empty<object>());
    public void NormalizeRestoredCopy(string validatedStagingDatabasePath)
    {
        var fullPath = Path.GetFullPath(validatedStagingDatabasePath);
        if (string.Equals(fullPath, _database.DatabasePath, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("TOPIC_MATERIAL_RESTORE_REQUIRES_STAGING");
        lock (_gate)
        {
            EnsureSchema();
            using var current = _database.Open(); using var currentTx = current.BeginTransaction(deferred: false);
            var attempts = new List<(string Scope, long At)>();
            using (var command = Command(current, currentTx, "SELECT scope,reserved_at FROM topic_material_research_attempts"))
            using (var reader = command.ExecuteReader())
                while (reader.Read()) attempts.Add((reader.GetString(0), reader.GetInt64(1)));
            var cooldowns = new List<(string Scope, string Topic, long Refresh)>();
            using (var command = Command(current, currentTx, "SELECT scope,topic_key,refresh_at FROM topic_material_research"))
            using (var reader = command.ExecuteReader())
                while (reader.Read()) cooldowns.Add((reader.GetString(0), reader.GetString(1), reader.GetInt64(2)));
            var scopes = new List<(string Scope, string Role)>();
            using (var command = Command(current, currentTx, "SELECT scope,role_id FROM topic_material_scopes"))
            using (var reader = command.ExecuteReader())
                while (reader.Read()) scopes.Add((reader.GetString(0), reader.GetString(1)));
            var stagedDatabase = new RuntimeDatabase(fullPath, pooling: false);
            new TopicMaterialStore(stagedDatabase, _clock, _capacity).EnsureSchema();
            using var staged = stagedDatabase.Open(); using var tx = staged.BeginTransaction(deferred: false);
            Execute(staged, tx, "DELETE FROM topic_materials");
            foreach (var (scope, at) in attempts)
                Execute(staged, tx, "INSERT OR IGNORE INTO topic_material_research_attempts VALUES($scope,$at)",
                    ("$scope", scope), ("$at", at));
            foreach (var (scope, topic, refresh) in cooldowns)
                Execute(staged, tx, """
                    INSERT INTO topic_material_research VALUES($scope,$topic,$refresh)
                    ON CONFLICT(scope,topic_key) DO UPDATE SET refresh_at=MAX(refresh_at,excluded.refresh_at)
                    """, ("$scope", scope), ("$topic", topic), ("$refresh", refresh));
            foreach (var (scope, role) in scopes)
                Execute(staged, tx, "INSERT OR IGNORE INTO topic_material_scopes VALUES($scope,$role)",
                    ("$scope", scope), ("$role", role));
            Prune(staged, tx, _clock.GetUtcNow());
            tx.Commit();
            currentTx.Commit();
        }
    }
    private static void RememberScope(SqliteConnection db, SqliteTransaction tx, ConversationScope scope) =>
        Execute(db, tx, "INSERT OR IGNORE INTO topic_material_scopes VALUES($scope,$role)",
            ("$scope", scope.Key), ("$role", scope.ServantId));
    private static SqliteCommand Command(SqliteConnection db, SqliteTransaction tx, string sql,
        params (string Name, object? Value)[] arguments)
    {
        var command = db.CreateCommand(); command.Transaction = tx; command.CommandText = sql;
        foreach (var (name, value) in arguments) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }
    private static void Execute(SqliteConnection db, SqliteTransaction tx, string sql,
        params (string Name, object? Value)[] arguments)
    { using var command = Command(db, tx, sql, arguments); command.ExecuteNonQuery(); }
}
