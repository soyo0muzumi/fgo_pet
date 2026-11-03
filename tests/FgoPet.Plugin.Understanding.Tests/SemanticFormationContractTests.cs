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

public sealed class SemanticFormationContractTests
{
    private static readonly ModelRouteKey Route = ModelRouteKey.From(new ModelConnectionSettings("deepseek", "https://example.invalid/v1", "test"));
    private static readonly ConversationScope Scope = new("mash", "project");
    private static ReviewModelInput Input()
    {
        var source = new ConversationSourceSnapshot(new("conversation", "mash", "project"), "message", new('A', 64),
            new(2026, 10, 20, 0, 0, 0, TimeSpan.Zero), ConversationSourceKind.Assistant, "Synthetic", 1);
        return new("operation", Scope, [new(source, "text", new(source, 0, 4, 4))], [], _ => { });
    }

    [Fact]
    public async Task Both_adapters_expose_opaque_conversation_grouping_for_independence_assessment()
    {
        var first = Input().Originals[0];
        var secondSource = first.Source with { MessageId = "second" };
        var thirdSource = first.Source with { MessageId = "third", Scope = new("other-conversation", "mash", "project") };
        var input = Input() with { Originals = [first, new(secondSource, "text", new(secondSource, 0, 4, 4)), new(thirdSource, "text", new(thirdSource, 0, 4, 4))] };
        var review = new CaptureProvider(false);
        await new UnderstandingReviewModel(review, Route, new RequestTokenMeter()).ReviewAsync(input);
        var candidate = new UnderstandingProposal("p", UnderstandingDomain.Self, UnderstandingKind.CurrentStance, "assistant", "bounded", "today", [first.Source]);
        var classifier = new CaptureProvider(true);
        await UnderstandingSemanticClassifier.ClassifyAsync(input, [candidate], classifier, Route, new RequestTokenMeter(), default);
        string[]? expected = null;
        foreach (var request in new[] { review.Request!, classifier.Request! })
        {
            using var data = JsonDocument.Parse(request.ModelMessages[^1].Content);
            var groups = data.RootElement.GetProperty("sources").EnumerateArray().Select(s => s.GetProperty("conversationGroup").GetString()!).ToArray();
            Assert.Equal(groups[0], groups[1]);
            Assert.NotEqual(groups[0], groups[2]);
            Assert.All(groups, group => Assert.Matches("^[A-F0-9]{64}$", group));
            Assert.All(groups, group => Assert.DoesNotContain("conversation", group, StringComparison.Ordinal));
            if (expected is not null) Assert.Equal(expected, groups);
            expected = groups;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Both_review_protocols_and_classifier_receive_the_same_formation_constraints(bool compact)
    {
        var input = Input();
        var review = new CaptureProvider(false);
        await new UnderstandingReviewModel(review, Route, new RequestTokenMeter(), hostBoundExpressions: compact).ReviewAsync(input);
        var candidate = new UnderstandingProposal("p", UnderstandingDomain.Self, UnderstandingKind.CurrentStance, "assistant", "bounded", "today", [input.Originals[0].Source]);
        var classifier = new CaptureProvider(true);
        if (compact)
            await UnderstandingSemanticClassifier.ClassifyCompactAsync(input, [candidate], classifier, Route, new RequestTokenMeter(), default);
        else
            await UnderstandingSemanticClassifier.ClassifyAsync(input, [candidate], classifier, Route, new RequestTokenMeter(), default);
        foreach (var request in new[] { review.Request!, classifier.Request! })
        {
            var system = request.ModelMessages[0].Content;
            Assert.Contains("same narrow conditional meaning", system, StringComparison.Ordinal);
            Assert.Contains("at least two independent subsequent behavioral observations", system, StringComparison.Ordinal);
            Assert.Contains("cue-independent", system, StringComparison.Ordinal);
            Assert.Contains("persona-seeded", system, StringComparison.Ordinal);
            Assert.Contains("relative choices", system, StringComparison.Ordinal);
            Assert.Contains("IF conditions THEN tendency BECAUSE reason", system, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Both_review_protocols_carry_the_v023_boundary_calibration_guidance(bool compact)
    {
        var input = Input();
        var review = new CaptureProvider(false);
        await new UnderstandingReviewModel(review, Route, new RequestTokenMeter(), hostBoundExpressions: compact).ReviewAsync(input);
        var candidate = new UnderstandingProposal("p", UnderstandingDomain.Self, UnderstandingKind.CurrentStance, "assistant", "bounded", "today", [input.Originals[0].Source]);
        var classifier = new CaptureProvider(true);
        if (compact)
            await UnderstandingSemanticClassifier.ClassifyCompactAsync(input, [candidate], classifier, Route, new RequestTokenMeter(), default);
        else
            await UnderstandingSemanticClassifier.ClassifyAsync(input, [candidate], classifier, Route, new RequestTokenMeter(), default);
        foreach (var request in new[] { review.Request!, classifier.Request! })
        {
            var system = request.ModelMessages[0].Content;
            // Anti-abstraction: different reasons must not be lifted into one covering principle.
            Assert.Contains("do not lift different reasons into one higher covering principle", system, StringComparison.Ordinal);
            Assert.Contains("never merge by OR", system, StringComparison.Ordinal);
            // Boundary fidelity: a scene-only condition must be neither carried over nor silently dropped.
            Assert.Contains("scene-only condition", system, StringComparison.Ordinal);
            Assert.Contains("not by itself evidence of a general urgency rule", system, StringComparison.Ordinal);
            // Do not overcorrect into global NoChange.
            Assert.Contains("unnecessary NoChange is a failure too", system, StringComparison.Ordinal);
            // Attach must carry the target's whole substantive meaning.
            Assert.Contains("whole substantive meaning of the target", system, StringComparison.Ordinal);
            // Grounding: no observation may be invented beyond the raw sources.
            Assert.Contains("Do not attribute to the assistant any observation not present in the raw sources", system, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Both_review_protocols_require_the_attach_evidence_support_test(bool compact)
    {
        var input = Input();
        var review = new CaptureProvider(false);
        await new UnderstandingReviewModel(review, Route, new RequestTokenMeter(), hostBoundExpressions: compact).ReviewAsync(input);
        var candidate = new UnderstandingProposal("p", UnderstandingDomain.Self, UnderstandingKind.CurrentStance, "assistant", "bounded", "today", [input.Originals[0].Source]);
        var classifier = new CaptureProvider(true);
        if (compact)
            await UnderstandingSemanticClassifier.ClassifyCompactAsync(input, [candidate], classifier, Route, new RequestTokenMeter(), default);
        else
            await UnderstandingSemanticClassifier.ClassifyAsync(input, [candidate], classifier, Route, new RequestTokenMeter(), default);
        foreach (var request in new[] { review.Request!, classifier.Request! })
        {
            var system = request.ModelMessages[0].Content;
            // Line wrapping inside the raw string literal is presentation, not meaning, so
            // compare on whitespace-normalized text. Wording itself is asserted verbatim.
            var flat = string.Join(' ', system.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            // v0.2.4: AttachEvidence is gated by a support test, not by a domain comparison.
            Assert.Contains("Before AttachEvidence", flat, StringComparison.Ordinal);
            Assert.Contains("must directly support at least one existing reason, condition, or behavioral claim", flat, StringComparison.Ordinal);
            Assert.Contains("If it only introduces a different rationale, context, or behavioral pattern", flat, StringComparison.Ordinal);
            Assert.Contains("do not attach it merely because the outcomes or surface structure are similar", flat, StringComparison.Ordinal);
            Assert.Contains("Use Create / Revise / NoChange instead", flat, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Both_review_protocols_share_evidence_compatibility_for_the_resulting_meaning(bool compact)
    {
        var input = Input();
        var review = new CaptureProvider(false);
        await new UnderstandingReviewModel(review, Route, new RequestTokenMeter(), hostBoundExpressions: compact).ReviewAsync(input);
        var candidate = new UnderstandingProposal("p", UnderstandingDomain.Self, UnderstandingKind.CurrentStance, "assistant", "bounded", "today", [input.Originals[0].Source]);
        var classifier = new CaptureProvider(true);
        if (compact)
            await UnderstandingSemanticClassifier.ClassifyCompactAsync(input, [candidate], classifier, Route, new RequestTokenMeter(), default);
        else
            await UnderstandingSemanticClassifier.ClassifyAsync(input, [candidate], classifier, Route, new RequestTokenMeter(), default);
        foreach (var request in new[] { review.Request!, classifier.Request! })
        {
            var flat = string.Join(' ', request.ModelMessages[0].Content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            Assert.Contains("Evidence compatibility invariant for Create, AttachEvidence and Revise", flat, StringComparison.Ordinal);
            Assert.Contains("the resulting entry's specific reason, required conditions and behavioral claim", flat, StringComparison.Ordinal);
            Assert.Contains("the sources together must support its whole substantive meaning", flat, StringComparison.Ordinal);
            Assert.Contains("For Revise, recheck compatibility against the revised meaning", flat, StringComparison.Ordinal);
            Assert.Contains("Retained historical sources are provenance, not automatic evidence for the revised claim", flat, StringComparison.Ordinal);
            Assert.Contains("never keep an unsupported cross-domain union or replace it with a vacuous covering principle", flat, StringComparison.Ordinal);
        }
    }

    private sealed class CaptureProvider(bool classifier) : IChatProvider
    {
        public string ProviderId => "deepseek";
        public string ModelId => "test";
        public ChatRequest? Request { get; private set; }
        public Task<IReadOnlyList<ProviderModel>> ListModelsAsync(CancellationToken token) => throw new NotSupportedException();
        public async IAsyncEnumerable<ChatStreamChunk> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token)
        {
            Request = request;
            using var data = JsonDocument.Parse(request.ModelMessages[^1].Content);
            var binding = data.RootElement.GetProperty("request").GetString();
            var schema = data.RootElement.GetProperty("schema").GetInt32();
            var response = classifier
                ? JsonSerializer.Serialize(new { schema = 2, request = binding, verdicts = new[] { new { proposal = "p1", allow = false, reason = "REVIEW_REQUIRED", support = "SUPPORTED" } } })
                : JsonSerializer.Serialize(new { schema, request = binding, proposals = Array.Empty<object>() });
            await Task.Yield();
            yield return new(response, IsComplete: true, FinishReason: "stop", Usage: new(500, 20));
        }
    }
}
