using System.IO;
using System.Security.Cryptography;
using System.Text;
using FgoPet.Core.Dialogue;
using FgoPet.Extensibility;
using FgoPet.Kernel.Agent;
using FgoPet.Infrastructure.Providers;
using Xunit;

namespace FgoPet.Infrastructure.Tests.Providers;

public sealed class LocalReferenceRequestTokenMeterTests
{
    private const string Policy = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";
    private static readonly ModelRouteKey Route = ModelRouteKey.From(new("deepseek", "https://api.deepseek.com/v1", "deepseek-flash", maxOutputTokens: 2048));
    private static ChatRequest Request(string text = "合成输入") => new("fixture", "c", [new(ChatMessageRole.User, text)], maxOutputTokens: 2048);
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    [Fact]
    public void Verified_full_envelope_is_labelled_as_local_and_cached_without_becoming_provider_usage()
    {
        var counter = new Counter(); var meter = new LocalReferenceRequestTokenMeter(Route, Policy, counter);
        var request = Request(new string('中', 5000));
        var measured = meter.Measure(Route, request);
        Assert.Equal(1234, measured.InputTokens);
        Assert.Equal(TokenCountKind.LocalReferenceEstimate, measured.Kind);
        Assert.Equal(Policy, measured.ReferencePolicyHash);
        Assert.Equal(new RequestTokenMeter().Measure(Route, request).Fingerprint, measured.Fingerprint);
        Assert.Equal(measured, meter.Measure(Route, request)); Assert.Equal(1, counter.Calls);
        meter.RecordUsage(Route, request, new(2345, 7));
        Assert.Equal(TokenCountKind.ProviderUsage, meter.Measure(Route, request).Kind);
        Assert.Equal(2345, meter.Measure(Route, request).InputTokens);
        meter.Invalidate(Route);
        Assert.Equal(TokenCountKind.LocalReferenceEstimate, meter.Measure(Route, request).Kind);
        Assert.Equal(2, counter.Calls);
    }

    [Theory]
    [InlineData("endpoint")]
    [InlineData("provider")]
    [InlineData("model")]
    [InlineData("revision")]
    [InlineData("output")]
    [InlineData("metadata")]
    public void Changed_binding_or_unverified_transport_policy_falls_back_before_invoking_the_counter(string change)
    {
        var counter = new Counter(); var meter = new LocalReferenceRequestTokenMeter(Route, Policy, counter);
        var route = change switch { "endpoint" => Route with { EndpointKey = "proxy" }, "provider" => Route with { ProviderId = "other" },
            "model" => Route with { ModelId = "other" }, "revision" => Route with { ConfigurationRevision = "new" }, _ => Route };
        var request = change switch {
            "output" => new ChatRequest("fixture", "c", Request().Messages, maxOutputTokens: 1024),
            "metadata" => new ChatRequest("fixture", "c", Request().Messages, metadata: new Dictionary<string, string> { ["fgo_auxiliary"] = "understanding_review" }, maxOutputTokens: 2048),
            _ => Request()
        };
        Assert.Equal(new RequestTokenMeter().Measure(route, request), meter.Measure(route, request)); Assert.Equal(0, counter.Calls);
    }

    [Theory]
    [InlineData("input")]
    [InlineData("wire")]
    [InlineData("policy")]
    [InlineData("zero")]
    [InlineData("negative")]
    [InlineData("unavailable")]
    [InlineData("io")]
    [InlineData("timeout")]
    public void Unverified_or_unavailable_counts_preserve_the_complete_conservative_guard(string failure)
    {
        var counter = new Counter { Failure = failure }; var meter = new LocalReferenceRequestTokenMeter(Route, Policy, counter);
        var request = Request(new string('中', 6000));
        var measured = meter.Measure(Route, request);
        Assert.Equal(new RequestTokenMeter().Measure(Route, request), measured);
        Assert.True(measured.InputTokens > 16000); Assert.Null(measured.ReferencePolicyHash);
        Assert.Equal(1, counter.Calls);
    }

    [Fact]
    public void Forged_proxy_binding_or_malformed_policy_cannot_opt_into_local_counts()
    {
        var counter = new Counter();
        var proxy = Route with { EndpointKey = "proxy" };
        Assert.Equal(TokenCountKind.Estimated, new LocalReferenceRequestTokenMeter(proxy, Policy, counter).Measure(proxy, Request()).Kind);
        Assert.Equal(TokenCountKind.Estimated, new LocalReferenceRequestTokenMeter(Route, "PRIVATE_NOT_A_POLICY_HASH", counter).Measure(Route, Request()).Kind);
        Assert.Equal(0, counter.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Image_requests_never_enter_text_only_reference_counter_or_lose_image_budget(bool buddy)
    {
        var route = buddy ? ModelRouteKey.From(new("custom-openai-compatible", "http://127.0.0.1:8326/v1", "buddy/deepseek-v4.1-flash")) : Route;
        var scope = new ToolScope("conversation", "role", null);
        var store = new SelectedImageStore(s => s == scope);
        store.Grant(scope, route, new(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="), 1, 1), "user");
        store.Bind(scope, route, "user", "run", default);
        var request = ChatRequest.CreateAgent("role", "conversation", [
            new(ModelMessageRole.User, "synthetic", []),
            new(ModelMessageRole.Assistant, "", [new("call", "image.read", "{}")]),
            new(ModelMessageRole.Tool, "{}", [], "call") { Image = store.Read(scope, route, "run", default) }
        ], maxOutputTokens: 2048);
        ChatImageInputs.Prepare(request, route, store, ImageModelCapabilities.Supports(route), default);
        var counter = new Counter(); var meter = new LocalReferenceRequestTokenMeter(route, Policy, counter);
        var measured = meter.Measure(route, request);
        Assert.Equal(new RequestTokenMeter().Measure(route, request), measured);
        Assert.Equal(TokenCountKind.Estimated, measured.Kind); Assert.True(measured.InputTokens > 8192);
        Assert.Equal(0, counter.Calls);
        meter.RecordUsage(route, request, new(1498, 5));
        Assert.Equal(1498, meter.Measure(route, request).InputTokens);
        Assert.Equal(TokenCountKind.ProviderUsage, meter.Measure(route, request).Kind);
        Assert.Equal(0, counter.Calls);
    }

    private sealed class Counter : ILocalReferenceTokenCounter
    {
        public int Calls { get; private set; }
        public string? Failure { get; init; }
        public LocalReferenceTokenCount? TryCount(string input, string wire)
        {
            Calls++;
            if (Failure == "io") throw new IOException("PRIVATE_COUNTER_DETAIL");
            if (Failure == "timeout") throw new TimeoutException("PRIVATE_COUNTER_DETAIL");
            if (Failure == "unavailable") return null;
            return new(Failure == "zero" ? 0 : Failure == "negative" ? -1 : 1234,
                Failure == "input" ? "bad" : Hash(input), Failure == "wire" ? "bad" : Hash(wire), Failure == "policy" ? "bad" : Policy);
        }
    }
}
