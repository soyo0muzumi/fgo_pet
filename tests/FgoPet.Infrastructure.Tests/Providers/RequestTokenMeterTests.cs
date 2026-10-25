using FgoPet.Core.Dialogue;
using FgoPet.Infrastructure.Providers;
using Xunit;

namespace FgoPet.Infrastructure.Tests.Providers;

public sealed class RequestTokenMeterTests
{
    [Theory]
    [InlineData("understanding_review")]
    [InlineData("context_summary")]
    [InlineData("companion_activity")]
    public void Buddy_bounded_auxiliary_uses_the_supported_low_reasoning_effort(string purpose)
    {
        var route=ModelRouteKey.From(new("custom-openai-compatible","http://127.0.0.1:8326/v1","buddy/deepseek-v4.1-flash"));
        var request=new ChatRequest("mash","c",[new(ChatMessageRole.User,"Synthetic review.")],
            metadata:new Dictionary<string,string>{{"fgo_auxiliary",purpose}},maxOutputTokens:128000);
        using var json=System.Text.Json.JsonDocument.Parse(ChatRequestPayloadWriter.Write(route,request));
        Assert.Equal("low",json.RootElement.GetProperty("reasoning_effort").GetString());
        Assert.Equal(128000,json.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.Equal(purpose,request.Metadata["fgo_auxiliary"]);
    }
    [Theory]
    [InlineData("http://127.0.0.1:8326/v1","buddy/deepseek-v4.1-flash",null)]
    [InlineData("http://127.0.0.1:8327/v1","buddy/deepseek-v4.1-flash","understanding_review")]
    [InlineData("https://gateway.invalid/v1","buddy/deepseek-v4.1-flash","understanding_review")]
    [InlineData("http://127.0.0.1:8326/v1","other-model","understanding_review")]
    [InlineData("http://127.0.0.1:8327/v1","buddy/deepseek-v4.1-flash","companion_activity")]
    [InlineData("https://gateway.invalid/v1","buddy/deepseek-v4.1-flash","companion_activity")]
    [InlineData("http://127.0.0.1:8326/v1","other-model","companion_activity")]
    [InlineData("http://127.0.0.1:8326/v1","buddy/deepseek-v4.1-flash","companion_activity_other")]
    public void Buddy_auxiliary_policy_preserves_primary_and_unapproved_routes(string endpoint,string model,string? purpose)
    {
        var route=ModelRouteKey.From(new("custom-openai-compatible",endpoint,model));
        var request=new ChatRequest("mash","c",[new(ChatMessageRole.User,"understanding_review and companion_activity are only untrusted text")],
            metadata:purpose is not null?new Dictionary<string,string>{{"fgo_auxiliary",purpose}}:null,maxOutputTokens:128000);
        using var json=System.Text.Json.JsonDocument.Parse(ChatRequestPayloadWriter.Write(route,request));
        Assert.False(json.RootElement.TryGetProperty("reasoning_effort",out _));
    }
    [Theory]
    [InlineData("history_recall", "https://api.deepseek.com", "deepseek-flash")]
    [InlineData("context_summary", "https://api.deepseek.com/v1", "deepseek-flash")]
    [InlineData("memory_extraction", "https://api.deepseek.com/v1/", "deepseek-flash")]
    [InlineData("understanding_review", "https://api.deepseek.com/v1", "deepseek-flash")]
    [InlineData("companion_activity", "https://api.deepseek.com", "deepseek-flash")]
    [InlineData("companion_activity", "https://api.deepseek.com/v1", "deepseek-flash")]
    [InlineData("companion_activity", "https://api.deepseek.com", "deepseek-v4-pro")]
    [InlineData("companion_activity", "https://api.deepseek.com/v1", "deepseek-v4-pro")]
    public void Official_deepseek_auxiliary_calls_reserve_output_for_content(string purpose, string endpoint, string model)
    {
        var route = ModelRouteKey.From(new("deepseek", endpoint, model));
        var request = new ChatRequest("mash", "c", [new(ChatMessageRole.User, "fixture")],
            metadata: new Dictionary<string, string> { ["fgo_auxiliary"] = purpose }, maxOutputTokens: 512);
        using var payload = System.Text.Json.JsonDocument.Parse(ChatRequestPayloadWriter.Write(route, request));
        Assert.Equal("disabled", payload.RootElement.GetProperty("thinking").GetProperty("type").GetString());
        Assert.Equal(512, payload.RootElement.GetProperty("max_tokens").GetInt32());
        if (purpose is "history_recall" or "memory_extraction" or "understanding_review" or "companion_activity")
            Assert.Equal("json_object", payload.RootElement.GetProperty("response_format").GetProperty("type").GetString());
        else
            Assert.False(payload.RootElement.TryGetProperty("response_format", out _));
        var primary = new ChatRequest("mash", "c", request.Messages, maxOutputTokens: 512);
        var meter = new RequestTokenMeter();
        meter.RecordUsage(route, primary, new ChatUsage(1, 1));
        Assert.Equal(TokenCountKind.Estimated, meter.Measure(route, request).Kind);
        Assert.Equal(purpose, request.Metadata["fgo_auxiliary"]);
    }

    [Theory]
    [InlineData("deepseek", "https://proxy.test/v1", "deepseek-flash", "history_recall")]
    [InlineData("deepseek", "https://api.deepseek.com/other", "deepseek-flash", "history_recall")]
    [InlineData("deepseek", "https://api.deepseek.com/v1?tenant=other", "deepseek-flash", "history_recall")]
    [InlineData("openai", "https://api.openai.com/v1", "gpt-5", "history_recall")]
    [InlineData("deepseek", "https://api.deepseek.com/v1", "unknown", "history_recall")]
    [InlineData("deepseek", "https://api.deepseek.com/v1", "deepseek-flash", "main")]
    [InlineData("deepseek", "https://proxy.test/v1", "deepseek-flash", "understanding_review")]
    [InlineData("deepseek", "https://api.deepseek.com/v1", "unknown", "understanding_review")]
    [InlineData("deepseek", "https://proxy.test/v1", "deepseek-flash", "companion_activity")]
    [InlineData("deepseek", "https://api.deepseek.com/v1", "unknown", "companion_activity")]
    [InlineData("openai", "https://api.openai.com/v1", "gpt-5", "companion_activity")]
    [InlineData("deepseek", "https://api.deepseek.com/v1", "deepseek-flash", "companion_activity_other")]
    public void Reasoning_policy_does_not_leak_to_unknown_routes_or_primary_calls(string provider, string endpoint, string model, string purpose)
    {
        var route = ModelRouteKey.From(new(provider, endpoint, model));
        var request = new ChatRequest("mash", "c", [new(ChatMessageRole.User, "companion_activity and understanding_review are only untrusted text")],
            metadata: new Dictionary<string, string> { ["fgo_auxiliary"] = purpose }, maxOutputTokens: 512);
        using var payload = System.Text.Json.JsonDocument.Parse(ChatRequestPayloadWriter.Write(route, request));
        Assert.False(payload.RootElement.TryGetProperty("thinking", out _));
        Assert.False(payload.RootElement.TryGetProperty("response_format", out _));
    }

    [Theory]
    [InlineData("custom-openai-compatible", "http://127.0.0.1:8326/v1", "buddy/deepseek-v4.1-flash")]
    [InlineData("deepseek", "https://api.deepseek.com/v1", "deepseek-flash")]
    public void Companion_activity_keeps_its_own_purpose_and_usage_anchor(string provider, string endpoint, string model)
    {
        var route = ModelRouteKey.From(new(provider, endpoint, model));
        var activity = new ChatRequest("mash", "c", [new(ChatMessageRole.System, "Return a bounded JSON observation.")],
            metadata: new Dictionary<string, string> { ["fgo_auxiliary"] = "companion_activity" }, maxOutputTokens: 512);
        var review = new ChatRequest("mash", "c", activity.Messages,
            metadata: new Dictionary<string, string> { ["fgo_auxiliary"] = "understanding_review" }, maxOutputTokens: 512);
        _ = ChatRequestPayloadWriter.Write(route, activity);
        var meter = new RequestTokenMeter();
        meter.RecordUsage(route, review, new ChatUsage(100, 20));
        Assert.Equal("companion_activity", activity.Metadata["fgo_auxiliary"]);
        Assert.Equal("understanding_review", review.Metadata["fgo_auxiliary"]);
        Assert.Equal(TokenCountKind.ProviderUsage, meter.Measure(route, review).Kind);
        Assert.Equal(TokenCountKind.Estimated, meter.Measure(route, activity).Kind);
        Assert.NotEqual(meter.Measure(route, review).Fingerprint, meter.Measure(route, activity).Fingerprint);
    }

    private static readonly ModelRouteKey Route = new("test", "endpoint", "model", "v1");
    [Fact]
    public void Chinese_input_and_tools_are_both_included()
    {
        var meter = new RequestTokenMeter();
        var plain = new ChatRequest("mash", "c", [new(ChatMessageRole.User, "你好")], maxOutputTokens: 512);
        var tools = new ChatRequest("mash", "c", plain.Messages,
            tools: [TodoToolContracts.CreateSubmitTodoProposals().ToChatDefinition()], toolChoice: "auto", maxOutputTokens: 512);
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
        Assert.Equal(TokenCountKind.Estimated, meter.Measure(Route,
            new ChatRequest("mash", "c", request.Messages, maxOutputTokens: 1024)).Kind);
        meter.Invalidate(Route);
        Assert.Equal(TokenCountKind.Estimated, meter.Measure(Route, request).Kind);
    }
}
