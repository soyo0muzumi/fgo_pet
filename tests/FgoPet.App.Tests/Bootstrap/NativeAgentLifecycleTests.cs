using System.Collections.Immutable;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using FgoPet.App.Bootstrap;
using FgoPet.App.Dialogue;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Portraits;
using FgoPet.Core.Todo;
using FgoPet.Dialogue.Settings;
using FgoPet.Extensibility;
using FgoPet.Infrastructure.Dialogue;
using FgoPet.Infrastructure.Persistence;
using FgoPet.Kernel.Agent;
using FgoPet.Kernel.Conversation;
using FgoPet.Platform.Windows.Secrets;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace FgoPet.App.Tests.Bootstrap;

public sealed class NativeAgentLifecycleTests
{
    private static readonly string[] OfflinePluginIds = ["documents", "firstparty.skills", "shell", "workspace"];

    [Fact]
    public void Synchronously_disposing_production_composition_does_not_block_or_throw()
    {
        using var fixture = new DatabaseFixture(migrate: false);
        var provider = BuildProvider(fixture.Database);
        _ = provider.GetRequiredService<PluginCatalog>();

        var exception = Record.Exception(provider.Dispose);

        Assert.Null(exception);
    }

    [Fact]
    public async Task Plugin_start_does_not_migrate_agent_tables_until_application_ready()
    {
        using var fixture = new DatabaseFixture(migrate: true);
        await using var provider = BuildProvider(fixture.Database);
        var runtime = provider.GetRequiredService<PluginRuntime>();
        var availability = provider.GetRequiredService<NativeAgentAvailability>();

        Assert.True((await runtime.StartAsync(default)).Succeeded);
        Assert.Equal(0, fixture.CountTable("agent_runs"));
        Assert.False(availability.IsReady);

        var failures = await runtime.NotifyApplicationReadyAsync(new(true), default);

        Assert.Empty(failures);
        Assert.Equal(1, fixture.CountTable("agent_runs"));
        Assert.True(availability.IsReady);
        Assert.All(OfflinePluginIds, id => Assert.True(runtime.IsActive(id), id));
    }

    [Fact]
    public async Task Application_ready_recovers_active_run_after_plugin_start()
    {
        using var fixture = new DatabaseFixture(migrate: true);
        var identity = await fixture.CreateInProgressRunAsync();
        await using var provider = BuildProvider(fixture.Database);
        var runtime = provider.GetRequiredService<PluginRuntime>();
        var availability = provider.GetRequiredService<NativeAgentAvailability>();

        Assert.True((await runtime.StartAsync(default)).Succeeded);
        Assert.False(availability.IsReady);
        Assert.Equal(AgentRunStatus.Created,
            (await fixture.Store.LoadAsync(identity.RunId, default))!.Snapshot.Status);

        var failures = await runtime.NotifyApplicationReadyAsync(new(true), default);

        Assert.Empty(failures);
        Assert.True(availability.IsReady);
        var recovered = (await fixture.Store.LoadAsync(identity.RunId, default))!;
        Assert.Equal(AgentRunStatus.Interrupted, recovered.Snapshot.Status);
        Assert.Equal("RUN_INTERRUPTED", recovered.Snapshot.ErrorCode);
    }

    [Fact]
    public async Task Storage_unavailable_keeps_offline_plugins_active_and_rejects_native_run_without_effects()
    {
        using var fixture = new DatabaseFixture(migrate: false);
        await using var provider = BuildProvider(fixture.Database);
        var runtime = provider.GetRequiredService<PluginRuntime>();
        var availability = provider.GetRequiredService<NativeAgentAvailability>();

        Assert.True((await runtime.StartAsync(default)).Succeeded);
        Assert.All(OfflinePluginIds, id => Assert.True(runtime.IsActive(id), id));
        Assert.Empty(await runtime.NotifyApplicationReadyAsync(new(false), default));
        Assert.False(availability.IsReady);
        Assert.Equal(0, fixture.CountTable("schema_migrations"));
        Assert.Equal(0, fixture.CountTable("agent_runs"));

        var runtimeHost = provider.GetRequiredService<NativeConversationRuntime>();
        var callbacks = 0;
        var request = new AgentRunRequest(
            new("blocked-run", "root-message", new("conversation", "role", null), "fixture-model", 1),
            new(), []);
        var error = await Assert.ThrowsAsync<AgentStateException>(() => runtimeHost.StartAsync(request,
            _ =>
            {
                callbacks++;
                return ValueTask.FromException<IAgentModelStep>(new InvalidOperationException("Model callback must not run."));
            },
            () => callbacks++,
            (_, _) =>
            {
                callbacks++;
                return ValueTask.FromException<ConversationSendResult>(new InvalidOperationException("Delivery callback must not run."));
            }, default).AsTask());

        Assert.Equal("RUN_HOST_CLOSED", error.Code);
        Assert.Equal(0, callbacks);
        Assert.Equal(0, fixture.CountTable("schema_migrations"));
    }

