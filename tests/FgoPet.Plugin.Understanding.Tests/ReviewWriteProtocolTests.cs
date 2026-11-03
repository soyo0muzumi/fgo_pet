using System.Runtime.CompilerServices;
using System.Text.Json;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Settings;
using FgoPet.Extensibility;
using FgoPet.Infrastructure.Providers;
using FgoPet.Kernel.Agent;
using FgoPet.Understanding;
using FgoPet.Understanding.Contracts;
using Xunit;

namespace FgoPet.Plugin.Understanding.Tests;

public sealed class ReviewWriteProtocolTests
{
    private static readonly ConversationScope Scope = new("mash", "synthetic");
    private static readonly ConversationSourceSnapshot Source = new(new("one", "mash", "synthetic"), "reply",
        new('A', 64), new(2026, 10, 26, 0, 0, 0, TimeSpan.Zero), ConversationSourceKind.Assistant, "Synthetic", 1);
    private static readonly ReviewOriginal Original = new(Source, "A specific current stance.", new(Source, 0, 26, 26));
    private static readonly UnderstandingEntry Target = new("prior", Scope, 7, UnderstandingDomain.Self,
        UnderstandingKind.CurrentStance, "assistant", "A specific current stance.", "Current project only.", "expressed",
        [Source], UnderstandingReview.Unreviewed, UnderstandingEffect.Pending, UnderstandingLife.Active,
        Source.OccurredAtUtc, Source.OccurredAtUtc.AddDays(7), 1, Origin: UnderstandingOrigin.Expressed,
        Status: UnderstandingStatus.Tentative);

    private static string Json(UnderstandingWriteOperation operation, string? target = "t1", string? status = null) =>
        JsonSerializer.Serialize(new { schema = 3, request = "binding", proposals = new[] { new {
            domain = "Self", kind = "CurrentStance", subject = "assistant", content = Target.Content, conditions = Target.Conditions,
            sources = new[] { "s1" }, target, operation = operation.ToString(), origin = "Expressed",
            status = status ?? (operation == UnderstandingWriteOperation.Retract ? "Retracted" : "Tentative"),
            expressions = new[] { new { source = "s1", quote = Original.Text } }, conflicts = new[] { "s1" },
            maintenance_intents = new[] { "PossibleSpecialization" } } } });

    [Theory]
    [InlineData(UnderstandingWriteOperation.AttachEvidence)]
    [InlineData(UnderstandingWriteOperation.Revise)]
    [InlineData(UnderstandingWriteOperation.Retract)]
    public void Maintenance_operations_bind_exact_host_target_and_source_references(UnderstandingWriteOperation operation)
    {
        var result = Assert.Single(UnderstandingReviewResponse.Parse(Json(operation), "binding", [Original], [Target]));
        Assert.Equal(operation, result.Operation);
        Assert.Equal(Target.Id, result.TargetId);
        Assert.Equal(Target.Revision, result.TargetRevision);
        Assert.Equal(Source, Assert.Single(result.Sources));
        Assert.Equal(Source, Assert.Single(result.Expressions!).Source);
        Assert.Equal(Source, Assert.Single(result.Conflicts!));
        Assert.Equal(UnderstandingMaintenanceIntent.PossibleSpecialization, Assert.Single(result.MaintenanceIntents!));
    }

    [Fact]
    public void Host_bound_wire_references_copy_exact_original_without_model_quotation()
    {
        var json = Json(UnderstandingWriteOperation.Create, null).Replace("\"schema\":3", "\"schema\":4")
            .Replace(JsonSerializer.Serialize(new[] { new { source = "s1", quote = Original.Text } }), "[\"s1\"]");
        var proposal = Assert.Single(UnderstandingReviewResponse.Parse(json, "binding", [Original], [], hostBoundExpressions: true));
        Assert.Equal(Original.Text, Assert.Single(proposal.Expressions!).Quote);
        Assert.Throws<AgentProtocolException>(() => UnderstandingReviewResponse.Parse(json.Replace("\"expressions\":[\"s1\"]", "\"expressions\":[\"invented\"]"), "binding", [Original], [], hostBoundExpressions: true));
        var user = Original with { Source = Source with { Kind = ConversationSourceKind.User } };
        Assert.Throws<AgentProtocolException>(() => UnderstandingReviewResponse.Parse(json, "binding", [user], [], hostBoundExpressions: true));
    }

    [Theory]
    [InlineData(UnderstandingWriteOperation.Create, "t1", "Tentative")]
    [InlineData(UnderstandingWriteOperation.AttachEvidence, null, "Tentative")]
    [InlineData(UnderstandingWriteOperation.Revise, null, "Tentative")]
    [InlineData(UnderstandingWriteOperation.Retract, "t1", "Tentative")]
    [InlineData(UnderstandingWriteOperation.Create, null, "Retracted")]
    [InlineData(UnderstandingWriteOperation.Create, null, "Established")]
    public void Operation_target_and_status_mismatches_are_protocol_errors(UnderstandingWriteOperation operation, string? target, string status)
        => Assert.Throws<AgentProtocolException>(() => UnderstandingReviewResponse.Parse(Json(operation, target, status), "binding", [Original], [Target]));

