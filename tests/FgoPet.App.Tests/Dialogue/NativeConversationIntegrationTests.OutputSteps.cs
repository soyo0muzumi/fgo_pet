using System.Text.Json;
using FgoPet.App.Dialogue;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Portraits;
using FgoPet.Extensibility;
using FgoPet.Kernel.Agent;
using Xunit;

namespace FgoPet.App.Tests.Dialogue;

public sealed partial class NativeConversationIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_emotion_defaults_to_neutral_without_format_repair(bool withTool)
    {
        const string raw = "{\"text\":\"offline reply\"}";
        var final = ProviderTurn.Final(raw);
        var unexpectedRepair = ProviderTurn.Final("{\"text\":\"must not regenerate\",\"emotion\":\"neutral\"}");
        var provider = new ScriptedProvider(withTool
            ? [ProviderTurn.Call("fixture.read", "read-once", "{}"), ProviderTurn.Call(FinalizeTool, "finish", "{}"), final, unexpectedRepair]
            : [final, unexpectedRepair]) { RawFinalResponses = true, AutoFinalize = false };
        var tool = withTool ? new RecordingTool(ToolEffect.ReadOnly) : null;
        var observer = new RecordingObserver();
        var updates = new List<ConversationUpdate>();
        await using var host = await TestHost.CreateAsync(provider, tool, observer);
        host.Orchestrator.Updated += updates.Add;

        var result = await host.Orchestrator.SendAsync(RoleId, "Answer after any needed read.", default, new(ProjectId));

        Assert.Equal(ConversationSendStatus.Completed, result.Status);
        var expectedRequests = withTool ? 3 : 1;
        Assert.Equal(expectedRequests, provider.Requests.Count);
        AssertFinalRequest(provider.Requests[^1]);
        if (withTool)
        {
            Assert.Single(tool!.Invocations);
            Assert.All(provider.Requests.Take(2), AssertToolRequest);
        }
        Assert.Equal("offline reply", Assert.Single(observer.Turns).AssistantText);
        Assert.Equal(ExpressionSemantic.Neutral,
            Assert.Single(updates.Where(update => update.Type == ConversationUpdateType.AssistantCompleted)).Expression);
        Assert.Equal(1, host.CountRows("agent_final_deliveries"));
        Assert.Equal("offline reply", Assert.Single(host.Conversations.LoadMessages(result.ConversationId, RoleId)
            .Where(message => message.Role == ChatMessageRole.Assistant)).Text);
        var run = await host.Runs.LoadAsync(result.RunId!, default);
        Assert.Equal(expectedRequests, run!.Snapshot.ModelRequests);
        Assert.Equal(raw, run.FinalText);
    }

    [Fact]
    public async Task Compliant_final_is_delivered_in_the_original_request_without_regeneration()
    {
        var provider = new ScriptedProvider(
            ProviderTurn.Final("{\"text\":\"direct final\",\"emotion\":\"neutral\"}")) { RawFinalResponses = true };
        var observer = new RecordingObserver();
        await using var host = await TestHost.CreateAsync(provider, null, observer);
        var result = await host.Orchestrator.SendAsync(RoleId, "Answer directly.", default, new(ProjectId));
        Assert.Equal(ConversationSendStatus.Completed, result.Status);
        var request = Assert.Single(provider.Requests);
        Assert.Contains(request.ModelMessages, message => message.Role == ModelMessageRole.System
            && message.Content == StructuredOutputValidator.FinalStepInstructions);
        Assert.Equal("direct final", Assert.Single(observer.Turns).AssistantText);
        var run = await host.Runs.LoadAsync(result.RunId!, default);
        Assert.Equal(1, run!.Snapshot.StepNumber);
        Assert.Equal(1, run.Snapshot.ModelRequests);
        Assert.Equal(1, host.CountRows("agent_final_deliveries"));
    }

    [Fact]
    public async Task Tool_then_handoff_then_final_uses_three_requests_without_draft()
    {
        var provider = new ScriptedProvider(ProviderTurn.Call("fixture.read", "read-once", "{}"),
            ProviderTurn.Final("{\"text\":\"observed final\",\"emotion\":\"happy\"}")) { RawFinalResponses = true };
        var tool = new RecordingTool(ToolEffect.ReadOnly);
        var observer = new RecordingObserver();
        await using var host = await TestHost.CreateAsync(provider, tool, observer);
        var result = await host.Orchestrator.SendAsync(RoleId, "Read once and answer.", default, new(ProjectId));
        Assert.Equal(ConversationSendStatus.Completed, result.Status);
        Assert.Equal(3, provider.Requests.Count);
        Assert.Single(tool.Invocations);
        Assert.All(provider.Requests.Take(2), AssertToolRequest);
        AssertFinalRequest(provider.Requests[2]);
        Assert.Contains(provider.Requests[1].ModelMessages, message => message.Role == ModelMessageRole.Tool
            && message.ToolCallId == "read-once");
        Assert.Equal("observed final", Assert.Single(observer.Turns).AssistantText);
        var run = await host.Runs.LoadAsync(result.RunId!, default);
        Assert.Equal(3, run!.Snapshot.StepNumber);
        Assert.Equal(3, run.Snapshot.ModelRequests);
    }

    [Theory]
    [InlineData("ordinary final")]
    [InlineData("{\"text\":\"unfinished\"")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{\"text\":\"answer\",\"emotion\":null}")]
    [InlineData("{\"text\":\"answer\",\"emotion\":7}")]
    [InlineData("{\"text\":\"answer\",\"emotion\":\"\"}")]
    [InlineData("{\"text\":7,\"emotion\":\"neutral\"}")]
    [InlineData("{\"text\":\" \",\"emotion\":\"neutral\"}")]
    [InlineData("{\"text\":\"answer\",\"emotion\":\"unknown\"}")]
    [InlineData("{\"text\":\"one\",\"text\":\"two\",\"emotion\":\"neutral\"}")]
    public async Task Invalid_original_final_uses_only_one_tool_free_format_repair(string invalid)
    {
        var provider = new ScriptedProvider(ProviderTurn.Final(invalid),
            ProviderTurn.Final("{\"text\":\"repaired\",\"emotion\":\"neutral\"}")) { RawFinalResponses = true };
        var observer = new RecordingObserver();
        var tool = new RecordingTool(ToolEffect.ReadOnly);
        await using var host = await TestHost.CreateAsync(provider, tool, observer);
        var result = await host.Orchestrator.SendAsync(RoleId, "Answer.", default, new(ProjectId));
        Assert.Equal(ConversationSendStatus.Completed, result.Status);
        Assert.Equal(3, provider.Requests.Count);
        Assert.NotEmpty(provider.Requests[0].Tools!);
        AssertFinalRequest(provider.Requests[1]);
        AssertFinalRequest(provider.Requests[2]);
        Assert.Empty(tool.Invocations);
        Assert.Equal("repaired", Assert.Single(observer.Turns).AssistantText);
        var run = await host.Runs.LoadAsync(result.RunId!, default);
        Assert.Equal(2, run!.Snapshot.StepNumber);
        Assert.Equal(3, run.Snapshot.ModelRequests);
    }

    [Fact]
    public async Task Invalid_final_and_invalid_repair_never_deliver_a_plain_fallback()
    {
        var provider = new ScriptedProvider(ProviderTurn.Final("plain final"),
            ProviderTurn.Final("plain repair"), ProviderTurn.Final("must not send")) { RawFinalResponses = true };
        var observer = new RecordingObserver();
        await using var host = await TestHost.CreateAsync(provider, null, observer);
        var result = await host.Orchestrator.SendAsync(RoleId, "Answer.", default, new(ProjectId));
        Assert.Equal(ConversationSendStatus.Failed, result.Status);
        Assert.Equal(2, provider.Requests.Count);
        Assert.Empty(observer.Turns);
        Assert.Equal(0, host.CountRows("agent_final_deliveries"));
        Assert.Equal("MODEL_INVALID_FINAL", (await host.Runs.LoadAsync(result.RunId!, default))!.Snapshot.ErrorCode);
    }

    [Fact]
    public async Task Tool_call_in_format_repair_fails_without_executing_it()
    {
        var provider = new ScriptedProvider(ProviderTurn.Final("plain final"),
            ProviderTurn.Call("fixture.read", "forbidden", "{}")) { RawFinalResponses = true };
        var tool = new RecordingTool(ToolEffect.ReadOnly);
        var observer = new RecordingObserver();
        await using var host = await TestHost.CreateAsync(provider, tool, observer);
        var result = await host.Orchestrator.SendAsync(RoleId, "Answer.", default, new(ProjectId));
        Assert.Equal(ConversationSendStatus.Failed, result.Status);
        Assert.Equal(3, provider.Requests.Count);
        Assert.Empty(tool.Invocations);
        Assert.Empty(observer.Turns);
    }

    [Fact]
    public async Task Original_final_cancellation_prevents_delivery_without_regeneration()
    {
        using var cancellation = new CancellationTokenSource();
        var provider = new ScriptedProvider(
            ProviderTurn.Final("{\"text\":\"must not deliver\",\"emotion\":\"neutral\"}")) { RawFinalResponses = true };
        provider.RequestReceived = _ => cancellation.Cancel();
        var observer = new RecordingObserver();
        await using var host = await TestHost.CreateAsync(provider, null, observer);
        var result = await host.Orchestrator.SendAsync(RoleId, "Answer.", cancellation.Token, new(ProjectId));
        Assert.Equal(ConversationSendStatus.Cancelled, result.Status);
        Assert.Single(provider.Requests);
        Assert.Empty(observer.Turns);
        Assert.Equal(0, host.CountRows("agent_final_deliveries"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Explicit_json_literal_belongs_inside_output_text(bool rawLiteral)
    {
        const string literal = "{\"text\":\"unfinished literal\"";
        var envelope = JsonSerializer.Serialize(new { text = literal, emotion = "neutral" });
        var provider = new ScriptedProvider(rawLiteral
            ? [ProviderTurn.Final(literal), ProviderTurn.Final(envelope)] : [ProviderTurn.Final(envelope)])
            { RawFinalResponses = true };
        var observer = new RecordingObserver();
        await using var host = await TestHost.CreateAsync(provider, null, observer);
        var result = await host.Orchestrator.SendAsync(RoleId, "Quote exactly: " + literal, default, new(ProjectId));
        Assert.Equal(ConversationSendStatus.Completed, result.Status);
        Assert.Equal(rawLiteral ? 2 : 1, provider.Requests.Count);
        Assert.Equal(literal, Assert.Single(observer.Turns).AssistantText);
    }

    [Fact]
    public async Task Length_still_stops_before_format_repair()
    {
        var provider = new ScriptedProvider(new ProviderTurn(
            [new ChatStreamChunk("{\"text\":\"partial\"", IsComplete: true, FinishReason: "length")]))
            { RawFinalResponses = true };
        var observer = new RecordingObserver();
        await using var host = await TestHost.CreateAsync(provider, null, observer);
        var result = await host.Orchestrator.SendAsync(RoleId, "Answer.", default, new(ProjectId));
        Assert.Equal(ConversationSendStatus.Failed, result.Status);
        Assert.Single(provider.Requests);
        Assert.Empty(observer.Turns);
        Assert.Equal("MODEL_OUTPUT_LIMIT", (await host.Runs.LoadAsync(result.RunId!, default))!.Snapshot.ErrorCode);
    }
}