    [Fact]
    public async Task Shutdown_closes_admission_and_prevents_plugin_restart()
    {
        using var fixture = new DatabaseFixture(migrate: false);
        await using var provider = BuildProvider(fixture.Database);
        var runtime = provider.GetRequiredService<PluginRuntime>();

        Assert.True((await runtime.StartAsync(default)).Succeeded);
        Assert.Empty(await runtime.StopAsync());

        var restart = await runtime.StartAsync(default);

        Assert.False(restart.Succeeded);
        Assert.Equal("PLUGIN_RUNTIME_STOPPED", restart.ErrorCode);
        Assert.All(OfflinePluginIds, id => Assert.False(runtime.IsActive(id), id));
        Assert.Equal(0, fixture.CountTable("schema_migrations"));
    }

    [Theory]
    [InlineData("reader")]
    [InlineData("router")]
    public async Task Recovery_skips_stale_publication_and_contains_observer_failures(string failurePoint)
    {
        using var fixture = new DatabaseFixture(migrate: true);
        var availability = new NativeAgentAvailability();
        var scope = new ToolScope("fixture-conversation", "fixture-role", null);
        var identity = new AgentRunIdentity("fixture-final-run", "fixture-root", scope, "fixture-route", 1);
        var content = new ContentContextKey("fixture-role", "fixture-package", "1", "fixture-appearance", "1", "1");
        var stale = CreateAcceptedFinal(identity, "stale-delivery", content);
        var accepted = CreateAcceptedFinal(identity, "published-delivery", content);
        var root = new ChatMessage(identity.RootUserMessageId, scope.ConversationId, scope.RoleId,
            ChatMessageRole.User, "synthetic source", ChatMessageStatus.Completed, DateTimeOffset.UtcNow, content, 1);
        var assistant = new ChatMessage("fixture-assistant", scope.ConversationId, scope.RoleId,
            ChatMessageRole.Assistant, accepted.Output.Text, ChatMessageStatus.Completed, DateTimeOffset.UtcNow, content, 2);
        var deliveryStore = new ControlledFinalDeliveryStore(stale, accepted, assistant);
        var logger = new CapturingLogger<NativeAgentLifecyclePlugin>();
        var catalog = PluginCatalog.Create([]);
        var plugins = new PluginRuntime(catalog);
        var nativeRuntime = new NativeConversationRuntime(fixture.Store,
            new ToolRegistry(catalog, plugins), new NativeAgentExtensions(), admissionAvailable: () => availability.IsReady);
        var runtimeFactoryCalls = 0;
        var routerFactoryCalls = 0;
        var reader = new RecoveryConversationReader(root, fail: failurePoint == "reader");
        var plugin = new NativeAgentLifecyclePlugin(
            () => fixture.Store,
            () => deliveryStore,
            () => { runtimeFactoryCalls++; return nativeRuntime; },
            () =>
            {
                routerFactoryCalls++;
                throw new InvalidOperationException("PRIVATE_RECOVERY_MARKER");
            },
            reader,
            new RecoveryDialogueSettingsStore(),
            availability,
            logger);

        await plugin.StartAsync(default);
        await plugin.OnApplicationReadyAsync(new(true), default);

        Assert.True(availability.IsReady);
        Assert.Equal(1, runtimeFactoryCalls);
        Assert.Equal(2, deliveryStore.PublishCalls.Count);
        Assert.Equal("stale-delivery", deliveryStore.PublishCalls[0]);
        Assert.Equal("published-delivery", deliveryStore.PublishCalls[1]);
        Assert.Equal(["published-delivery"], deliveryStore.ClaimCalls);
        Assert.Equal(failurePoint == "router" ? 1 : 0, routerFactoryCalls);
        Assert.Equal(1, reader.LoadCalls);
        var logged = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, logged.Level);
        Assert.Equal("Native recovery observer dispatch failed: NATIVE_OBSERVER_DISPATCH_FAILED", logged.Message);
        Assert.DoesNotContain(logger.Entries, entry => entry.Message.Contains("PRIVATE_RECOVERY_MARKER", StringComparison.Ordinal));
        Assert.Equal(0, fixture.CountRows("agent_run_events"));

