using FgoPet.Core.Dialogue;
using FgoPet.Extensibility;
using Xunit;

namespace FgoPet.App.Tests.Dialogue;

public sealed partial class NativeConversationIntegrationTests
{
    [Theory][InlineData(false)][InlineData(true)]
    public async Task Explicit_task_input_limit_rejects_an_overlarge_mandatory_envelope_before_provider_entry(bool native)
    {
        var scripted=new ScriptedProvider(PersonaFinal("Synthetic reply."));
        await using var host=await TestHost.CreateAsync(new LimitedProvider(scripted,256),null,new RecordingObserver(),
            settings:TestSettingsStore.Create(32768),useNative:native);
        var result=await host.Orchestrator.SendAsync(RoleId,"Synthetic protected current request.",default,new(ProjectId));
        Assert.Equal(ConversationSendStatus.Failed,result.Status);Assert.Empty(scripted.Requests);
    }
    [Fact]
    public async Task Task_input_limit_omits_an_optional_block_whole_and_preserves_the_current_request()
    {
        var scripted=new ScriptedProvider(PersonaFinal("Synthetic reply."));var limited=new LimitedProvider(scripted,8000);
        await using var host=await TestHost.CreateAsync(limited,null,new RecordingObserver(),settings:TestSettingsStore.Create(65536),context:new OversizedWholeContext());
        var result=await host.Orchestrator.SendAsync(RoleId,"Current-request-sentinel",default,new(ProjectId));
        Assert.True(result.Status==ConversationSendStatus.Completed,$"{result.Status}: {result.SafeError}; {await host.SafeRunErrorsAsync(new(result.ConversationId,RoleId,ProjectId))}; requests={scripted.Requests.Count}");Assert.NotEmpty(scripted.Requests);
        var route=ModelRouteKey.From(host.Settings.Load().ModelConnection!);var meter=new FgoPet.Infrastructure.Providers.RequestTokenMeter();
        Assert.All(scripted.Requests,request=>
        {
            Assert.True(meter.Measure(route,request).InputTokens<=8000);
            Assert.Contains(request.ModelMessages,m=>m.Content.Contains("Current-request-sentinel",StringComparison.Ordinal));
            Assert.DoesNotContain(request.ModelMessages,m=>m.Content.Contains("oversized-context-sentinel",StringComparison.Ordinal));
        });
    }
    private sealed class LimitedProvider(IChatProvider inner,int limit):IChatProvider,IChatInputLimit
    {
        public int MaxInputTokens=>limit;public string ProviderId=>inner.ProviderId;public string ModelId=>inner.ModelId;
        public IAsyncEnumerable<ChatStreamChunk> StreamAsync(ChatRequest request,CancellationToken token)=>inner.StreamAsync(request,token);
        public Task<IReadOnlyList<ProviderModel>> ListModelsAsync(CancellationToken token)=>inner.ListModelsAsync(token);
    }
    private sealed class OversizedWholeContext:IAgentContextProvider
    {
        public string Id=>"large-optional";public bool ProvidesTrustedInstructions=>false;
        public ValueTask<IReadOnlyList<AgentContextBlock>> BuildAsync(AgentContextRequest request,CancellationToken token)=>
            ValueTask.FromResult<IReadOnlyList<AgentContextBlock>>([new("oversized-context-sentinel",AgentContextKind.Data,"oversized-context-sentinel"+new string('x',20000),10,Whole:true)]);
    }
}
