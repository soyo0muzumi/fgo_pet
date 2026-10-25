using System.Runtime.CompilerServices;
using System.Text;
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

public sealed class UnderstandingReviewModelTests
{
    private static readonly ConversationScope Scope = new("mash", "synthetic-project");
    private static readonly ModelRouteKey Route = ModelRouteKey.From(new ModelConnectionSettings(
        "deepseek", "https://api.deepseek.com/v1", "deepseek-flash", maxOutputTokens: 2048));
    private static ReviewModelInput Input(Action<CancellationToken>? ensure = null) => new("synthetic-review", Scope,
        [Original("one", "A synthetic assistant statement.")], [], ensure ?? (_ => { }));
    private static ReviewOriginal Original(string id, string text) => new(
        new(new("synthetic-conversation", Scope.ServantId, Scope.ProjectId), id, new('A', 64),
            new(2026, 10, 20, 0, 0, 0, TimeSpan.Zero), ConversationSourceKind.Assistant, "Synthetic", 1),
        text, new(new(new("synthetic-conversation", Scope.ServantId, Scope.ProjectId), id, new('A', 64),
            new(2026, 10, 20, 0, 0, 0, TimeSpan.Zero), ConversationSourceKind.Assistant, "Synthetic", 1),
            0, text.EnumerateRunes().Count(), text.EnumerateRunes().Count()));
    private static string Binding(ChatRequest request)
    {
        using var json = JsonDocument.Parse(request.ModelMessages.Last().Content);
        return json.RootElement.GetProperty("request").GetString()!;
    }
    private static string Response(ChatRequest request, string items = "[]") =>
        "{\"schema\":2,\"request\":\"" + Binding(request) + "\",\"proposals\":" + items + "}";
    private const string NewProposal = """
        [{"domain":"Self","kind":"CurrentInterest","subject":"assistant","content":"A tentative interest in this synthetic topic.","conditions":"Only the supplied project and dated material.","sources":["s1"],"target":null}]
        """;

    [Fact]
    public async Task Explicit_host_limits_allow_review_measurement_and_usage_above_legacy_caps()
    {
        var provider = new Provider(q => Response(q)) { Usage = new(20000, 4096) };
        var result = await new UnderstandingReviewModel(provider, Route, new OversizedMeter(),
            requestLimits: new(128000, 8192)).ReviewAsync(Input());
        Assert.Empty(result.Proposals);
        Assert.Equal(8192, Assert.Single(provider.Requests).MaxOutputTokens);
        Assert.Equal(20000, result.Usage.InputTokens);
    }

    [Fact]
    public async Task Adapter_uses_actual_host_request_with_no_tools_and_the_existing_limits()
    {
        var provider = new Provider(q => Response(q, NewProposal));
        var result = await new UnderstandingReviewModel(provider, Route, new RequestTokenMeter()).ReviewAsync(Input());
        var request = Assert.Single(provider.Requests);
        Assert.True(request.IsAgentRequest);
        Assert.Null(request.Tools);
        Assert.Null(request.ToolChoice);
        Assert.Equal(2048, request.MaxOutputTokens);
        Assert.Equal("understanding_review", request.Metadata["fgo_auxiliary"]);
        Assert.Equal([ModelMessageRole.System, ModelMessageRole.User], request.ModelMessages.Select(m => m.Role));
        var proposal = Assert.Single(result.Proposals);
        Assert.Equal(Input().Originals[0].Source, Assert.Single(proposal.Sources));
        Assert.Null(proposal.TargetId);
        Assert.Equal(Input().Originals[0].Coverage, Assert.Single(result.SuppliedCoverage));
        Assert.Equal(Binding(request), result.RequestHash);
    }

    [Fact]
    public async Task Empty_proposals_are_a_valid_no_change_result()
    {
        var provider = new Provider(q => Response(q));
        Assert.Empty((await new UnderstandingReviewModel(provider, Route, new RequestTokenMeter()).ReviewAsync(Input())).Proposals);
        Assert.Equal(1, provider.DisposeCount);
    }

