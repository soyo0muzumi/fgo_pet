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

    [Fact]
    public void Registration_status_round_trips_one_time_credential()
    {
        var value = new RegistrationStatusResponse("approved", "req-1", "codex-abc", "secret", null);
        var copy = ProtocolEnvelope.Create("m1", "registration_status", value)
            .DeserializePayload<RegistrationStatusResponse>();

        Assert.Equal(value, copy);
    }

    [Fact]
    public void App_administration_contracts_cannot_expose_credentials()
    {
        Assert.DoesNotContain(typeof(PendingSourceDto).GetProperties(), p => p.Name.Contains("Credential"));
        Assert.DoesNotContain(typeof(ApprovedSourceDto).GetProperties(), p => p.Name.Contains("Credential"));
    }

    [Fact]
    public void Registration_request_uses_versioned_identity_and_nonce_fields()
    {
        var request = new RegistrationRequestMessage(
            "codex", "Codex", "instance-1", "1.0", "1", Nonce());
        var envelope = ProtocolEnvelope.Create("m1", "registration_request", request);

        AgentProtocolValidator.Validate(envelope);
        var json = envelope.ToJson();

        Assert.Contains("\"source_instance_id\"", json, StringComparison.Ordinal);
        Assert.Contains("\"request_nonce\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Registration_request_rejects_incompatible_protocol_and_nonce()
    {
        var incompatible = new RegistrationRequestMessage(
            "codex", "Codex", "instance-1", "1.0", "2", Nonce());
        var malformedNonce = new RegistrationRequestMessage(
            "codex", "Codex", "instance-1", "1.0", "1", "not-a-nonce");

        Assert.Throws<AgentProtocolValidationException>(() => AgentProtocolValidator.Validate(
            ProtocolEnvelope.Create("m1", "registration_request", incompatible)));
        Assert.Throws<AgentProtocolValidationException>(() => AgentProtocolValidator.Validate(
            ProtocolEnvelope.Create("m2", "registration_request", malformedNonce)));
    }

    [Fact]
    public void Authenticate_and_registration_status_validate_32_byte_base64_credentials()
    {
        var credential = Convert.ToBase64String(new byte[32]);
        var authenticate = new AuthenticateRequest("codex", "instance-1", credential);
        var response = new RegistrationStatusResponse("approved", "req-1", "instance-1", credential, null);

        AgentProtocolValidator.Validate(ProtocolEnvelope.Create("m1", "authenticate", authenticate));
        AgentProtocolValidator.Validate(ProtocolEnvelope.Create("m2", "registration_status", response));

        var invalid = new AuthenticateRequest("codex", "instance-1", "c2VjcmV0");
        Assert.Throws<AgentProtocolValidationException>(() => AgentProtocolValidator.Validate(
            ProtocolEnvelope.Create("m3", "authenticate", invalid)));
    }

    [Fact]
    public void Credential_fields_nested_or_on_requests_remain_forbidden()
    {
        var nested = ProtocolEnvelope.Parse(
            "{\"protocol_version\":\"1\",\"message_id\":\"m1\",\"message_type\":\"authenticate\",\"sent_at\":\"2026-08-30T08:00:00Z\",\"payload\":{\"source_type\":\"codex\",\"source_instance_id\":\"instance-1\",\"credential\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=\",\"metadata\":{\"credential\":\"secret\"}}}");
        var statusRequest = ProtocolEnvelope.Parse(
            "{\"protocol_version\":\"1\",\"message_id\":\"m2\",\"message_type\":\"registration_status\",\"sent_at\":\"2026-08-30T08:00:00Z\",\"payload\":{\"request_id\":\"req-1\",\"source_instance_id\":\"instance-1\",\"request_nonce\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"credential\":\"secret\"}}");

        Assert.Throws<AgentProtocolValidationException>(() => AgentProtocolValidator.Validate(nested));
        Assert.Throws<AgentProtocolValidationException>(() => AgentProtocolValidator.Validate(statusRequest));
    }

    [Fact]
    public void Administration_contracts_validate_decisions_permissions_and_protocol_status()
    {
        var decision = new RegistrationDecisionRequest("req-1", "approve");
        var permissions = new UpdatePermissionsRequest("codex", "instance-1", new[] { "target-1" }, true);
        var revoke = new RevokeSourceRequest("codex", "instance-1");
        var connection = new RelayConnectionTestResponse(
            true, true, true, "1", "connected", DateTimeOffset.UtcNow, null);

        AgentProtocolValidator.Validate(ProtocolEnvelope.Create("m1", "decide_registration", decision));
        AgentProtocolValidator.Validate(ProtocolEnvelope.Create("m2", "update_permissions", permissions));
        AgentProtocolValidator.Validate(ProtocolEnvelope.Create("m3", "revoke_source", revoke));
        AgentProtocolValidator.Validate(ProtocolEnvelope.Create("m4", "connection_test", connection));

        Assert.Throws<AgentProtocolValidationException>(() => AgentProtocolValidator.Validate(
            ProtocolEnvelope.Create("m5", "decide_registration", new RegistrationDecisionRequest("req-1", "maybe"))));
        Assert.Throws<AgentProtocolValidationException>(() => AgentProtocolValidator.Validate(
            ProtocolEnvelope.Create("m6", "update_permissions", new UpdatePermissionsRequest("codex", "instance-1", new[] { "C:\\\\secret" }, true))));
    }

    private static string Nonce() => new('a', 64);
}