    [Theory]
    [InlineData(UnderstandingDomain.Self, UnderstandingKind.CurrentInterest, UnderstandingOrigin.Expressed, true)]
    [InlineData(UnderstandingDomain.Self, UnderstandingKind.Uncertainty, UnderstandingOrigin.SecondOrderInferred, true)]
    [InlineData(UnderstandingDomain.Relationship, UnderstandingKind.InteractionPattern, UnderstandingOrigin.Enacted, true)]
    [InlineData(UnderstandingDomain.Relationship, UnderstandingKind.Interpretation, UnderstandingOrigin.Enacted, false)]
    [InlineData(UnderstandingDomain.Self, UnderstandingKind.StableIdentity, UnderstandingOrigin.Expressed, false)]
    [InlineData(UnderstandingDomain.World, UnderstandingKind.ExternalObservation, UnderstandingOrigin.Expressed, false)]
    [InlineData(UnderstandingDomain.User, UnderstandingKind.UserFact, UnderstandingOrigin.Expressed, false)]
    [InlineData(UnderstandingDomain.Self, UnderstandingKind.CurrentInterest, UnderstandingOrigin.ExternallySuggested, false)]
    public void Contract_classifier_candidates_preserve_owner_and_authorship_boundaries(UnderstandingDomain domain,
        UnderstandingKind kind, UnderstandingOrigin origin, bool expected)
    {
        var proposal = new UnderstandingProposal("candidate", domain, kind, "assistant", "A bounded view.", "This project.", [Source],
            Origin: origin, Status: origin == UnderstandingOrigin.Expressed ? UnderstandingStatus.Tentative : UnderstandingStatus.Hypothesis);
        Assert.Equal(expected, UnderstandingSemanticClassifier.Candidate(proposal));
        Assert.False(UnderstandingSemanticClassifier.Candidate(proposal with { Status = UnderstandingStatus.Established }));
    }

    [Theory]
    [InlineData(UnderstandingDomain.Self, UnderstandingKind.CurrentStance, "CURRENT_SELF")]
    [InlineData(UnderstandingDomain.Relationship, UnderstandingKind.InteractionPattern, "CURRENT_RELATIONSHIP")]
    public async Task Independent_semantic_classification_binds_contract_metadata_and_cannot_be_reused_after_edit(
        UnderstandingDomain domain, UnderstandingKind kind, string reason)
    {
        var proposal = new UnderstandingProposal("p", domain, kind, "assistant", "A bounded view.", "This project.", [Source],
            Origin: UnderstandingOrigin.Expressed, Status: UnderstandingStatus.Tentative,
            Expressions: [new(Source, Original.Text)]);
        var provider = new ClassifierProvider(reason);
        var input = new ReviewModelInput("operation", Scope, [Original], [], _ => { });
        var policy = await UnderstandingSemanticClassifier.ClassifyAsync(input, [proposal], provider,
            ModelRouteKey.From(new ModelConnectionSettings("deepseek", "https://api.deepseek.com/v1", "deepseek-flash")),
            new RequestTokenMeter(), default);
        Assert.True(policy.IsLowImpact(proposal, [Source]));
        Assert.False(policy.IsLowImpact(proposal with { Origin = UnderstandingOrigin.SecondOrderInferred }, [Source]));
        Assert.False(policy.IsLowImpact(proposal with { Content = "A broader permanent view." }, [Source]));
    }

    private sealed class ClassifierProvider(string reason) : IChatProvider
    {
        public string ProviderId => "deepseek";
        public string ModelId => "deepseek-flash";
        public Task<IReadOnlyList<ProviderModel>> ListModelsAsync(CancellationToken token) => throw new NotSupportedException();
        public async IAsyncEnumerable<ChatStreamChunk> StreamAsync(ChatRequest request,
            [EnumeratorCancellation] CancellationToken token)
        {
            using var body = JsonDocument.Parse(request.ModelMessages[^1].Content);
            var candidate = body.RootElement.GetProperty("proposals")[0];
            Assert.Equal("Expressed", candidate.GetProperty("Origin").GetString());
            Assert.Equal("Tentative", candidate.GetProperty("Status").GetString());
            Assert.Equal("Create", candidate.GetProperty("Operation").GetString());
            Assert.Single(candidate.GetProperty("Expressions").EnumerateArray());
            var binding = body.RootElement.GetProperty("request").GetString();
            await Task.Yield();
            yield return new(JsonSerializer.Serialize(new { schema = 2, request = binding,
                verdicts = new[] { new { proposal = "p1", allow = true, reason, support = "SUPPORTED" } } }),
                IsComplete: true, FinishReason: "stop", Usage: new(1000, 30));
        }
    }
}
