using System.Runtime.CompilerServices;
using System.Text.Json;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Settings;
using FgoPet.Extensibility;
using FgoPet.Infrastructure.Providers;
using FgoPet.Understanding;
using FgoPet.Understanding.Contracts;
using Xunit;

namespace FgoPet.Plugin.Understanding.Tests;

public sealed class EvidenceValidationProtocolTests
{
    [Theory]
    [InlineData(false, "SUPPORTED", UnderstandingEvidenceSupport.Supported)]
    [InlineData(true, "SUPPORTED", UnderstandingEvidenceSupport.Supported)]
    [InlineData(false, "PARTIAL", UnderstandingEvidenceSupport.Partial)]
    [InlineData(true, "PARTIAL", UnderstandingEvidenceSupport.Partial)]
    [InlineData(false, "UNSUPPORTED", UnderstandingEvidenceSupport.Unsupported)]
    [InlineData(true, "UNSUPPORTED", UnderstandingEvidenceSupport.Unsupported)]
    [InlineData(false, "CONTRADICTED", UnderstandingEvidenceSupport.Contradicted)]
    [InlineData(true, "CONTRADICTED", UnderstandingEvidenceSupport.Contradicted)]
    public async Task Validation_is_discrete_read_only_and_bound_to_the_exact_candidate_and_sources(
        bool compact, string support, UnderstandingEvidenceSupport expected)
    {
        var root = new ConversationSourceSnapshot(new("scene", "mash", "synthetic"), "reply", new('A', 64),
            DateTimeOffset.UtcNow, ConversationSourceKind.Assistant, "Synthetic", 1);
        const string text = "我在低代价且可撤回的比较中先小试，以反馈区分方案。";
        var original = new ReviewOriginal(root, text, new(root, 0, text.Length, text.Length));
        var p = new UnderstandingProposal("candidate", UnderstandingDomain.Self, UnderstandingKind.CurrentStance,
            "assistant", text, "仅当前低代价比较。", [root], Origin: UnderstandingOrigin.Expressed,
            Status: UnderstandingStatus.Tentative, Expressions: [new(root, text)]);
        var input = new ReviewModelInput("validation", new("mash", "synthetic"), [original], [], _ => { });
        var provider = new Provider(support);
        var route = ModelRouteKey.From(new ModelConnectionSettings("deepseek", "https://example.invalid/v1", "test"));
        var policy = compact
            ? await UnderstandingSemanticClassifier.ClassifyCompactAsync(input, [p], provider, route, new RequestTokenMeter(), default)
            : await UnderstandingSemanticClassifier.ClassifyAsync(input, [p], provider, route, new RequestTokenMeter(), default);
        var validation = Assert.IsAssignableFrom<IUnderstandingEvidenceValidation>(policy);
        Assert.Equal(expected, validation.Assess(p, [root]));
        Assert.Null(validation.Assess(p with { Content = "另一条更宽泛的命题" }, [root]));
        Assert.Null(validation.Assess(p with { Conditions = "永久适用" }, [root]));
        Assert.Null(validation.Assess(p with { TargetRevision = 99 }, [root]));
        Assert.Null(validation.Assess(p, [root with { Fingerprint = new('B', 64) }]));
        Assert.Equal(text, p.Content);
        Assert.Equal(text, original.Text);
        Assert.Contains("Do not rewrite", provider.Request!.ModelMessages[0].Content, StringComparison.Ordinal);
        Assert.Contains("PARTIAL", provider.Request.ModelMessages[0].Content, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("supported", 2, false, false)]
    [InlineData("UNKNOWN", 2, false, false)]
    [InlineData(null, 2, false, false)]
    [InlineData(true, 2, false, false)]
    [InlineData("SUPPORTED", 1, false, false)]
    [InlineData("SUPPORTED", 2, true, false)]
    [InlineData("SUPPORTED", 2, false, true)]
    public async Task Malformed_support_results_and_rewrite_payloads_fail_closed(object? support,int schema,bool missing,bool rewrite)
    {
        var source = new ConversationSourceSnapshot(new("scene", "mash", "synthetic"), "reply", new('A',64),
            DateTimeOffset.UtcNow, ConversationSourceKind.Assistant, "Synthetic", 1);
        var original = new ReviewOriginal(source,"text",new(source,0,4,4));
        var input = new ReviewModelInput("validate",new("mash","synthetic"),[original],[],_=>{});
        var p = new UnderstandingProposal("candidate",UnderstandingDomain.Self,UnderstandingKind.CurrentStance,"assistant","text","today",[source]);
        var route = ModelRouteKey.From(new ModelConnectionSettings("deepseek","https://example.invalid/v1","test"));
        await Assert.ThrowsAsync<FgoPet.Kernel.Agent.AgentProtocolException>(()=>UnderstandingSemanticClassifier.ClassifyAsync(input,[p],
            new Provider(support,schema,missing,rewrite),route,new RequestTokenMeter(),default));
    }

    internal sealed class Provider(object? support,int schema=2,bool missing=false,bool rewrite=false) : IChatProvider
    {
        public ChatRequest? Request;
        public string ProviderId => "deepseek";
        public string ModelId => "test";
        public Task<IReadOnlyList<ProviderModel>> ListModelsAsync(CancellationToken token) => throw new NotSupportedException();
        public async IAsyncEnumerable<ChatStreamChunk> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token)
        {
            Request = request;
            using var doc = JsonDocument.Parse(request.ModelMessages[^1].Content);
            var verdict = new Dictionary<string,object?> { ["proposal"]="p1", ["allow"]=true, ["reason"]="CURRENT_SELF" };
            if(!missing)verdict["support"]=support;
            if(rewrite)verdict["content"]="A replacement candidate must be rejected";
            await Task.Yield();
            yield return new(JsonSerializer.Serialize(new { schema, request = doc.RootElement.GetProperty("request").GetString(),
                verdicts = new[] { verdict } }),
                IsComplete: true, FinishReason: "stop", Usage: new(1000, 40));
        }
    }
}
