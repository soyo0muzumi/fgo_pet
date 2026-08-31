using FgoPet.AgentProtocol;
using FgoPet.AgentProtocol.Messages;
using FgoPet.AgentProtocol.Validation;
using Xunit;

namespace FgoPet.AgentProtocol.Tests.Fixtures;

public sealed class RelayResponseContractTests
{
    [Fact]
    public void Runtime_acknowledgements_and_errors_are_responses_only()
    {
        var responses = new[]
        {
            ProtocolEnvelope.Create("a", "authenticate", new { result = "authenticated" }),
            ProtocolEnvelope.Create("b", "authenticate", new { result = "revoked" }),
            ProtocolEnvelope.Create("c", "agent_event", new { result = "queued" }),
            ProtocolEnvelope.Create("d", "dispatch_task", new { result = "accepted", dispatch_request_id = "dispatch-1" }),
            ProtocolEnvelope.Create("e", "decide_registration", new { result = "ok" }),
            ProtocolEnvelope.Create("f", "update_permissions", new { result = "ok" }),
            ProtocolEnvelope.Create("g", "revoke_source", new { result = "ok" }),
            ProtocolEnvelope.Create("h", "error", new { result = "unsupported_operation", error = "unsupported_operation" }),
        };
        foreach (var response in responses)
        {
            AgentProtocolValidator.ValidateResponse(response);
            Assert.Throws<AgentProtocolValidationException>(() => AgentProtocolValidator.Validate(response));
        }
        Assert.Throws<AgentProtocolValidationException>(() => AgentProtocolValidator.ValidateResponse(
            ProtocolEnvelope.Create("i", "authenticate", new { result = "anything" })));
    }

    [Fact]
    public void Source_collections_validate_each_item_and_cannot_be_sent_as_requests()
    {
        var response = ProtocolEnvelope.Create("sources", "pending_sources", new
        {
            result = "pending_sources",
            sources = new[] { new PendingSourceDto("request-1", "codex", "Codex", "source-1", "1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(10)) },
        });
        AgentProtocolValidator.ValidateResponse(response);
        Assert.Throws<AgentProtocolValidationException>(() => AgentProtocolValidator.Validate(response));
        AgentProtocolValidator.ValidateResponse(ProtocolEnvelope.Create("empty", "list_sources", new { result = "ok", sources = Array.Empty<ApprovedSourceDto>() }));
        Assert.Throws<AgentProtocolValidationException>(() => AgentProtocolValidator.ValidateResponse(
            ProtocolEnvelope.Create("bad", "list_sources", new { result = "ok", sources = new[] { new { enabled = true } } })));
    }

    [Fact]
    public void Status_responses_validate_embedded_envelopes_and_reject_privacy_leaks()
    {
        var message = ProtocolEnvelope.Create("event", "agent_event", new AgentEventMessage("codex", "source-1", "task-1", 1, "task_started", DateTimeOffset.UtcNow));
        AgentProtocolValidator.ValidateResponse(ProtocolEnvelope.Create("events", "status_check", new { result = "status", events = new[] { message.ToJson() } }));
        AgentProtocolValidator.ValidateResponse(ProtocolEnvelope.Create("dispatches", "status_check", new { result = "dispatches", dispatches = Array.Empty<string>() }));
        Assert.Throws<AgentProtocolValidationException>(() => AgentProtocolValidator.ValidateResponse(
            ProtocolEnvelope.Create("bad", "status_check", new { result = "status", events = new[] { "{}" } })));
        Assert.Throws<AgentProtocolValidationException>(() => AgentProtocolValidator.ValidateResponse(
            ProtocolEnvelope.Create("bad", "status_check", new { result = "status", events = new[] { new { credential = "do-not-leak" } } })));
        Assert.Throws<AgentProtocolValidationException>(() => AgentProtocolValidator.Validate(
            ProtocolEnvelope.Create("bad", "status_check", new { events = new[] { message.ToJson() } })));
    }
}