        await plugin.StopAsync(default);
        plugins.Dispose();
    }

    [Fact]
    public async Task Public_conversation_deletion_cascades_native_runs_deliveries_and_command_receipts()
    {
        using var fixture = new DatabaseFixture(migrate: true);
        var identity = await fixture.CreateInProgressRunAsync();
        var finalDeliveries = new SqliteAgentFinalDeliveryStore(fixture.Database,
            new WindowsStateProtector(), fixture.Store);
        Assert.Empty(finalDeliveries.ReadPending());

        var runKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity.RunId)));
        fixture.Execute("""
            INSERT INTO agent_final_deliveries(
              delivery_id,run_key,conversation_id,root_message_id,role_id,project_id,
              logical_fingerprint,protected_payload,accepted_at_utc)
            VALUES('fixture-delivery',$runKey,$conversation,$root,$role,NULL,$fingerprint,X'01',$at)
            """, ("$runKey", runKey), ("$conversation", identity.Scope.ConversationId),
            ("$root", identity.RootUserMessageId), ("$role", identity.Scope.RoleId),
            ("$fingerprint", new string('B', 64)), ("$at", DateTimeOffset.UtcNow.ToString("O")));

        var todoRepository = new SqliteTodoRepository(fixture.Database);
        var now = DateTimeOffset.UtcNow;
        var receipt = todoRepository.CommitAgentCommand(new TodoAgentCommit(identity.Scope,
            new string('C', 64), new string('D', 64), null,
            new TodoItem("fixture-todo", "Synthetic receipt item", null, TodoPriority.Normal, null, now, now)));
        Assert.Equal(TodoAgentCommitKind.Committed, receipt.Kind);

        Assert.Equal(1, fixture.CountRows("agent_runs"));
        Assert.Equal(1, fixture.CountRows("agent_run_events"));
        Assert.Equal(1, fixture.CountRows("agent_final_deliveries"));
        Assert.Equal(1, fixture.CountRows("todo_agent_receipts"));

        new SqliteConversationRepository(fixture.Database)
            .DeleteConversation(identity.Scope.ConversationId, identity.Scope.RoleId);

        Assert.Equal(0, fixture.CountRows("agent_runs"));
        Assert.Equal(0, fixture.CountRows("agent_run_events"));
        Assert.Equal(0, fixture.CountRows("agent_final_deliveries"));
        Assert.Equal(0, fixture.CountRows("todo_agent_receipts"));
        Assert.Null(await fixture.Store.LoadAsync(identity.RunId, default));
    }

    private static ServiceProvider BuildProvider(RuntimeDatabase database)
    {
        var services = new ServiceCollection().AddFgoPet([], includeTodo: false, includeFocus: false,
            includeMemory: false, includeSpeech: false, includeIndexTts: false, includeAgentBackend: false);
        services.RemoveAll<RuntimeDatabase>();
        services.AddSingleton(database);
        return services.BuildServiceProvider();
    }

    private static AcceptedAgentFinal CreateAcceptedFinal(AgentRunIdentity identity, string deliveryId,
        ContentContextKey content) => new(identity, deliveryId,
        new(identity.RootUserMessageId, new string('A', 64)), content,
        new ValidatedChatOutput("synthetic final", ExpressionSemantic.Neutral, null, null),
        DateTimeOffset.UtcNow, ImmutableArray<ResolvedUserInput>.Empty);

    private sealed class ControlledFinalDeliveryStore(AcceptedAgentFinal stale, AcceptedAgentFinal accepted,
        ChatMessage assistant) : IAgentFinalDeliveryStore
    {
        private int reads;
        public List<string> PublishCalls { get; } = [];
        public List<string> ClaimCalls { get; } = [];

        public bool TryAccept(AcceptedAgentFinal item) => throw new NotSupportedException();

        public AgentFinalDelivery? Publish(ToolScope scope, string deliveryId)
        {
            PublishCalls.Add(deliveryId);
            return deliveryId == stale.DeliveryId ? null : new(accepted, assistant);
        }

        public IReadOnlyList<AcceptedAgentFinal> ReadPending(int limit = 100)
        {
            if (Interlocked.Increment(ref reads) != 1) return [];
            return new[] { stale, accepted }.Take(limit).ToArray();
        }

        public bool TryClaimObservers(ToolScope scope, string deliveryId)
        {
            ClaimCalls.Add(deliveryId);
            return true;
        }
    }

    private sealed class RecoveryConversationReader(ChatMessage root, bool fail) : IConversationReader
    {
        public int LoadCalls { get; private set; }
        public Conversation? GetConversation(string conversationId, string servantId) => null;
        public IReadOnlyList<ChatMessage> LoadMessages(string conversationId, string servantId)
        {
            LoadCalls++;
            if (fail) throw new InvalidOperationException("PRIVATE_RECOVERY_MARKER");
            return [root];
        }
    }

    private sealed class RecoveryDialogueSettingsStore : IDialogueSettingsStore
    {
        public DialogueSettings Load() => DialogueSettings.Defaults;
        public void Save(DialogueSettings settings) => throw new NotSupportedException();
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, formatter(state, exception)));
    }

    private sealed class DatabaseFixture : IDisposable
    {
        private readonly string databasePath;
        private readonly string directoryPath;

        public RuntimeDatabase Database { get; }
        public SqliteAgentRunStore Store { get; }

        public DatabaseFixture(bool migrate)
        {
            directoryPath = Path.Combine(Path.GetTempPath(), "fgo-native-lifecycle-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directoryPath);
            databasePath = Path.Combine(directoryPath, "runtime.db");
            Database = new RuntimeDatabase(databasePath, pooling: false);
            Store = new SqliteAgentRunStore(Database, new WindowsStateProtector());
            if (migrate) new RuntimeDatabaseMigrator(Database).Migrate();
        }

        public async Task<AgentRunIdentity> CreateInProgressRunAsync()
        {
            var now = DateTimeOffset.UtcNow;
            using (var connection = Database.Open())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    INSERT INTO conversations(conversation_id,servant_id,created_at_utc,updated_at_utc,status)
                    VALUES('fixture-conversation','fixture-role',$now,$now,'active');
                    INSERT INTO chat_messages(message_id,conversation_id,servant_id,sequence,role,text,status,created_at_utc)
                    VALUES('fixture-root','fixture-conversation','fixture-role',1,'user','synthetic fixture','completed',$now);
                    """;
                command.Parameters.AddWithValue("$now", now.ToString("O"));
                command.ExecuteNonQuery();
            }

            var identity = new AgentRunIdentity("fixture-run", "fixture-root",
                new("fixture-conversation", "fixture-role", null), "fixture-route", 1);
            var checkpoint = new AgentRunCheckpoint
            {
                Snapshot = new AgentRunSnapshot { Identity = identity, StartedAt = now },
                JournalSequence = 1
            };
            var correlation = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity.RunId)))[..24];
            var created = new AgentEvent(1, now, AgentEventKind.RunCreated, correlation, 0);
            Assert.True(await Store.TryCreateAsync(checkpoint, ImmutableArray.Create(created), default));
            return identity;
        }

        public long CountTable(string name)
        {
            using var connection = Database.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$name";
            command.Parameters.AddWithValue("$name", name);
            return (long)command.ExecuteScalar()!;
        }

        public long CountRows(string name)
        {
            var query = name switch
            {
                "agent_runs" => "SELECT COUNT(*) FROM agent_runs",
                "agent_run_events" => "SELECT COUNT(*) FROM agent_run_events",
                "agent_final_deliveries" => "SELECT COUNT(*) FROM agent_final_deliveries",
                "todo_agent_receipts" => "SELECT COUNT(*) FROM todo_agent_receipts",
                _ => throw new ArgumentOutOfRangeException(nameof(name))
            };
            using var connection = Database.Open();
            using var command = connection.CreateCommand();
            command.CommandText = query;
            return (long)command.ExecuteScalar()!;
        }

        public void Execute(string sql, params (string Name, object Value)[] parameters)
        {
            using var connection = Database.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
            command.ExecuteNonQuery();
        }

        public void Dispose()
        {
            foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
            {
                var path = databasePath + suffix;
                if (File.Exists(path)) File.Delete(path);
            }
            Directory.Delete(directoryPath);
        }
    }
}
