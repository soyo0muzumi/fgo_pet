using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using FgoPet.Core.Dialogue;
using FgoPet.Infrastructure.Providers;
using FgoPet.Infrastructure.Secrets;
using FgoPet.Kernel.Agent;
using Xunit;

namespace FgoPet.Infrastructure.Tests.Providers;

public sealed class OrdinaryWireToolNameTests
{
    [Theory]
    [InlineData(false,false)]
    [InlineData(false,true)]
    [InlineData(true,false)]
    public async Task Shared_wire_projection_preserves_schema_and_request_local_name_identity(bool native,bool unknown)
    {
        const string canonical="experience.read";
        var alias=ModelToolNameMap.GetWireName(canonical);
        var returned=unknown?"fgo_unoffered_function":alias;
        var handler=new Reply(returned);using var client=new HttpClient(handler);
        var provider=new OpenAiCompatibleChatProvider("deepseek",new Uri("https://api.deepseek.com/v1"),"deepseek-flash",new Credentials(),client);
        var definition=new ChatToolDefinition(native?alias:canonical,"Read one synthetic original.",
            """{"type":"object","properties":{"id":{"type":"string"}},"required":["id"],"additionalProperties":false}""");
        var request=native?ChatRequest.CreateAgent("m","c",[new(ModelMessageRole.User,"Synthetic",[])],tools:[definition],maxOutputTokens:2048):
            new ChatRequest("m","c",[new(ChatMessageRole.User,"Synthetic")],tools:[definition],maxOutputTokens:2048);
        var chunks=new List<ChatStreamChunk>();await foreach(var chunk in provider.StreamAsync(request,default))chunks.Add(chunk);
        using var wire=JsonDocument.Parse(handler.Body!);
        var function=Assert.Single(wire.RootElement.GetProperty("tools").EnumerateArray()).GetProperty("function");
        Assert.Matches("^[A-Za-z0-9_-]{1,64}$",function.GetProperty("name").GetString()!);
        Assert.Equal(alias,function.GetProperty("name").GetString());
        Assert.Equal(definition.Description,function.GetProperty("description").GetString());
        Assert.Equal(definition.Parameters.GetRawText(),function.GetProperty("parameters").GetRawText());
        Assert.Equal(native?alias:canonical,definition.Name);
        var call=Assert.Single(chunks.Where(c=>c.ToolCallDelta?.Name is not null)).ToolCallDelta!;
        Assert.Equal(unknown?returned:native?alias:canonical,call.Name);
        using var input=JsonDocument.Parse(ChatRequestInputEnvelope.Write("deepseek-flash",request));
        Assert.Equal(function.GetRawText(),input.RootElement.GetProperty("tools")[0].GetProperty("function").GetRawText());
    }
    private sealed class Credentials:ICredentialReader
    {public Task<string?> ReadAsync(string target,CancellationToken token)=>Task.FromResult<string?>("synthetic-only-key");}
    private sealed class Reply(string name):HttpMessageHandler
    {
        public string? Body;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            Body=await request.Content!.ReadAsStringAsync(token);
            var frame=JsonSerializer.Serialize(new{choices=new[]{new{delta=new{tool_calls=new[]{new{index=0,id="fixture-call",function=new{name,arguments="{}"}}}},finish_reason="tool_calls"}},
                usage=new{prompt_tokens=100,completion_tokens=10}});
            return new(HttpStatusCode.OK){Content=new StringContent("data: "+frame+"\n\ndata: [DONE]\n\n",Encoding.UTF8,"text/event-stream")};
        }
    }
}
