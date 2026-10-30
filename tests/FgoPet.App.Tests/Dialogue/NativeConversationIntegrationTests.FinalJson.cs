using FgoPet.Core.Dialogue;
using FgoPet.Extensibility;
using Xunit;

namespace FgoPet.App.Tests.Dialogue;

public sealed partial class NativeConversationIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Only_host_prepared_final_step_requests_json_without_extra_generation(bool withRead)
    {
        var final = ProviderTurn.Final("{\"text\":\"synthetic final\",\"emotion\":\"neutral\"}");
        var provider = new ScriptedProvider(withRead
            ? [ProviderTurn.Call("fixture.read", "read-once", "{}"), ProviderTurn.Call(FinalizeTool, "finish", "{}"), final]
            : [final]) { RawFinalResponses = true, AutoFinalize = false };
        var tool = withRead ? new RecordingTool(ToolEffect.ReadOnly) : null;
        var observer = new RecordingObserver();
        await using var host = await TestHost.CreateAsync(provider, tool, observer);
        var result = await host.Orchestrator.SendAsync(RoleId, "Synthetic final request.", default, new(ProjectId));
        Assert.Equal(ConversationSendStatus.Completed, result.Status);
        Assert.Equal(withRead ? 3 : 1, provider.Requests.Count);
        var last = provider.Requests[^1];
        Assert.Equal("json_object", last.Metadata["fgo_final_output"]);
        Assert.Null(last.Tools);
        Assert.All(provider.Requests.SkipLast(1), request => Assert.False(request.Metadata.ContainsKey("fgo_final_output")));
        Assert.Equal(withRead ? 1 : 0, tool?.Invocations.Count ?? 0);
        Assert.Equal("synthetic final", Assert.Single(observer.Turns).AssistantText);
    }
}