    [Theory]
    [InlineData("schema1")]
    [InlineData("duplicate-key")]
    [InlineData("extra-field")]
    [InlineData("unknown-source")]
    [InlineData("wrong-binding")]
    [InlineData("unknown-target")]
    [InlineData("markdown")]
    public async Task Invalid_response_is_rejected_as_a_whole(string fault)
    {
        var provider = new Provider(q => fault switch
        {
            "schema1" => Response(q, NewProposal).Replace("\"schema\":2", "\"schema\":1"),
            "duplicate-key" => Response(q).Replace("\"schema\":2", "\"schema\":2,\"schema\":2"),
            "extra-field" => Response(q).Replace("\"proposals\":[]", "\"proposals\":[],\"approved\":true"),
            "unknown-source" => Response(q, NewProposal.Replace("s1", "s2")),
            "wrong-binding" => Response(q).Replace(Binding(q), new('0', 64)),
            "unknown-target" => Response(q, NewProposal.Replace("\"target\":null", "\"target\":\"t1\"")),
            _ => "```json\n" + Response(q) + "\n```",
        });
        await Assert.ThrowsAsync<AgentProtocolException>(() =>
            new UnderstandingReviewModel(provider, Route, new RequestTokenMeter()).ReviewAsync(Input()));
        Assert.Equal(1, provider.DisposeCount);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(16001, 1)]
    [InlineData(1, 2049)]
    public async Task Missing_or_out_of_bounds_usage_never_produces_candidates(int? input, int? output)
    {
        var provider = new Provider(q => Response(q)) { Usage = input is null ? null : new(input.Value, output!.Value) };
        await Assert.ThrowsAsync<AgentProtocolException>(() =>
            new UnderstandingReviewModel(provider, Route, new RequestTokenMeter()).ReviewAsync(Input()));
        Assert.Equal(1, provider.DisposeCount);
    }

    [Fact]
    public async Task Oversized_request_is_refused_before_provider_entry()
    {
        var provider = new Provider(q => Response(q));
        await Assert.ThrowsAsync<AgentProtocolException>(() =>
            new UnderstandingReviewModel(provider, Route, new OversizedMeter()).ReviewAsync(Input()));
        Assert.Empty(provider.Requests);
    }

    [Fact]
    public async Task A_revoked_host_fence_prevents_the_first_provider_move()
    {
        var provider = new Provider(q => Response(q));
        await Assert.ThrowsAsync<OperationCanceledException>(() => new UnderstandingReviewModel(provider, Route,
            new RequestTokenMeter()).ReviewAsync(Input(_ => throw new OperationCanceledException())));
        Assert.Empty(provider.Requests);
    }

    [Fact]
    public async Task Partial_material_keeps_its_actual_coverage_and_never_becomes_a_complete_read()
    {
        var original = Original("partial", "abc");
        original = original with { Coverage = original.Coverage with { StartScalar = 5, TotalScalars = 20 } };
        var provider = new Provider(q => Response(q));
        var result = await new UnderstandingReviewModel(provider, Route, new RequestTokenMeter())
            .ReviewAsync(Input() with { Originals = [original] });
        Assert.Equal(original.Coverage, Assert.Single(result.SuppliedCoverage));
        Assert.False(result.SuppliedCoverage[0].Complete);
    }

    [Fact]
    public async Task Cross_scope_material_is_refused_before_provider_entry()
    {
        var provider = new Provider(q => Response(q));
        await Assert.ThrowsAsync<AgentProtocolException>(() => new UnderstandingReviewModel(provider, Route,
            new RequestTokenMeter()).ReviewAsync(Input() with { Scope = new("mash", "other-project") }));
        Assert.Empty(provider.Requests);
    }

    [Fact]
    public async Task Usage_from_an_intermediate_chunk_cannot_replace_terminal_usage()
    {
        var provider = new Provider(q => Response(q)) { PreliminaryUsage = new(600, 120), Usage = null };
        var error = await Assert.ThrowsAsync<AgentProtocolException>(() =>
            new UnderstandingReviewModel(provider, Route, new RequestTokenMeter()).ReviewAsync(Input()));
        Assert.Equal("UNDERSTANDING_REVIEW_USAGE_MISSING", error.Code);
        Assert.Equal(1, provider.DisposeCount);
    }

    [Theory]
    [InlineData("finish-event")]
    [InlineData("after-finish")]
    public async Task Completed_http_stream_accepts_usage_at_the_finish_event_or_its_usage_trailer(string layout)
    {
        var provider = new Provider(q => Response(q)) { UsageLayout = layout };
        var result = await new UnderstandingReviewModel(provider, Route, new RequestTokenMeter()).ReviewAsync(Input());
        Assert.Equal(600, result.Usage.InputTokens);
        Assert.Equal(120, result.Usage.OutputTokens);
        Assert.Equal(1, provider.DisposeCount);
    }

    [Fact]
    public async Task Revocation_in_the_last_diagnostic_callback_prevents_a_success_result()
    {
        var enabled = true;
        var provider = new Provider(q => Response(q));
        var model = new UnderstandingReviewModel(provider, Route, new RequestTokenMeter(),
            d => { if (d.Stage == "review_model_completed") enabled = false; });
        await Assert.ThrowsAsync<OperationCanceledException>(() => model.ReviewAsync(Input(_ =>
        {
            if (!enabled) throw new OperationCanceledException();
        })));
        Assert.Equal(1, provider.DisposeCount);
    }

