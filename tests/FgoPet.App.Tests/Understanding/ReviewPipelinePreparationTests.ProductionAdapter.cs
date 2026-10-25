using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using FgoPet.App.Providers;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Settings;
using FgoPet.Infrastructure.Providers;
using FgoPet.Understanding;
using FgoPet.Understanding.Contracts;
using Xunit;

namespace FgoPet.App.Tests.Understanding;

public sealed partial class ReviewPipelinePreparationTests
{
    [Theory]
    [InlineData("proposal", UnderstandingOutcome.ProposedPending)]
    [InlineData("no-change", UnderstandingOutcome.NoChange)]
    [InlineData("no-change-trailing-usage", UnderstandingOutcome.NoChange)]
    [InlineData("wrong-schema", UnderstandingOutcome.Failed)]
    public async Task Actual_review_adapter_round_trips_through_the_production_http_parser_and_manual_owner(
        string response, UnderstandingOutcome expected)
    {
        using var fixture = new Fixture(); fixture.Allow();
        using var handler = new ReviewHttpFixture(response);
        using var http = new HttpClient(handler);
        var credentials = new ReviewFixtureCredentials();
        var connection = new ModelConnectionSettings("deepseek", "https://api.deepseek.com/v1", "deepseek-flash", maxOutputTokens: 2048);
        var provider = new ChatProviderFactory(new ProviderCatalog(), credentials, http).Create(connection);
        var adapter = new UnderstandingReviewModel(provider, ModelRouteKey.From(connection), new RequestTokenMeter(), fixture.Diagnostics.Add);
        fixture.ProduceAsync = async (request, token) => (await adapter.ReviewAsync(new("PRIVATE_OPERATION", fixture.Scope,
            request.Material.Select((m, i) => new ReviewOriginal(m.Source, m.Text, request.ReadProof[i].Coverage)).ToArray(),
            request.Targets, request.EnsureCurrent), token)).Proposals;
        var result = await fixture.Run();
        Assert.Equal(expected, Assert.Single(result.Items).Outcome);
        Assert.Equal(1, handler.Requests);
        Assert.Equal(1, credentials.Reads);
        Assert.Equal(2, fixture.Originals.Reads);
        Assert.Empty(fixture.Owner.Query(fixture.Scope, "").Items);
        if (expected == UnderstandingOutcome.ProposedPending)
        {
            var entry = Assert.Single(fixture.Owner.List(fixture.Scope, true));
            Assert.Equal(UnderstandingReview.Unreviewed, entry.Review);
            Assert.Equal(UnderstandingEffect.Pending, entry.Effect);
            Assert.Null(entry.Coverage); // Existing manual preview is not an automatic coverage-aware Review service.
        }
        else Assert.Empty(fixture.Owner.List(fixture.Scope, true));
        Assert.DoesNotContain("synthetic provider candidate", JsonSerializer.Serialize(fixture.Diagnostics));
    }

    private sealed class ReviewFixtureCredentials : FgoPet.Infrastructure.Secrets.ICredentialReader
    {
        public int Reads { get; private set; }
        public Task<string?> ReadAsync(string target, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Assert.Equal("fgo-pet/provider/deepseek", target); Reads++;
            return Task.FromResult<string?>("synthetic-test-only");
        }
    }
    private sealed class ReviewHttpFixture(string response) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests++;
            Assert.Equal("https://api.deepseek.com/v1/chat/completions", request.RequestUri!.AbsoluteUri);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var root = body.RootElement;
            Assert.Equal(2048, root.GetProperty("max_tokens").GetInt32());
            Assert.Equal("disabled", root.GetProperty("thinking").GetProperty("type").GetString());
            Assert.Equal("json_object", root.GetProperty("response_format").GetProperty("type").GetString());
            Assert.False(root.TryGetProperty("tools", out _));
            using var material = JsonDocument.Parse(root.GetProperty("messages")[1].GetProperty("content").GetString()!);
            var binding = material.RootElement.GetProperty("request").GetString()!;
            var proposals = response.StartsWith("no-change", StringComparison.Ordinal) ? Array.Empty<object>() : [new {
                domain = "Self", kind = "CurrentInterest", subject = "assistant", content = "synthetic provider candidate",
                conditions = "Only this dated synthetic project; tentative.", sources = new[] { "s2" }, target = (string?)null }];
            var json = JsonSerializer.Serialize(new { schema = response == "wrong-schema" ? 1 : 2, request = binding, proposals });
            var choices = new[] { new { delta = new { content = json }, finish_reason = "stop" } };
            var usage = new { prompt_tokens = 700, completion_tokens = 100, total_tokens = 800 };
            var sse = response == "no-change-trailing-usage"
                ? "data: " + JsonSerializer.Serialize(new { choices }) + "\n\ndata: " +
                    JsonSerializer.Serialize(new { choices = Array.Empty<object>(), usage }) + "\n\ndata: [DONE]\n\n"
                : "data: " + JsonSerializer.Serialize(new { choices, usage }) + "\n\ndata: [DONE]\n\n";
            return new(HttpStatusCode.OK) { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") };
        }
    }
}
