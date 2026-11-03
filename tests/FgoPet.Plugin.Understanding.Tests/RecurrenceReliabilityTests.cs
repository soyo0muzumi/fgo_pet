using System.Runtime.CompilerServices;
using System.Text.Json;
using FgoPet.Core.Dialogue;
using FgoPet.Extensibility;
using FgoPet.Infrastructure.Providers;
using FgoPet.Kernel.Agent;
using FgoPet.Understanding;
using FgoPet.Understanding.Contracts;
using Xunit;

namespace FgoPet.Plugin.Understanding.Tests;

public sealed class RecurrenceReliabilityTests
{
    private static readonly ConversationSourceSnapshot Source = new(new("c", "mash", "p"), "a", new('A',64),
        DateTimeOffset.UtcNow.AddHours(-1), ConversationSourceKind.Assistant, "Synthetic", 1);
    private static readonly ReviewModelInput Input = new("event", new("mash", "p"),
        [new(Source, "I would compare both reasons before deciding.", new(Source,0,44,44))], [], _ => { });

    [Theory] [InlineData(true)] [InlineData(false)]
    public async Task Host_binds_original_text_and_an_independent_unsupported_verdict_prevents_a_clue(bool supported)
    {
        var provider = new Scripted(supported); var model = new RecurrenceReliabilityModel(provider, Route(provider), new RequestTokenMeter());
        var result = await model.DiscoverAsync(Input, []);
        Assert.Equal(supported ? 1 : 0, result.Clues.Count);
        if (supported) Assert.Equal(Input.Originals[0].Text, Assert.Single(result.Clues[0].Expressions).Quote);
        Assert.Equal(2, provider.Calls); Assert.Empty(result.RetrievedIds);
    }
    [Theory] [InlineData("invented")] [InlineData("s2")]
    public async Task Unknown_or_user_source_cannot_be_host_bound(string alias)
    {
        var user = Source with { Kind = ConversationSourceKind.User, MessageId = "u" };
        var input = Input with { Originals = [Input.Originals[0], new(user,"user",new(user,0,4,4))] };
        var provider = new Scripted(true) { Alias = alias };
        var model = new RecurrenceReliabilityModel(provider, Route(provider), new RequestTokenMeter());
        await Assert.ThrowsAsync<AgentProtocolException>(() => model.DiscoverAsync(input, []));
    }
    [Fact]
    public async Task Broad_eight_can_be_reranked_to_a_different_subset_without_extra_authority()
    {
        var provider = new Scripted(true); var model = new RecurrenceReliabilityModel(provider,Route(provider),new RequestTokenMeter());
        var clues = Enumerable.Range(1,8).Select(i=>new RecurrenceClue("id"+i,"observed", "Assistant compared reasons.",
            [new(new("old"+i,"m",1,Source.OccurredAtUtc), Source with { Scope = Source.Scope with { ConversationId = "old"+i } },44)],
            Source.OccurredAtUtc,Source.OccurredAtUtc.AddDays(14))).ToArray();
        var selected = await model.RetrieveAsync(Input,clues,new([],[]));
        Assert.Equal(8,selected.BroadIds.Count); Assert.Equal(["id2"],selected.RetrievedIds); Assert.Equal(2,provider.Calls);
    }
    [Theory] [InlineData("unknown")] [InlineData("duplicate")] [InlineData("over_limit")]
    public async Task Compact_rerank_still_rejects_unknown_duplicate_and_over_limit_selections(string invalid)
    {
        var provider=new Scripted(true) { Top=invalid switch {"unknown"=>["c9"],"duplicate"=>["c1","c1"],_=>["c1","c2","c3","c4"]} };
        var model=new RecurrenceReliabilityModel(provider,Route(provider),new RequestTokenMeter());
        var clues=Enumerable.Range(1,8).Select(i=>new RecurrenceClue("id"+i,"observed","Assistant compared reasons.",[],Source.OccurredAtUtc,Source.OccurredAtUtc.AddDays(14))).ToArray();
        await Assert.ThrowsAsync<AgentProtocolException>(()=>model.RetrieveAsync(Input,clues,new([],[])));
    }
    private static ModelRouteKey Route(Scripted p) => new(p.ProviderId,"fixture",p.ModelId,"fixture");
    private sealed class Scripted(bool supported):IChatProvider
    {
        public int Calls; public string Alias="s1"; public string[] Top=["c2"];
        public string ProviderId=>"fixture";public string ModelId=>"fixture";
        public Task<IReadOnlyList<ProviderModel>> ListModelsAsync(CancellationToken token)=>Task.FromResult<IReadOnlyList<ProviderModel>>([]);
        public async IAsyncEnumerable<ChatStreamChunk> StreamAsync(ChatRequest request,[EnumeratorCancellation] CancellationToken token)
        {
            Calls++; using var d=JsonDocument.Parse(request.ModelMessages.Last().Content);var root=d.RootElement;
            var binding=root.GetProperty("request").GetString();var phase=root.GetProperty("phase").GetString();
            object body=phase switch {
                "detect"=>new{schema=1,request=binding,observations=new[]{new{sources=new[]{Alias},theme="reason comparison",observation="Assistant compares both reasons before deciding."}}},
                "validate"=>new{schema=1,request=binding,verdicts=new[]{new{observation="o1",supported,reason=supported?"SUPPORTED":"UNSUPPORTED"}}},
                "recall"=>new{schema=1,request=binding,recall=Enumerable.Range(1,8).Select(i=>"c"+i).ToArray()},
                _=>new{schema=1,request=binding,top=Top}
            };
            yield return new(JsonSerializer.Serialize(body));yield return new("",IsComplete:true,FinishReason:"stop",Usage:new(100,100));await Task.CompletedTask;
        }
    }
}