    private static UnderstandingEntry Target(string id, int revision) => new(id, Scope, revision,
        UnderstandingDomain.Self, UnderstandingKind.CurrentInterest, "assistant", "Synthetic prior interest.",
        "Only this synthetic project.", "assistant_statement", [Input().Originals[0].Source],
        UnderstandingReview.Approved, UnderstandingEffect.Approved, UnderstandingLife.Active,
        new(2026, 10, 20, 0, 0, 0, TimeSpan.Zero), new(2026, 10, 27, 0, 0, 0, TimeSpan.Zero), 1);

    [Fact]
    public async Task Target_aliases_bind_independently_to_exact_host_revisions()
    {
        var proposal = NewProposal[1..^1];
        var items = "[" + proposal.Replace("\"target\":null", "\"target\":\"t2\"") + "," +
            proposal.Replace("\"target\":null", "\"target\":\"t1\"") + "]";
        var provider = new Provider(q => Response(q, items));
        var result = await new UnderstandingReviewModel(provider, Route, new RequestTokenMeter())
            .ReviewAsync(Input() with { Targets = [Target("first", 3), Target("second", 7)] });
        Assert.Equal(("second", 7), (result.Proposals[0].TargetId, result.Proposals[0].TargetRevision));
        Assert.Equal(("first", 3), (result.Proposals[1].TargetId, result.Proposals[1].TargetRevision));
    }

    [Fact]
    public async Task Duplicate_target_revisions_reject_the_whole_response()
    {
        var proposal = NewProposal[1..^1].Replace("\"target\":null", "\"target\":\"t1\"");
        var provider = new Provider(q => Response(q, "[" + proposal + "," + proposal + "]"));
        await Assert.ThrowsAsync<AgentProtocolException>(() => new UnderstandingReviewModel(provider, Route,
            new RequestTokenMeter()).ReviewAsync(Input() with { Targets = [Target("first", 3)] }));
    }

    [Theory]
    [InlineData("length", false)]
    [InlineData("stop", true)]
    public async Task Truncated_or_extra_stream_data_never_produces_candidates(string finish, bool extra)
    {
        var provider = new Provider(q => Response(q, NewProposal)) { FinishReason = finish, ExtraChunk = extra };
        await Assert.ThrowsAsync<AgentProtocolException>(() =>
            new UnderstandingReviewModel(provider, Route, new RequestTokenMeter()).ReviewAsync(Input()));
        Assert.Equal(1, provider.DisposeCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Incorrect_read_coverage_never_enters_provider(bool wrongSource)
    {
        var original = Input().Originals[0];
        var coverage = original.Coverage with { IncludedScalars = original.Coverage.IncludedScalars - 1 };
        if (wrongSource) coverage = original.Coverage with { Source = original.Source with { SourceRevision = 2 } };
        var provider = new Provider(q => Response(q));
        await Assert.ThrowsAsync<AgentProtocolException>(() => new UnderstandingReviewModel(provider, Route,
            new RequestTokenMeter()).ReviewAsync(Input() with { Originals = [original with { Coverage = coverage }] }));
        Assert.Empty(provider.Requests);
    }

    private sealed class OversizedMeter : IRequestTokenMeter
    {
        public TokenMeasurement Measure(ModelRouteKey route, ChatRequest request) => new(16001, TokenCountKind.Estimated, "synthetic");
        public void RecordUsage(ModelRouteKey route, ChatRequest request, ChatUsage usage) { }
        public void Invalidate(ModelRouteKey route) { }
    }
    private sealed class Provider(Func<ChatRequest, string> response) : IChatProvider
    {
        public string ProviderId => "deepseek";
        public string ModelId => "deepseek-flash";
        public List<ChatRequest> Requests { get; } = [];
        public int DisposeCount { get; private set; }
        public ChatUsage? Usage { get; init; } = new(600, 120);
        public ChatUsage? PreliminaryUsage { get; init; }
        public string FinishReason { get; init; } = "stop";
        public bool ExtraChunk { get; init; }
        public string UsageLayout { get; init; } = "terminal";
        public Task<IReadOnlyList<ProviderModel>> ListModelsAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<ProviderModel>>([]);
        public async IAsyncEnumerable<ChatStreamChunk> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token)
        {
            Requests.Add(request);
            try
            {
                await Task.Yield(); token.ThrowIfCancellationRequested();
                if (PreliminaryUsage is not null) yield return new("", Usage: PreliminaryUsage);
                if (UsageLayout == "terminal")
                    yield return new(response(request), IsComplete: true, FinishReason: FinishReason, Usage: Usage);
                else
                {
                    yield return new(response(request), FinishReason: FinishReason, Usage: UsageLayout == "finish-event" ? Usage : null);
                    if (UsageLayout == "after-finish") yield return new("", Usage: Usage);
                    yield return new("", IsComplete: true, FinishReason: FinishReason);
                }
                if (ExtraChunk) yield return new("unexpected tail");
            }
            finally { DisposeCount++; }
        }
    }
}
