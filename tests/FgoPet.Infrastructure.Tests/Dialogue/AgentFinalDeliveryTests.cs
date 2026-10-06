using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FgoPet.App.Dialogue;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Portraits;
using FgoPet.Extensibility;
using FgoPet.Infrastructure.Dialogue;
using FgoPet.Infrastructure.Persistence;
using FgoPet.Kernel.Agent;
using FgoPet.Kernel.Conversation;
using FgoPet.Platform.Secrets;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FgoPet.Infrastructure.Tests.Dialogue;

public sealed class AgentFinalDeliveryTests
{
    private const string RawFinal = "{\"text\":\"final-public-marker\",\"emotion\":\"happy\"}";

    [Fact]
    public async Task Accepted_final_survives_reopen_publishes_semantic_clarifications_once_and_claims_once()
    {
        using var fixture = new Fixture();
        var accepted = await fixture.StageCompletedRunAsync(withClarifications: true, includeToolTrace: true);
        fixture.AppendPriorMessage("prior assistant turn");

        Assert.Empty(fixture.Store.ReadPending());
        Assert.Null(fixture.Store.Publish(fixture.Scope, accepted.DeliveryId));
        Assert.Equal(2, fixture.MessageCount());
        Assert.True(fixture.Store.TryAccept(accepted));
        Assert.True(fixture.Store.TryAccept(accepted));
        Assert.True(fixture.Store.TryAccept(accepted with { AcceptedAt = accepted.AcceptedAt.AddMinutes(1) }));
        Assert.False(fixture.Store.TryAccept(accepted with { Output = accepted.Output with { Text = "conflicting final" } }));
        Assert.Equal(1, fixture.DeliveryCount());
        var beforeRun = await fixture.Runs.LoadAsync(accepted.Identity.RunId, default);
        var eventCount = fixture.Count("agent_run_events");
        var reopenedDatabase = new RuntimeDatabase(fixture.Database.DatabasePath, pooling: false);
        var reopenedRuns = new SqliteAgentRunStore(reopenedDatabase, fixture.Protector, fixture.Clock);
        var reopened = new SqliteAgentFinalDeliveryStore(reopenedDatabase, fixture.Protector, reopenedRuns);
        var pending = Assert.Single(reopened.ReadPending());
        Assert.Equal(accepted.DeliveryId, pending.DeliveryId);
        Assert.Equal("final-public-marker", pending.Output.Text);

        var first = Assert.IsType<AgentFinalDelivery>(reopened.Publish(fixture.Scope, accepted.DeliveryId));
        var repeated = Assert.IsType<AgentFinalDelivery>(reopened.Publish(fixture.Scope, accepted.DeliveryId));
        Assert.Equal(first.Assistant, repeated.Assistant);
        Assert.Equal(ChatMessageRole.Assistant, first.Assistant.Role);
        Assert.Equal("final-public-marker", first.Assistant.Text);

        var messages = fixture.Conversations.LoadMessages(fixture.Scope.ConversationId, fixture.Scope.RoleId);
        Assert.Equal(7, messages.Count);
        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6, 7 }, messages.Select(message => message.Sequence));
        Assert.Equal((ChatMessageRole.Assistant, "Which synthetic option?"), (messages[2].Role, messages[2].Text));
        Assert.Equal(ChatMessageRole.User, messages[3].Role);
        Assert.Contains("Preferred label", messages[3].Text, StringComparison.Ordinal);
        Assert.Equal((ChatMessageRole.Assistant, "Any extra note?"), (messages[4].Role, messages[4].Text));
        Assert.Equal(ChatMessageRole.User, messages[5].Role);
        Assert.Contains("private-answer-marker", messages[5].Text, StringComparison.Ordinal);
        Assert.Equal(ChatMessageRole.Assistant, messages[6].Role);
        Assert.All(messages, message => Assert.Equal(fixture.Context, message.ContentContext));
        Assert.DoesNotContain(messages, message => message.Text.Contains("private-tool-argument-marker", StringComparison.Ordinal));
        Assert.Empty(reopened.ReadPending());
        var afterRun = await reopenedRuns.LoadAsync(accepted.Identity.RunId, default);
        Assert.Equal(beforeRun!.JournalSequence, afterRun!.JournalSequence);
        Assert.Equal(beforeRun.Calls.Single().Status, afterRun.Calls.Single().Status);
        Assert.Equal(eventCount, fixture.Count("agent_run_events"));

        Assert.False(reopened.TryClaimObservers(new("conversation", "other-role", null), accepted.DeliveryId));
        Assert.True(reopened.TryClaimObservers(fixture.Scope, accepted.DeliveryId));
        Assert.False(reopened.TryClaimObservers(fixture.Scope, accepted.DeliveryId));
        Assert.Equal(7, fixture.MessageCount());
    }

    [Fact]
    public async Task Acceptance_requires_completed_matching_run_delivery_identity_and_structured_output()
    {
        using var fixture = new Fixture();
        var accepted = await fixture.StageCompletedRunAsync();

        Assert.False(fixture.Store.TryAccept(accepted with { DeliveryId = "other-delivery" }));
        Assert.False(fixture.Store.TryAccept(accepted with
        {
            Identity = accepted.Identity with { RootUserMessageId = "other-root" }
        }));
        Assert.False(fixture.Store.TryAccept(accepted with
        {
            Output = accepted.Output with { Text = "not the validated final" }
        }));
        Assert.False(fixture.Store.TryAccept(accepted with
        {
            ContentContext = Fixture.ContextFor("other-role")
        }));
        Assert.Equal(0, fixture.DeliveryCount());

        var malformed = await fixture.StageCompletedRunAsync("malformed", rawFinal: "{\"text\":\"final-public-marker\"");
        Assert.False(fixture.Store.TryAccept(malformed));
        Assert.Equal(0, fixture.DeliveryCount());

        var missingAnswer = await fixture.StageCompletedRunAsync("missing-answer", withClarifications: true, emptyAnswer: true);
        Assert.False(fixture.Store.TryAccept(missingAnswer));
        Assert.Equal(0, fixture.DeliveryCount());

        var unfinished = await fixture.StageCompletedRunAsync("unfinished", completed: false);
        Assert.False(fixture.Store.TryAccept(unfinished));
        Assert.Equal(0, fixture.DeliveryCount());
    }

    [Theory]
    [InlineData("archive")]
    [InlineData("change-project")]
    [InlineData("change-source")]
    [InlineData("change-binding")]
    public async Task Acceptance_rejects_changed_active_scope_root_source_or_content_binding(string change)
    {
        using var fixture = new Fixture();
        var accepted = await fixture.StageCompletedRunAsync();
        switch (change)
        {
            case "archive": fixture.Execute("UPDATE conversations SET status='archived' WHERE conversation_id='conversation'"); break;
            case "change-project": fixture.Execute("UPDATE conversations SET project_id='different-project' WHERE conversation_id='conversation'"); break;
            case "change-source": fixture.Execute("UPDATE chat_messages SET text='changed source' WHERE message_id='root-run'"); break;
            case "change-binding": fixture.Execute("UPDATE content_bindings SET persona_version='changed-persona' WHERE binding_id=(SELECT current_binding_id FROM conversations WHERE conversation_id='conversation')"); break;
            default: throw new ArgumentOutOfRangeException(nameof(change));
        }

        Assert.False(fixture.Store.TryAccept(accepted));
        Assert.Equal(0, fixture.DeliveryCount());
    }

    [Fact]
    public async Task Publish_rechecks_current_scope_and_source_before_transcript_mutation()
    {
        using var fixture = new Fixture();
        var accepted = await fixture.StageCompletedRunAsync(withClarifications: true);
        Assert.True(fixture.Store.TryAccept(accepted));
        fixture.Execute("UPDATE chat_messages SET text='source changed after acceptance' WHERE message_id='root-run'");

        Assert.Null(fixture.Store.Publish(fixture.Scope, accepted.DeliveryId));
        Assert.Equal(1, fixture.MessageCount());
        Assert.Equal(1, fixture.PendingCount());
        Assert.False(fixture.Store.TryClaimObservers(fixture.Scope, accepted.DeliveryId));
    }

    [Theory]
    [InlineData("archive")]
    [InlineData("change-project")]
    [InlineData("change-role")]
    [InlineData("change-binding")]
    public async Task Publish_rejects_archived_or_changed_scope_without_consuming_pending_final(string change)
    {
        using var fixture = new Fixture();
        var accepted = await fixture.StageCompletedRunAsync(withClarifications: true);
        Assert.True(fixture.Store.TryAccept(accepted));
        switch (change)
        {
            case "archive": fixture.Execute("UPDATE conversations SET status='archived' WHERE conversation_id='conversation'"); break;
            case "change-project": fixture.Execute("UPDATE conversations SET project_id='different-project' WHERE conversation_id='conversation'"); break;
            case "change-role": fixture.Execute("UPDATE conversations SET servant_id='different-role' WHERE conversation_id='conversation'"); break;
            case "change-binding": fixture.Execute("UPDATE content_bindings SET persona_version='changed-persona' WHERE binding_id=(SELECT current_binding_id FROM conversations WHERE conversation_id='conversation')"); break;
            default: throw new ArgumentOutOfRangeException(nameof(change));
        }

        Assert.Null(fixture.Store.Publish(fixture.Scope, accepted.DeliveryId));
        Assert.Equal(1, fixture.PendingCount());
        Assert.Equal(1, fixture.MessageCount());
        Assert.False(fixture.Store.TryClaimObservers(fixture.Scope, accepted.DeliveryId));
    }

    [Theory]
    [InlineData("archive")]
    [InlineData("change-binding")]
    [InlineData("change-source")]
    public async Task ReadPending_skips_stale_scope_rows_without_deleting_them_or_blocking_valid_rows(string change)
    {
        using var fixture = new Fixture();
        var stale = await fixture.StageCompletedRunAsync("stale");
        Assert.True(fixture.Store.TryAccept(stale));
        switch (change)
        {
            case "archive": fixture.Execute("UPDATE conversations SET status='archived' WHERE conversation_id='conversation'"); break;
            case "change-binding": fixture.Execute("UPDATE content_bindings SET persona_version='changed-persona' WHERE binding_id=(SELECT current_binding_id FROM conversations WHERE conversation_id='conversation')"); break;
            case "change-source": fixture.Execute("UPDATE chat_messages SET text='changed source' WHERE message_id='root-stale'"); break;
            default: throw new ArgumentOutOfRangeException(nameof(change));
        }

        var currentScope = new ToolScope("valid-conversation", "role", null);
        var currentContext = change == "change-binding"
            ? new ContentContextKey("role", "valid-package", "1", "default", "persona-v1", "knowledge-v1")
            : fixture.Context;
        fixture.Conversations.CreateConversation(currentScope.ConversationId, currentScope.RoleId, currentContext, fixture.Clock.GetUtcNow());
        var current = await fixture.StageCompletedRunAsync("current", scope: currentScope, contextOverride: currentContext);
        Assert.True(fixture.Store.TryAccept(current));

        var pending = Assert.Single(fixture.Store.ReadPending(limit: 1));
        Assert.Equal(current.DeliveryId, pending.DeliveryId);
        Assert.Equal(2, fixture.PendingCount());
        Assert.True(fixture.DeliveryExists(stale.DeliveryId));
        Assert.Null(fixture.Store.Publish(stale.Identity.Scope, stale.DeliveryId));
        Assert.NotNull(fixture.Store.Publish(currentScope, current.DeliveryId));
        Assert.Equal(1, fixture.PendingCount());
        Assert.True(fixture.DeliveryExists(stale.DeliveryId));
    }

    [Fact]
    public async Task ReadPending_scans_past_one_hundred_stale_rows_to_recover_valid_tail()
    {
        using var fixture = new Fixture();
        var stale = new List<AcceptedAgentFinal>(100);
        for (var index = 0; index < 100; index++)
        {
            var accepted = await fixture.StageCompletedRunAsync("boundary-stale-" + index.ToString("D3", System.Globalization.CultureInfo.InvariantCulture));
            Assert.True(fixture.Store.TryAccept(accepted));
            stale.Add(accepted);
        }

        var valid = await fixture.StageCompletedRunAsync("boundary-valid-tail");
        Assert.True(fixture.Store.TryAccept(valid));
        foreach (var accepted in stale)
            fixture.Execute($"UPDATE chat_messages SET text='changed source' WHERE message_id='{accepted.Identity.RootUserMessageId}'");

        var pending = Assert.Single(fixture.Store.ReadPending(limit: 1));
        Assert.Equal(valid.DeliveryId, pending.DeliveryId);
        Assert.Equal(101, fixture.PendingCount());
        Assert.Null(fixture.Store.Publish(stale[0].Identity.Scope, stale[0].DeliveryId));
        Assert.True(fixture.DeliveryExists(stale[0].DeliveryId));
        Assert.NotNull(fixture.Store.Publish(valid.Identity.Scope, valid.DeliveryId));
        Assert.Equal(100, fixture.PendingCount());
    }

    [Fact]
    public async Task ReadPending_fails_closed_if_pending_row_count_exceeds_store_bound()
    {
        using var fixture = new Fixture();
        var accepted = await fixture.StageCompletedRunAsync();
        Assert.True(fixture.Store.TryAccept(accepted));
        fixture.Execute("""
            PRAGMA foreign_keys=OFF;
            WITH RECURSIVE synthetic_rows(index_value) AS (
                SELECT 1 UNION ALL SELECT index_value + 1 FROM synthetic_rows WHERE index_value < 256
            )
            INSERT INTO agent_final_deliveries(
                delivery_id,run_key,conversation_id,root_message_id,role_id,project_id,
                logical_fingerprint,protected_payload,accepted_at_utc)
            SELECT 'corrupt-overflow-' || synthetic_rows.index_value,
                'orphan-run-' || synthetic_rows.index_value,
                template.conversation_id,template.root_message_id,template.role_id,template.project_id,
                template.logical_fingerprint,template.protected_payload,template.accepted_at_utc
            FROM synthetic_rows CROSS JOIN agent_final_deliveries AS template
            WHERE template.delivery_id='delivery-run';
            """);

        var error = Assert.Throws<AgentStateException>(() => fixture.Store.ReadPending());
        Assert.Equal("AGENT_FINAL_DELIVERY_UNREADABLE", error.Code);
        Assert.Equal(257, fixture.PendingCount());
    }

    [Fact]
    public async Task Failed_transcript_insert_rolls_back_clarifications_final_marker_and_binding_revision()
    {
        using var fixture = new Fixture();
        var accepted = await fixture.StageCompletedRunAsync(withClarifications: true);
        Assert.True(fixture.Store.TryAccept(accepted));
        var beforeRevision = fixture.ContextRevision();
        fixture.Execute("CREATE TRIGGER reject_final BEFORE INSERT ON chat_messages WHEN new.role='assistant' BEGIN SELECT RAISE(ABORT,'synthetic failure'); END");

        var error = Assert.Throws<AgentStateException>(() => fixture.Store.Publish(fixture.Scope, accepted.DeliveryId));
        Assert.Equal("AGENT_FINAL_DELIVERY_FAILED", error.Code);
        Assert.Equal(1, fixture.MessageCount());
        Assert.Equal(beforeRevision, fixture.ContextRevision());
        Assert.Equal(1, fixture.PendingCount());
        Assert.False(fixture.IsDelivered(accepted.DeliveryId));

        fixture.Execute("DROP TRIGGER reject_final");
        Assert.NotNull(fixture.Store.Publish(fixture.Scope, accepted.DeliveryId));
        Assert.Equal(6, fixture.MessageCount());
    }

    [Fact]
    public async Task Protected_payload_contains_no_final_or_clarification_plaintext_and_corruption_fails_closed()
    {
        using var fixture = new Fixture();
        var accepted = await fixture.StageCompletedRunAsync(withClarifications: true);
        Assert.True(fixture.Store.TryAccept(accepted));
        var blob = fixture.ProtectedPayload(accepted.DeliveryId);
        var encoded = Encoding.UTF8.GetString(blob);
        Assert.DoesNotContain("final-public-marker", encoded);
        Assert.DoesNotContain("private-answer-marker", encoded);
        Assert.DoesNotContain("final-public-marker", fixture.DeliveryMetadata(accepted.DeliveryId));
        Assert.DoesNotContain("private-answer-marker", fixture.DeliveryMetadata(accepted.DeliveryId));
        Assert.Contains(fixture.Protector.Purposes, purpose => purpose.Contains("final-delivery", StringComparison.Ordinal));

        fixture.Execute("UPDATE agent_final_deliveries SET protected_payload=x'01020304' WHERE delivery_id='delivery-run'");
        var error = Assert.Throws<AgentStateException>(() => fixture.Store.ReadPending());
        Assert.Equal("AGENT_FINAL_DELIVERY_UNREADABLE", error.Code);
        Assert.DoesNotContain("synthetic", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Conversation_and_root_deletion_cascade_accepted_delivery_rows()
    {
        using var fixture = new Fixture();
        var rootBound = await fixture.StageCompletedRunAsync();
        Assert.True(fixture.Store.TryAccept(rootBound));
        fixture.Execute("DELETE FROM chat_messages WHERE message_id='root-run'");
        Assert.Equal(0, fixture.DeliveryCount());
        Assert.Equal(0, fixture.Count("agent_runs"));

        var conversationBound = await fixture.StageCompletedRunAsync("conversation-bound");
        Assert.True(fixture.Store.TryAccept(conversationBound));
        fixture.Conversations.DeleteConversation("conversation", "role");
        Assert.Equal(0, fixture.DeliveryCount());
        Assert.Equal(0, fixture.Count("agent_runs"));
        Assert.Equal(0, fixture.Count("agent_run_events"));
        Assert.Empty(fixture.Store.ReadPending());
    }

    [Fact]
    public async Task Oversized_semantic_projection_is_rejected_without_truncation_or_pending_row()
    {
        using var fixture = new Fixture();
        var accepted = await fixture.StageCompletedRunAsync(withClarifications: true,
            clarificationQuestion: new string('q', 11_990));

        Assert.False(fixture.Store.TryAccept(accepted));
        Assert.Equal(0, fixture.DeliveryCount());
        Assert.Equal(1, fixture.MessageCount());
    }

    [Fact]
    public async Task Protected_delivery_byte_quota_preserves_all_unpublished_rows()
    {
        using var fixture = new Fixture();
        var accepted = new List<AcceptedAgentFinal>();
        AcceptedAgentFinal? rejected = null;
        for (var index = 0; index < 120; index++)
        {
            var runId = "quota-run-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var item = await fixture.StageLargeCompletedRunAsync(runId);
            if (!fixture.Store.TryAccept(item)) { rejected = item; break; }
            accepted.Add(item);
        }

        Assert.True(accepted.Count > 80);
        Assert.NotNull(rejected);
        Assert.Equal(accepted.Count, fixture.DeliveryCount());
        Assert.Equal(accepted.Count, fixture.PendingCount());
        Assert.True(fixture.Store.TryAccept(accepted[0]));
        Assert.Equal(accepted.Count, fixture.DeliveryCount());
        Assert.True(fixture.DeliveryExists(accepted[0].DeliveryId));
        Assert.InRange(fixture.ProtectedBytes(), 1, 16L * 1024 * 1024);
    }

    [Fact]
    public async Task Aggregate_quota_counts_protector_overhead_and_does_not_evict_accepted_rows()
    {
        using var fixture = new Fixture();
        fixture.Protector.OverheadBytes = 140_000;
        var accepted = new List<AcceptedAgentFinal>();
        AcceptedAgentFinal? rejected = null;
        for (var index = 0; index < 120; index++)
        {
            var runId = "overhead-run-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var item = await fixture.StageCompletedRunAsync(runId);
            if (!fixture.Store.TryAccept(item)) { rejected = item; break; }
            accepted.Add(item);
        }

        Assert.True(accepted.Count is > 80 and < 120);
        Assert.NotNull(rejected);
        Assert.Equal(accepted.Count, fixture.DeliveryCount());
        Assert.Equal(accepted.Count, fixture.PendingCount());
        Assert.True(fixture.ProtectedPayload(accepted[0].DeliveryId).Length > 140_000);
        Assert.InRange(fixture.ProtectedBytes(), 1, 16L * 1024 * 1024);
        Assert.True(fixture.Store.TryAccept(accepted[0]));
        Assert.Equal(accepted.Count, fixture.DeliveryCount());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Accepted_delivery_keeps_its_terminal_run_and_original_answer_past_run_retention(bool publishBeforeRetention)
    {
        using var fixture = new Fixture();
        var accepted = await fixture.StageCompletedRunAsync(withClarifications: true);
        Assert.True(fixture.Store.TryAccept(accepted));
        AgentFinalDelivery? originalDelivery = null;
        if (publishBeforeRetention) originalDelivery = fixture.Store.Publish(fixture.Scope, accepted.DeliveryId);
        Assert.Equal(publishBeforeRetention, originalDelivery is not null);

        fixture.Clock.Advance(TimeSpan.FromDays(8));
        _ = await fixture.StageCompletedRunAsync("retention-trigger"); // TryCreate invokes terminal-run pruning.

        var retained = await fixture.Runs.LoadAsync(accepted.Identity.RunId, default);
        Assert.NotNull(retained);
        Assert.Equal(AgentRunStatus.Completed, retained!.Snapshot.Status);
        Assert.Equal("private-answer-marker", retained.ResolvedInputs.Single().Reply.Answers[1].Text);
        Assert.Equal(1, fixture.DeliveryCount());

        if (publishBeforeRetention)
        {
            var repeated = fixture.Store.Publish(fixture.Scope, accepted.DeliveryId);
            Assert.NotNull(repeated);
            Assert.Equal(originalDelivery!.Assistant.MessageId, repeated!.Assistant.MessageId);
        }
        else
        {
            var pending = Assert.Single(fixture.Store.ReadPending());
            Assert.Equal(accepted.DeliveryId, pending.DeliveryId);
            var recovered = fixture.Store.Publish(fixture.Scope, pending.DeliveryId);
            Assert.NotNull(recovered);
            Assert.Equal("final-public-marker", recovered!.Assistant.Text);
            Assert.Empty(fixture.Store.ReadPending());
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string directoryPath = Path.Combine(Path.GetTempPath(), "fgo-agent-final-delivery-" + Guid.NewGuid().ToString("N"));
        private readonly DateTimeOffset now;

        public Fixture()
        {
            now = DateTimeOffset.UtcNow;
            Clock = new MutableTimeProvider(now);
            Directory.CreateDirectory(directoryPath);
            Database = new(Path.Combine(directoryPath, "runtime.db"), pooling: false);
            new RuntimeDatabaseMigrator(Database).Migrate();
            Protector = new SyntheticProtector();
            Conversations = new SqliteConversationRepository(Database);
            Conversations.CreateConversation("conversation", "role", ContextFor("role"), now);
            Runs = new SqliteAgentRunStore(Database, Protector, Clock);
            Store = new SqliteAgentFinalDeliveryStore(Database, Protector, Runs);
        }

        public RuntimeDatabase Database { get; }
        public MutableTimeProvider Clock { get; }
        public SyntheticProtector Protector { get; }
        public SqliteConversationRepository Conversations { get; }
        public SqliteAgentRunStore Runs { get; }
        public SqliteAgentFinalDeliveryStore Store { get; }
        public ToolScope Scope { get; } = new("conversation", "role", null);
        public static ContentContextKey ContextFor(string role) => new(role, "synthetic-package", "1", "default", "persona-v1", "knowledge-v1");
        public ContentContextKey Context => ContextFor("role");

        public async Task<AcceptedAgentFinal> StageCompletedRunAsync(string runId = "run", bool completed = true,
            bool withClarifications = false, bool includeToolTrace = false, string clarificationQuestion = "Which synthetic option?",
            string clarificationAnswer = "private-answer-marker", string rawFinal = RawFinal, bool emptyAnswer = false,
            ToolScope? scope = null, ContentContextKey? contextOverride = null)
        {
            var runScope = scope ?? Scope;
            var rootId = "root-" + runId;
            var at = Clock.GetUtcNow().AddMilliseconds(MessageCount());
            var context = contextOverride ?? ContextFor(runScope.RoleId);
            var root = new ChatMessage(rootId, runScope.ConversationId, runScope.RoleId, ChatMessageRole.User,
                "synthetic source for " + runId, ChatMessageStatus.Completed, at, context, NextSequence(runScope.ConversationId));
            Conversations.Append(root);
            var identity = new AgentRunIdentity(runId, rootId, runScope, "model-revision", 1);
            var clarifications = withClarifications
                ? ImmutableArray.Create(CreateClarification(identity, at, clarificationQuestion, clarificationAnswer, emptyAnswer))
                : ImmutableArray<ResolvedUserInput>.Empty;
            await StageCheckpointAsync(identity, completed, rawFinal, "delivery-" + runId, clarifications, includeToolTrace, at);
            var source = new ProtectedQueryReference(rootId, Fingerprint(root));
            var output = StructuredOutputValidator.Validate(rawFinal, ExpressionSemanticKeys.Core.ToHashSet(StringComparer.Ordinal));
            return new(identity, "delivery-" + runId, source, context, output, at.AddSeconds(2), clarifications);
        }

        public async Task<AcceptedAgentFinal> StageLargeCompletedRunAsync(string runId)
        {
            var rootId = "root-" + runId;
            var at = Clock.GetUtcNow().AddMilliseconds(MessageCount());
            var root = new ChatMessage(rootId, Scope.ConversationId, Scope.RoleId, ChatMessageRole.User,
                "synthetic source for " + runId, ChatMessageStatus.Completed, at, Context, NextSequence());
            Conversations.Append(root);
            var identity = new AgentRunIdentity(runId, rootId, Scope, "model-revision", 1);
            var clarifications = ImmutableArray.CreateBuilder<ResolvedUserInput>(16);
            for (var requestIndex = 0; requestIndex < 16; requestIndex++)
                clarifications.Add(CreateLargeClarification(identity, at, requestIndex));
            var items = clarifications.ToImmutable();
            await StageCheckpointAsync(identity, true, RawFinal, "delivery-" + runId, items, includeToolTrace: false, at);
            var source = new ProtectedQueryReference(rootId, Fingerprint(root));
            var output = StructuredOutputValidator.Validate(RawFinal, ExpressionSemanticKeys.Core.ToHashSet(StringComparer.Ordinal));
            return new(identity, "delivery-" + runId, source, Context, output, at.AddSeconds(2), items);
        }

        private async Task StageCheckpointAsync(AgentRunIdentity identity, bool completed, string rawFinal, string deliveryId,
            ImmutableArray<ResolvedUserInput> clarifications, bool includeToolTrace, DateTimeOffset startedAt)
        {
            var at = startedAt;
            var calls = includeToolTrace
                ? ImmutableArray.Create(new AgentCallCheckpoint(new("ask-call", "user.ask", "{\"private\":\"private-tool-argument-marker\"}"),
                    AgentCallStatus.Completed, true, ToolEffect.Command,
                    new ToolResult(true, JsonSerializer.SerializeToElement(new { result = "private-tool-result-marker" }))
                    { ExecutionState = ToolExecutionState.Committed }))
                : ImmutableArray<AgentCallCheckpoint>.Empty;
            var initial = new AgentRunCheckpoint
            {
                Snapshot = new AgentRunSnapshot { Identity = identity, StartedAt = at },
                JournalSequence = 1
            };
            Assert.True(await Runs.TryCreateAsync(initial, [Event(identity, 1, AgentEventKind.RunCreated, at)], default));

            var running = initial with
            {
                Snapshot = initial.Snapshot with
                {
                    Revision = 1,
                    Status = AgentRunStatus.Running,
                    StepNumber = includeToolTrace ? 1 : 0,
                    ToolCalls = includeToolTrace ? 1 : 0
                },
                Calls = calls,
                NextCallIndex = calls.Length,
                JournalSequence = 2
            };
            Assert.True(await Runs.TryCommitAsync(running, 0, [Event(identity, 2, AgentEventKind.RunStarted, at)], default));
            if (!completed) return;

            var final = running with
            {
                Snapshot = running.Snapshot with
                {
                    Revision = 2,
                    Status = AgentRunStatus.Completed,
                    CompletedAt = at.AddSeconds(1)
                },
                JournalSequence = 4,
                FinalText = rawFinal,
                FinalDeliveryId = deliveryId,
                ResolvedInputs = clarifications
            };
            Assert.True(await Runs.TryCommitAsync(final, 1,
                [Event(identity, 3, AgentEventKind.StepCompleted, at), Event(identity, 4, AgentEventKind.RunCompleted, at)], default));
        }

        private ResolvedUserInput CreateClarification(AgentRunIdentity identity, DateTimeOffset at, string firstQuestion, string answer, bool emptyAnswer)
        {
            var binding = new InteractionBinding(identity, 1, "ask-call", "request-" + identity.RunId, 1, at.AddHours(1));
            var request = new UserInputRequest(binding,
            [
                new("choice", firstQuestion, ["Preferred label", "Other"], false),
                new("notes", "Any extra note?", [], false)
            ]);
            var reply = new UserInputReply(identity.RunId, binding.RequestId, binding.WaitingRevision,
            [
                new("choice", emptyAnswer ? [] : [0], null),
                new("notes", [], answer)
            ]);
            return new(request, reply);
        }

        private static ResolvedUserInput CreateLargeClarification(AgentRunIdentity identity, DateTimeOffset at, int requestIndex)
        {
            var requestId = "request-" + requestIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var binding = new InteractionBinding(identity, requestIndex + 1, "ask-" + requestIndex, requestId,
                requestIndex + 1, at.AddHours(1));
            var questions = ImmutableArray.CreateBuilder<UserQuestion>(3);
            var answers = ImmutableArray.CreateBuilder<QuestionAnswer>(3);
            for (var questionIndex = 0; questionIndex < 3; questionIndex++)
            {
                var id = "q" + questionIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
                questions.Add(new(id, "Synthetic quota question " + id, [], false));
                answers.Add(new(id, [], new string('x', 2_900)));
            }
            return new(new UserInputRequest(binding, questions.ToImmutable()),
                new UserInputReply(identity.RunId, requestId, binding.WaitingRevision, answers.ToImmutable()));
        }

        private AgentEvent Event(AgentRunIdentity identity, long sequence, AgentEventKind kind, DateTimeOffset at) =>
            new(sequence, at, kind, Correlation(identity.RunId), 0);

        private static string Correlation(string runId) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(runId)))[..24];
        private static string Fingerprint(ChatMessage message) => Convert.ToHexString(SHA256.HashData(
            JsonSerializer.SerializeToUtf8Bytes(new { message.MessageId, message.Role, message.Text, message.CreatedAtUtc })));

        public void AppendPriorMessage(string text)
        {
            var sequence = NextSequence();
            Conversations.Append(new ChatMessage("prior-message", Scope.ConversationId, Scope.RoleId, ChatMessageRole.Assistant,
                text, ChatMessageStatus.Completed, now.AddSeconds(1), Context, sequence));
        }

        private int NextSequence(string conversationId = "conversation")
        {
            using var connection = Database.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COALESCE(MAX(sequence),0)+1 FROM chat_messages WHERE conversation_id=$conversation";
            command.Parameters.AddWithValue("$conversation", conversationId);
            return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }

        public void Execute(string sql)
        {
            using var connection = Database.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        public int MessageCount() => Convert.ToInt32(Scalar("SELECT COUNT(*) FROM chat_messages"), System.Globalization.CultureInfo.InvariantCulture);
        public int DeliveryCount() => Convert.ToInt32(Scalar("SELECT COUNT(*) FROM agent_final_deliveries"), System.Globalization.CultureInfo.InvariantCulture);
        public long ProtectedBytes() => Scalar("SELECT COALESCE(SUM(length(protected_payload)),0) FROM agent_final_deliveries");
        public int PendingCount() => Convert.ToInt32(Scalar("SELECT COUNT(*) FROM agent_final_deliveries WHERE delivered_at_utc IS NULL"), System.Globalization.CultureInfo.InvariantCulture);
        public int ContextRevision() => Convert.ToInt32(Scalar("SELECT context_revision FROM conversations WHERE conversation_id='conversation'"), System.Globalization.CultureInfo.InvariantCulture);
        public long Count(string table)
        {
            if (table is not ("agent_runs" or "agent_run_events" or "agent_final_deliveries")) throw new ArgumentOutOfRangeException(nameof(table));
            return Scalar("SELECT COUNT(*) FROM " + table);
        }
        public bool IsDelivered(string deliveryId) => Scalar("SELECT COUNT(*) FROM agent_final_deliveries WHERE delivery_id=$delivery AND delivered_at_utc IS NOT NULL", deliveryId) == 1;
        public bool DeliveryExists(string deliveryId) => Scalar("SELECT COUNT(*) FROM agent_final_deliveries WHERE delivery_id=$delivery", deliveryId) == 1;
        public byte[] ProtectedPayload(string deliveryId)
        {
            using var connection = Database.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT protected_payload FROM agent_final_deliveries WHERE delivery_id=$delivery";
            command.Parameters.AddWithValue("$delivery", deliveryId);
            return (byte[])command.ExecuteScalar()!;
        }
        public string DeliveryMetadata(string deliveryId)
        {
            using var connection = Database.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT delivery_id||'|'||run_key||'|'||conversation_id||'|'||root_message_id||'|'||role_id||'|'
                    ||COALESCE(project_id,'')||'|'||logical_fingerprint||'|'||accepted_at_utc||'|'
                    ||COALESCE(delivered_at_utc,'')||'|'||COALESCE(assistant_message_id,'')
                FROM agent_final_deliveries WHERE delivery_id=$delivery
                """;
            command.Parameters.AddWithValue("$delivery", deliveryId);
            return (string)command.ExecuteScalar()!;
        }
        private long Scalar(string sql, string? deliveryId = null)
        {
            using var connection = Database.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            if (deliveryId is not null) command.Parameters.AddWithValue("$delivery", deliveryId);
            return (long)command.ExecuteScalar()!;
        }

        public void Dispose()
        {
            Protector.Dispose();
            foreach (var suffix in new[] { "runtime.db", "runtime.db-wal", "runtime.db-shm" })
            {
                var exactPath = Path.Combine(directoryPath, suffix);
                if (File.Exists(exactPath)) File.Delete(exactPath);
            }
            if (Directory.Exists(directoryPath)) Directory.Delete(directoryPath);
        }
    }

    private sealed class SyntheticProtector : IProtectedStateProtector, IDisposable
    {
        private readonly byte[] key = RandomNumberGenerator.GetBytes(32);
        public List<string> Purposes { get; } = [];
        public int OverheadBytes { get; set; }

        public byte[] Protect(ReadOnlySpan<byte> plaintext, string purpose)
        {
            Purposes.Add(purpose);
            var nonce = RandomNumberGenerator.GetBytes(12);
            var tag = new byte[16];
            var ciphertext = new byte[plaintext.Length];
            using var aes = new AesGcm(key, tag.Length);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, Encoding.UTF8.GetBytes(purpose));
            return [.. nonce, .. tag, .. ciphertext, .. new byte[OverheadBytes]];
        }

        public byte[] Unprotect(ReadOnlySpan<byte> protectedState, string purpose)
        {
            if (protectedState.Length < 28 + OverheadBytes) throw new StateProtectionException("SYNTHETIC_CIPHER_INVALID");
            try
            {
                var plaintext = new byte[protectedState.Length - 28 - OverheadBytes];
                using var aes = new AesGcm(key, 16);
                aes.Decrypt(protectedState[..12], protectedState.Slice(28, protectedState.Length - 28 - OverheadBytes), protectedState[12..28], plaintext,
                    Encoding.UTF8.GetBytes(purpose));
                return plaintext;
            }
            catch (CryptographicException)
            {
                throw new StateProtectionException("SYNTHETIC_CIPHER_INVALID");
            }
        }

        public void Dispose() => CryptographicOperations.ZeroMemory(key);
    }

    private sealed class MutableTimeProvider(DateTimeOffset initial) : TimeProvider
    {
        private DateTimeOffset now = initial;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan amount) => now += amount;
    }
}
