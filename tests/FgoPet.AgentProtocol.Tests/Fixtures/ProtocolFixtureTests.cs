using FgoPet.AgentProtocol;
using FgoPet.AgentProtocol.Messages;
using FgoPet.AgentProtocol.Privacy;
using FgoPet.AgentProtocol.Validation;
using Xunit;

namespace FgoPet.AgentProtocol.Tests.Fixtures;

public sealed class ProtocolFixtureTests
{
    [Fact]
    public void V1_event_envelope_round_trips_as_line_json()
    {
        var message = new AgentEventMessage("codex", "source-1", "task-1", 1, "task_started", DateTimeOffset.Parse("2026-08-30T08:00:00Z"));
        var envelope = ProtocolEnvelope.Create("message-1", "agent_event", message, DateTimeOffset.Parse("2026-08-30T08:00:01Z"));

        var json = envelope.ToJson();
        var parsed = ProtocolEnvelope.Parse(json);
        AgentProtocolValidator.Validate(parsed);
        var roundTripped = parsed.DeserializePayload<AgentEventMessage>();

        Assert.Equal("1", parsed.ProtocolVersion);
        Assert.Equal("agent_event", parsed.MessageType);
        Assert.Equal(message.SourceType, roundTripped.SourceType);
        Assert.Equal(message.SourceInstance, roundTripped.SourceInstance);
        Assert.Equal(message.TaskId, roundTripped.TaskId);
        Assert.Equal(message.Sequence, roundTripped.Sequence);
        Assert.Equal(message.EventType, roundTripped.EventType);
        Assert.Equal(message.OccurredAt, roundTripped.OccurredAt);
    }

    [Fact]
    public void Unknown_protocol_version_is_rejected()
    {
        var json = "{\"protocol_version\":\"2\",\"message_id\":\"m1\",\"message_type\":\"agent_event\",\"sent_at\":\"2026-08-30T08:00:00Z\",\"payload\":{}}";

        Assert.Throws<AgentProtocolValidationException>(() => AgentProtocolValidator.Validate(ProtocolEnvelope.Parse(json)));
    }

    [Fact]
    public void Unknown_event_and_missing_sequence_are_rejected()
    {
        var unknown = ProtocolEnvelope.Parse("{\"protocol_version\":\"1\",\"message_id\":\"m1\",\"message_type\":\"agent_event\",\"sent_at\":\"2026-08-30T08:00:00Z\",\"payload\":{\"source_type\":\"codex\",\"source_instance\":\"s1\",\"task_id\":\"t1\",\"sequence\":1,\"event_type\":\"invented\",\"occurred_at\":\"2026-08-30T08:00:00Z\"}}");
        var missingSequence = ProtocolEnvelope.Parse("{\"protocol_version\":\"1\",\"message_id\":\"m2\",\"message_type\":\"agent_event\",\"sent_at\":\"2026-08-30T08:00:00Z\",\"payload\":{\"source_type\":\"codex\",\"source_instance\":\"s1\",\"task_id\":\"t1\",\"event_type\":\"task_started\",\"occurred_at\":\"2026-08-30T08:00:00Z\"}}");

        Assert.Throws<AgentProtocolValidationException>(() => AgentProtocolValidator.Validate(unknown));
        Assert.Throws<AgentProtocolValidationException>(() => AgentProtocolValidator.Validate(missingSequence));
    }

    [Theory]
    [InlineData("C:\\\\Users\\\\alice\\\\secret.txt")]
    [InlineData("sk-proj-1234567890")]
    public void Free_text_paths_and_credentials_are_rejected(string unsafeText)
    {
        var message = new AgentEventMessage("codex", "source-1", "task-1", 1, "task_updated", DateTimeOffset.UtcNow, Summary: unsafeText);

        Assert.Throws<AgentProtocolValidationException>(() => AgentProtocolValidator.Validate(
            ProtocolEnvelope.Create("message-1", "agent_event", message)));
    }

    [Fact]
    public void Private_event_is_sanitized_to_anonymous_status()
    {
        var message = new AgentEventMessage("codex", "source-1", "task-1", 1, "attention_required", DateTimeOffset.UtcNow, "Secret title", "Secret summary", IsPrivate: true);

        var sanitized = AgentPayloadSanitizer.Sanitize(message);

        Assert.True(sanitized.IsPrivate);
        Assert.Null(sanitized.Title);
        Assert.Null(sanitized.Summary);
        Assert.Equal("attention_required", sanitized.EventType);
    }

    [Theory]
    [InlineData("C:\\Users\\alice\\project")]
    [InlineData("token=secret-value")]
    public void Opaque_identity_fields_cannot_carry_paths_or_credentials(string unsafeIdentity)
    {
        var message = new AgentEventMessage("codex", "source-1", unsafeIdentity, 1, "task_started", DateTimeOffset.UtcNow);

        Assert.Throws<AgentProtocolValidationException>(() => AgentProtocolValidator.Validate(
            ProtocolEnvelope.Create("message-identity", "agent_event", message)));
    }

    [Fact]
    public void Denylisted_payload_fields_are_rejected_even_if_not_modelled()
    {
        var json = "{\"protocol_version\":\"1\",\"message_id\":\"m1\",\"message_type\":\"agent_event\",\"sent_at\":\"2026-08-30T08:00:00Z\",\"payload\":{\"source_type\":\"codex\",\"source_instance\":\"s1\",\"task_id\":\"t1\",\"sequence\":1,\"event_type\":\"task_started\",\"occurred_at\":\"2026-08-30T08:00:00Z\",\"prompt\":\"do it\"}}";

        Assert.Throws<AgentProtocolValidationException>(() => AgentProtocolValidator.Validate(ProtocolEnvelope.Parse(json)));
    }

    [Fact]
    public void Goal_coverage_cannot_reference_a_different_source_identity()
    {
        var message = new AgentEventMessage(
            "codex", "source-1", "goal-1", 1, "goal_completed", DateTimeOffset.UtcNow,
            CoveredTaskKeys: new[] { "claude/source-2/task-9" });

        Assert.Throws<AgentProtocolValidationException>(() => AgentProtocolValidator.Validate(
            ProtocolEnvelope.Create("message-1", "agent_event", message)));
    }
}
