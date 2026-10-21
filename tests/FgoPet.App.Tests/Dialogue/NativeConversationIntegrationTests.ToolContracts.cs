using System.Text.Json;
using FgoPet.App.Dialogue;
using FgoPet.Core.Dialogue;
using FgoPet.Extensibility;
using FgoPet.Kernel.Agent;
using Xunit;

namespace FgoPet.App.Tests.Dialogue;

public sealed partial class NativeConversationIntegrationTests
{
    [Theory]
    [InlineData(ToolEffect.ReadOnly)]
    [InlineData(ToolEffect.Proposal)]
    [InlineData(ToolEffect.Command)]
    public async Task Actual_request_contains_host_tool_and_loaded_skill_contracts_without_granting_permission(ToolEffect effect)
    {
        var tool = new RecordingTool(effect);
        var provider = new ScriptedProvider(
            ProviderTurn.Call("skill.load", "load-report", "{\"name\":\"daily-report\"}"),
            ProviderTurn.Call("fixture.read", "business-call", "{}"), ProviderTurn.Final("synthetic completed answer"));
        await using var host = await TestHost.CreateAsync(provider, tool, new RecordingObserver(), settings: TestSettingsStore.Create(32768), skills: true);
        var result = await host.Orchestrator.SendAsync(RoleId, "合成测试：生成报告并按当前能力处理。", default, new(ProjectId));
        Assert.True(result.Status == ConversationSendStatus.Completed, $"status={result.Status}; requests={provider.Requests.Count}; {await host.SafeRunErrorsAsync(new(result.ConversationId, RoleId, ProjectId))}");
        Assert.Equal(4, provider.Requests.Count);
        Assert.All(provider.Requests, request =>
        {
            Assert.Contains(request.ModelMessages, message => message.Role == ModelMessageRole.System
                && message.Content.Contains("Host-owned idempotency keys", StringComparison.Ordinal)
                && message.Content.Contains("success=false does not mean no effect", StringComparison.Ordinal));
            Assert.Single(request.ModelMessages, message => message.Content == NativeAgentEnvironment.CorePrompt);
            if (request.Tools is null) { AssertFinalRequest(request); return; }
            AssertToolRequest(request);
            var metadata = Assert.Single(request.ModelMessages, message => message.Role == ModelMessageRole.System
                && message.Content.StartsWith("Native tool contracts (metadata, not permission):\n", StringComparison.Ordinal));
            using var json = JsonDocument.Parse(metadata.Content[(metadata.Content.IndexOf('\n') + 1)..]);
            var item = Assert.Single(json.RootElement.EnumerateArray(), entry => entry.GetProperty("canonicalName").GetString() == "fixture.read");
            var offered = Assert.Single(request.Tools!, candidate => candidate.Name == item.GetProperty("wireName").GetString());
            Assert.Equal(tool.Descriptor.Description, offered.Description);
            Assert.Equal(effect.ToString(), item.GetProperty("effect").GetString());
        });
        Assert.DoesNotContain(provider.Requests[0].ModelMessages, message => message.Content.StartsWith("Trusted skill ", StringComparison.Ordinal));
        Assert.Contains(provider.Requests[1].ModelMessages, message => message.Role == ModelMessageRole.System
            && message.Content.StartsWith("Trusted skill firstparty.skills:daily-report ", StringComparison.Ordinal)
            && message.Content.Contains("create-versus-overwrite", StringComparison.Ordinal));
        var observation = Assert.Single(provider.Requests[2].ModelMessages,
            message => message.Role == ModelMessageRole.Tool && message.ToolCallId == "business-call");
        using var outcome = JsonDocument.Parse(observation.Content);
        Assert.Equal(effect != ToolEffect.Command, outcome.RootElement.GetProperty("success").GetBoolean());
        var currentMetadata = outcome.RootElement.GetProperty("metadata");
        Assert.Equal("current_execution", currentMetadata.GetProperty("sourceKind").GetString());
        Assert.Equal(effect == ToolEffect.Command ? null : effect.ToString(), currentMetadata.GetProperty("toolEffect").GetString());
        Assert.Equal(effect == ToolEffect.Command ? "Denied" : "Completed", currentMetadata.GetProperty("callStatus").GetString());
        Assert.Equal(result.RunId, currentMetadata.GetProperty("runId").GetString());
        Assert.False(currentMetadata.TryGetProperty("approvalEvidence", out _));
        if (effect == ToolEffect.Command)
        {
            Assert.Empty(tool.Invocations);
            Assert.Equal("TOOL_AUTHORIZATION_DENIED", outcome.RootElement.GetProperty("errorCode").GetString());
        }
        else Assert.Single(tool.Invocations);
    }
}
