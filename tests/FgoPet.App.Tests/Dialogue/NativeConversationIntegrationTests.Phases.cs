using System.Text.Json;
using FgoPet.App.Dialogue;
using FgoPet.Core.Dialogue;
using FgoPet.Extensibility;
using FgoPet.Kernel.Agent;
using Xunit;

namespace FgoPet.App.Tests.Dialogue;

public sealed partial class NativeConversationIntegrationTests
{
    private const string FinalizeTool = "response.finalize";
    private static void AssertFinalRequest(ChatRequest request)
    {
        Assert.True(request.Tools is null or { Count: 0 });
        var template = Assert.Single(request.ModelMessages.Where(m => m.Role == ModelMessageRole.System
            && m.Content == StructuredOutputValidator.FinalStepInstructions));
        Assert.Same(template, request.ModelMessages[^1]);
        Assert.DoesNotContain("Tools if needed", template.Content, StringComparison.Ordinal);
    }
    private static void AssertToolRequest(ChatRequest request)
    {
        var control = Assert.Single(request.Tools!, tool => tool.Name == ModelToolNameMap.GetWireName(FinalizeTool));
        var instruction = request.ModelMessages[^1];
        Assert.Equal(ModelMessageRole.System, instruction.Role);
        Assert.Contains("do not answer the user in this step", instruction.Content, StringComparison.Ordinal);
        Assert.Contains($"first call {control.Name} alone with {{}} and no text", instruction.Content, StringComparison.Ordinal);
        Assert.Contains("The host will then send a separate final-output request with no tools for your answer",
            instruction.Content, StringComparison.Ordinal);
        Assert.DoesNotContain(FinalizeTool, instruction.Content, StringComparison.Ordinal);
        Assert.Single(request.ModelMessages.Where(message => message.Role == ModelMessageRole.System
            && message.Content == instruction.Content));
        Assert.DoesNotContain(request.ModelMessages, message => message.Role == ModelMessageRole.System
            && message.Content == StructuredOutputValidator.FinalStepInstructions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Phase_tool_instruction_names_offered_control_and_explains_separate_answer(bool withRead)
    {
        var signal = ProviderTurn.Call(FinalizeTool, "finish", "{}");
        var final = ProviderTurn.Final("{\"text\":\"answer\",\"emotion\":\"neutral\"}");
        var provider = new ScriptedProvider(withRead
            ? [ProviderTurn.Call("fixture.read", "read-once", "{}"), signal, final]
            : [signal, final]) { RawFinalResponses = true, AutoFinalize = false };
        var tool = new RecordingTool(ToolEffect.ReadOnly);
        var observer = new RecordingObserver();
        var settings = TestSettingsStore.Create();
        settings.Save(settings.Load() with
        {
            ModelConnection = new("fixture", "https://fixture.invalid/v1", "fixture-model",
                contextWindowOverride: 8192, maxOutputTokens: 2048),
        });
        await using var host = await TestHost.CreateAsync(provider, tool, observer, settings);

        var result = await host.Orchestrator.SendAsync(RoleId, "Answer after any needed read.", default, new(ProjectId));

        Assert.Equal(ConversationSendStatus.Completed, result.Status);
        var expectedRequests = withRead ? 3 : 2;
        Assert.Equal(expectedRequests, provider.Requests.Count);
        Assert.All(provider.Requests, request => Assert.Equal(2048, request.MaxOutputTokens));
        Assert.All(provider.Requests.Take(expectedRequests - 1), AssertToolRequest);
        var toolInstruction = provider.Requests[0].ModelMessages[^1].Content;
        var finalRequest = provider.Requests[^1];
        AssertFinalRequest(finalRequest);
        Assert.DoesNotContain(finalRequest.ModelMessages, message => message.Content == toolInstruction);
        Assert.DoesNotContain(finalRequest.ModelMessages, message => message.ToolCallId == "finish");
        Assert.Equal(withRead ? 1 : 0, tool.Invocations.Count);
        Assert.Equal("answer", Assert.Single(observer.Turns).AssistantText);
        Assert.Equal(expectedRequests, (await host.Runs.LoadAsync(result.RunId!, default))!.Snapshot.ModelRequests);
    }

    [Fact]
    public async Task Phase_direct_without_tools_has_one_final_request_and_template_last()
    {
        var provider = new ScriptedProvider(ProviderTurn.Final("{\"text\":\"direct\",\"emotion\":\"neutral\"}")) { RawFinalResponses = true };
        var observer = new RecordingObserver();
        await using var host = await TestHost.CreateAsync(provider, null, observer);
        Assert.Equal(ConversationSendStatus.Completed, (await host.Orchestrator.SendAsync(RoleId, "Answer directly.", default, new(ProjectId))).Status);
        AssertFinalRequest(Assert.Single(provider.Requests));
        Assert.Equal("direct", Assert.Single(observer.Turns).AssistantText);
    }

    [Fact]
    public async Task Phase_empty_handoff_then_final_has_two_requests_and_no_business_effect()
    {
        var provider = new ScriptedProvider(ProviderTurn.Call(FinalizeTool, "finish", "{}"),
            ProviderTurn.Final("{\"text\":\"answer\",\"emotion\":\"neutral\"}")) { RawFinalResponses = true };
        var tool = new RecordingTool(ToolEffect.ReadOnly);
        var observer = new RecordingObserver();
        await using var host = await TestHost.CreateAsync(provider, tool, observer);
        var result = await host.Orchestrator.SendAsync(RoleId, "Answer without using the business tool.", default, new(ProjectId));
        Assert.Equal(ConversationSendStatus.Completed, result.Status);
        Assert.Equal(2, provider.Requests.Count);
        AssertToolRequest(provider.Requests[0]);
        AssertFinalRequest(provider.Requests[1]);
        Assert.DoesNotContain(provider.Requests[1].ModelMessages, message => message.ToolCallId == "finish");
        Assert.Empty(tool.Invocations);
        Assert.Equal("answer", Assert.Single(observer.Turns).AssistantText);
        var run = (await host.Runs.LoadAsync(result.RunId!, default))!;
        Assert.Equal(2, run.Snapshot.ModelRequests);
        Assert.Equal(1, run.Snapshot.ToolCalls);
        Assert.Contains(run.CompletedSteps.SelectMany(step => step.Calls), call => call.Call.Name == FinalizeTool && call.Status == AgentCallStatus.Completed);
    }

    [Fact]
    public async Task Phase_multiple_business_steps_stay_tools_until_explicit_handoff()
    {
        var provider = new ScriptedProvider(ProviderTurn.Call("fixture.read", "first", "{}"),
            ProviderTurn.Call("fixture.read", "second", "{}"), ProviderTurn.Call(FinalizeTool, "finish", "{}"),
            ProviderTurn.Final("{\"text\":\"done\",\"emotion\":\"happy\"}")) { RawFinalResponses = true };
        var tool = new RecordingTool(ToolEffect.ReadOnly);
        await using var host = await TestHost.CreateAsync(provider, tool, new RecordingObserver());
        Assert.Equal(ConversationSendStatus.Completed, (await host.Orchestrator.SendAsync(RoleId, "Read twice, then answer.", default, new(ProjectId))).Status);
        Assert.Equal(4, provider.Requests.Count);
        Assert.All(provider.Requests.Take(3), AssertToolRequest);
        AssertFinalRequest(provider.Requests[3]);
        Assert.Equal(2, tool.Invocations.Count);
    }

    [Fact]
    public async Task Phase_plain_answer_in_tools_is_rejected_without_draft_regeneration()
    {
        var provider = new ScriptedProvider(ProviderTurn.Final("must not become a draft"), ProviderTurn.Final("must not send")) { RawFinalResponses = true, AutoFinalize = false };
        var observer = new RecordingObserver();
        await using var host = await TestHost.CreateAsync(provider, new RecordingTool(ToolEffect.ReadOnly), observer);
        var result = await host.Orchestrator.SendAsync(RoleId, "Use the phase protocol.", default, new(ProjectId));
        Assert.Equal(ConversationSendStatus.Failed, result.Status);
        Assert.Single(provider.Requests);
        Assert.Empty(observer.Turns);
        Assert.Equal("MODEL_EXPECTED_FINALIZE", (await host.Runs.LoadAsync(result.RunId!, default))!.Snapshot.ErrorCode);
    }

    [Theory]
    [InlineData("{\"text\":\"draft\"}")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{")]
    public async Task Phase_handoff_rejects_nonempty_or_invalid_arguments(string arguments)
    {
        var provider = new ScriptedProvider(ProviderTurn.Call(FinalizeTool, "bad-finish", arguments),
            ProviderTurn.Call(FinalizeTool, "bad-correction", arguments)) { RawFinalResponses = true, AutoFinalize = false };
        var tool = new RecordingTool(ToolEffect.ReadOnly);
        await using var host = await TestHost.CreateAsync(provider, tool, new RecordingObserver());
        var result = await host.Orchestrator.SendAsync(RoleId, "Use the phase protocol.", default, new(ProjectId));
        Assert.Equal(ConversationSendStatus.Failed, result.Status);
        Assert.Equal(2, provider.Requests.Count);
        AssertControlRepair(provider.Requests[1]);
        Assert.Empty(tool.Invocations);
        Assert.Equal("MODEL_INVALID_FINALIZE", (await host.Runs.LoadAsync(result.RunId!, default))!.Snapshot.ErrorCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Phase_handoff_with_body_or_sibling_is_rejected_before_any_effect(bool sibling)
    {
        var signal = ProviderTurn.Call(FinalizeTool, "finish", "{}");
        var chunks = signal.Chunks.Take(signal.Chunks.Count - 1).ToList();
        chunks.Add(sibling ? new ChatStreamChunk("", ToolCallDelta: new(1, id: "sibling",
            name: ModelToolNameMap.GetWireName("fixture.read"), argumentsDelta: "{}")) : new ChatStreamChunk("forbidden answer draft"));
        chunks.Add(signal.Chunks[^1]);
        var provider = new ScriptedProvider(new ProviderTurn(chunks), new ProviderTurn(chunks)) { RawFinalResponses = true, AutoFinalize = false };
        var tool = new RecordingTool(ToolEffect.ReadOnly);
        await using var host = await TestHost.CreateAsync(provider, tool, new RecordingObserver());
        var result = await host.Orchestrator.SendAsync(RoleId, "Use the phase protocol.", default, new(ProjectId));
        Assert.Equal(ConversationSendStatus.Failed, result.Status);
        Assert.Equal(2, provider.Requests.Count);
        AssertControlRepair(provider.Requests[1]);
        Assert.Empty(tool.Invocations);
        var run = (await host.Runs.LoadAsync(result.RunId!, default))!;
        Assert.Equal("MODEL_INVALID_FINALIZE", run.Snapshot.ErrorCode);
        Assert.Equal(0, run.Snapshot.ToolCalls);
    }

    [Fact]
    public async Task Phase_final_tool_call_fails_without_reopening_tools()
    {
        var provider = new ScriptedProvider(ProviderTurn.Call(FinalizeTool, "finish", "{}"),
            ProviderTurn.Call("fixture.read", "forbidden", "{}")) { RawFinalResponses = true };
        var tool = new RecordingTool(ToolEffect.ReadOnly);
        await using var host = await TestHost.CreateAsync(provider, tool, new RecordingObserver());
        var result = await host.Orchestrator.SendAsync(RoleId, "Answer.", default, new(ProjectId));
        Assert.Equal(ConversationSendStatus.Failed, result.Status);
        Assert.Equal(2, provider.Requests.Count);
        AssertFinalRequest(provider.Requests[1]);
        Assert.Empty(tool.Invocations);
        Assert.Equal("MODEL_UNEXPECTED_TOOL_CALL", (await host.Runs.LoadAsync(result.RunId!, default))!.Snapshot.ErrorCode);
    }

    [Fact]
    public async Task Phase_bad_final_repairs_once_with_template_last_and_no_tool_replay()
    {
        var provider = new ScriptedProvider(ProviderTurn.Call("fixture.read", "read", "{}"),
            ProviderTurn.Call(FinalizeTool, "finish", "{}"), ProviderTurn.Final("bad final"),
            ProviderTurn.Final("{\"text\":\"fixed\",\"emotion\":\"neutral\"}")) { RawFinalResponses = true };
        var tool = new RecordingTool(ToolEffect.ReadOnly);
        await using var host = await TestHost.CreateAsync(provider, tool, new RecordingObserver());
        Assert.Equal(ConversationSendStatus.Completed, (await host.Orchestrator.SendAsync(RoleId, "Read and answer.", default, new(ProjectId))).Status);
        Assert.Equal(4, provider.Requests.Count);
        Assert.All(provider.Requests.Take(2), AssertToolRequest);
        Assert.All(provider.Requests.Skip(2), AssertFinalRequest);
        Assert.Single(tool.Invocations);
        Assert.Contains(provider.Requests[3].ModelMessages, message => message.Role == ModelMessageRole.Assistant && message.Content == "bad final");
    }

    [Fact]
    public async Task Phase_cancellation_at_final_dispatch_never_delivers_or_replays()
    {
        using var cancellation = new CancellationTokenSource();
        var provider = new ScriptedProvider(ProviderTurn.Call(FinalizeTool, "finish", "{}"),
            ProviderTurn.Final("{\"text\":\"not delivered\",\"emotion\":\"neutral\"}")) { RawFinalResponses = true };
        provider.RequestReceived = _ => { if (provider.Requests.Count == 2) cancellation.Cancel(); };
        var tool = new RecordingTool(ToolEffect.ReadOnly);
        var observer = new RecordingObserver();
        await using var host = await TestHost.CreateAsync(provider, tool, observer);
        Assert.Equal(ConversationSendStatus.Cancelled, (await host.Orchestrator.SendAsync(RoleId, "Answer.", cancellation.Token, new(ProjectId))).Status);
        Assert.Equal(2, provider.Requests.Count);
        Assert.Empty(observer.Turns);
        Assert.Empty(tool.Invocations);
    }

    [Fact]
    public async Task Phase_existing_tools_rejected_fallback_builds_a_final_request()
    {
        var provider = new ScriptedProvider(ProviderTurn.Fail(new FgoPet.Infrastructure.Providers.ProviderRequestException(
                FgoPet.Infrastructure.Providers.ProviderFailureCategory.ToolsRejected, "fixture")),
            ProviderTurn.Final("{\"text\":\"fallback\",\"emotion\":\"neutral\"}")) { RawFinalResponses = true };
        var tool = new RecordingTool(ToolEffect.ReadOnly);
        await using var host = await TestHost.CreateAsync(provider, tool, new RecordingObserver());
        Assert.Equal(ConversationSendStatus.Completed, (await host.Orchestrator.SendAsync(RoleId, "Answer.", default, new(ProjectId))).Status);
        Assert.Equal(2, provider.Requests.Count);
        AssertToolRequest(provider.Requests[0]);
        AssertFinalRequest(provider.Requests[1]);
        Assert.Empty(tool.Invocations);
    }
}
