using System.Net;
using System.Text;
using FgoPet.Core.Dialogue;
using FgoPet.Infrastructure.Providers;
using Xunit;

namespace FgoPet.Infrastructure.Tests.Providers;

public sealed class ChatDispatchGateTests
{
    [Fact]
    public async Task Revocation_during_async_credentials_stops_the_actual_http_handoff()
    {
        var allowed=true;var gate=new Gate(()=>allowed);using var handler=new SyntheticHttp();using var http=new HttpClient(handler);
        var provider=new OpenAiCompatibleChatProvider("deepseek",new("https://api.deepseek.com/v1"),"deepseek-flash",
            new Credentials(()=>allowed=false),http,gate);
        var request=new ChatRequest("synthetic","conversation",[new(ChatMessageRole.User,"Synthetic only")],maxOutputTokens:2048);
        await Assert.ThrowsAsync<InvalidOperationException>(async()=>{await foreach(var chunk in provider.StreamAsync(request,default)) { }});
        Assert.Equal(1,gate.Checks);Assert.Equal(0,handler.Requests);
    }

    [Fact]
    public async Task Allowed_handoff_enters_the_host_gate_before_http_starts()
    {
        var entered=false;var gate=new Gate(()=>{entered=true;return true;});
        using var handler=new SyntheticHttp(()=>Assert.True(entered));using var http=new HttpClient(handler);
        var provider=new FgoPet.App.Providers.ChatProviderFactory(new ProviderCatalog(),new Credentials(()=>{}),http)
            .Create(new("deepseek","https://api.deepseek.com/v1","deepseek-flash"),null,gate);
        var request=new ChatRequest("synthetic","conversation",[new(ChatMessageRole.User,"Synthetic only")],maxOutputTokens:2048);
        await foreach(var chunk in provider.StreamAsync(request,default)) { }
        Assert.Equal(1,gate.Checks);Assert.Equal(1,handler.Requests);
    }

    private sealed class Gate(Func<bool> allowed):IChatDispatchGate
    {
        public int Checks {get;private set;}
        public ModelRouteKey? LastRoute {get;private set;}
        public T Dispatch<T>(ChatRequest request,ModelRouteKey route,Func<T> start,CancellationToken token)
        {token.ThrowIfCancellationRequested();Checks++;LastRoute=route;if(!allowed())throw new InvalidOperationException("SYNTHETIC_REVOKED");return start();}
    }
    [Fact] public async Task Actual_http_gate_retains_the_hosts_nondefault_connection_revision()
    {
        var connection=new FgoPet.Core.Settings.ModelConnectionSettings("deepseek","https://api.deepseek.com/v1","deepseek-flash",false,32768,2048);
        var gate=new Gate(()=>true);using var handler=new SyntheticHttp();using var http=new HttpClient(handler);
        var provider=new FgoPet.App.Providers.ChatProviderFactory(new ProviderCatalog(),new Credentials(()=>{}),http).Create(connection,null,gate);
        await foreach(var chunk in provider.StreamAsync(new("synthetic","conversation",[new(ChatMessageRole.User,"Synthetic")],maxOutputTokens:2048),default)) { }
        Assert.Equal(ModelRouteKey.From(connection),gate.LastRoute);
    }
    private sealed class Credentials(Action before):FgoPet.Infrastructure.Secrets.ICredentialReader
    {
        public async Task<string?> ReadAsync(string target,CancellationToken token)
        {await Task.Yield();token.ThrowIfCancellationRequested();before();return "synthetic-only-key";}
    }
    private sealed class SyntheticHttp(Action? before=null):HttpMessageHandler
    {
        public int Requests {get;private set;}
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            Requests++;before?.Invoke();return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {Content=new StringContent("data: {\"choices\":[{\"delta\":{\"content\":\"ok\"},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":1}}\n\ndata: [DONE]\n\n",Encoding.UTF8,"text/event-stream")});
        }
    }
}
