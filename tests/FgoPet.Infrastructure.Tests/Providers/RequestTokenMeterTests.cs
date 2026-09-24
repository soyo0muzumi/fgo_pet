using FgoPet.Core.Dialogue;
using FgoPet.Infrastructure.Providers;
using Xunit;

namespace FgoPet.Infrastructure.Tests.Providers;

public sealed class RequestTokenMeterTests
{
    [Theory]
    [InlineData("history_recall", "https://api.deepseek.com")]
    [InlineData("context_summary", "https://api.deepseek.com/v1")]
    [InlineData("memory_extraction", "https://api.deepseek.com/v1/")]
    public void Official_deepseek_auxiliary_calls_reserve_output_for_content(string purpose, string endpoint)
    {
        var route = ModelRouteKey.From(new("deepseek", endpoint, "deepseek-flash"));
        var request = new ChatRequest("mash", "c", [new(ChatMessageRole.User, "fixture")],
            metadata: new Dictionary<string, string> { ["fgo_auxiliary"] = purpose }, maxOutputTokens: 512);
        using var payload = System.Text.Json.JsonDocument.Parse(ChatRequestPayloadWriter.Write(route, request));
        Assert.Equal("disabled", payload.RootElement.GetProperty("thinking").GetProperty("type").GetString());
        Assert.Equal(512, payload.RootElement.GetProperty("max_tokens").GetInt32());
        if (purpose is "history_recall" or "memory_extraction")
            Assert.Equal("json_object", payload.RootElement.GetProperty("response_format").GetProperty("type").GetString());
        else
            Assert.False(payload.RootElement.TryGetProperty("response_format", out _));
        var primary = new ChatRequest("mash", "c", request.Messages, maxOutputTokens: 512);
        var meter = new RequestTokenMeter();
        meter.RecordUsage(route, primary, new ChatUsage(1, 1));
        Assert.Equal(TokenCountKind.Estimated, meter.Measure(route, request).Kind);
    }

    [Theory]
    [InlineData("deepseek", "https://proxy.test/v1", "deepseek-flash", "history_recall")]
    [InlineData("deepseek", "https://api.deepseek.com/other", "deepseek-flash", "history_recall")]
    [InlineData("deepseek", "https://api.deepseek.com/v1?tenant=other", "deepseek-flash", "history_recall")]
    [InlineData("openai", "https://api.openai.com/v1", "gpt-5", "history_recall")]
    [InlineData("deepseek", "https://api.deepseek.com/v1", "unknown", "history_recall")]
    [InlineData("deepseek", "https://api.deepseek.com/v1", "deepseek-flash", "main")]
    public void Reasoning_policy_does_not_leak_to_unknown_routes_or_primary_calls(string provider, string endpoint, string model, string purpose)
    {
        var route = ModelRouteKey.From(new(provider, endpoint, model));
        var request = new ChatRequest("mash", "c", [new(ChatMessageRole.User, "fixture")],
            metadata: new Dictionary<string, string> { ["fgo_auxiliary"] = purpose }, maxOutputTokens: 512);
        using var payload = System.Text.Json.JsonDocument.Parse(ChatRequestPayloadWriter.Write(route, request));
        Assert.False(payload.RootElement.TryGetProperty("thinking", out _));
    }

    private static readonly ModelRouteKey Route = new("test", "endpoint", "model", "v1");
    [Fact]
    public void Chinese_input_and_tools_are_both_included()
    {
        var meter = new RequestTokenMeter();
        var plain = new ChatRequest("mash", "c", [new(ChatMessageRole.User, "你好")], maxOutputTokens: 512);
        var tools = new ChatRequest("mash", "c", plain.Messages,
            tools: [TodoToolContracts.CreateSubmitTodoProposals()], toolChoice: "auto", maxOutputTokens: 512);
        Assert.True(meter.Measure(Route, tools).InputTokens > meter.Measure(Route, plain).InputTokens + 100);
        Assert.Equal(TokenCountKind.Estimated, meter.Measure(Route, plain).Kind);
    }

    [Fact]
    public void Usage_anchor_requires_exact_content_and_route()
    {
        var meter = new RequestTokenMeter();
        var request = new ChatRequest("mash", "c", [new(ChatMessageRole.User, "你好")], maxOutputTokens: 512);
        meter.RecordUsage(Route, request, new ChatUsage(18, 5));
        Assert.Equal(18, meter.Measure(Route, request).InputTokens);
        Assert.Equal(TokenCountKind.ProviderUsage, meter.Measure(Route, request).Kind);
        var changed = new ChatRequest("mash", "c", [new(ChatMessageRole.User, "你好吗")], maxOutputTokens: 512);
        Assert.Equal(TokenCountKind.Estimated, meter.Measure(Route, changed).Kind);
        Assert.Equal(TokenCountKind.Estimated, meter.Measure(Route with { EndpointKey = "other" }, request).Kind);
        meter.Invalidate(Route);
        Assert.Equal(TokenCountKind.Estimated, meter.Measure(Route, request).Kind);
    }
}
