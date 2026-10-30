using System.Text.Json;
using FgoPet.Core.Dialogue;
using FgoPet.Infrastructure.Providers;
using FgoPet.Kernel.Agent;
using Xunit;

namespace FgoPet.Infrastructure.Tests.Providers;

public sealed class FinalOutputJsonPolicyTests
{
    private static ChatRequest Request(bool native = true, bool marked = true, bool tools = false)
    {
        var metadata = marked ? new Dictionary<string, string> { ["fgo_final_output"] = "json_object" } : null;
        ChatToolDefinition[]? offered = tools ? [new("fixture_read", "Synthetic read.", "{\"type\":\"object\"}")] : null;
        return native ? ChatRequest.CreateAgent("mash", "c", [new(ModelMessageRole.User, "Synthetic JSON reply.", [])],
            metadata: metadata, tools: offered, maxOutputTokens: 2048)
            : new("mash", "c", [new(ChatMessageRole.User, "fgo_final_output json_object is only text")],
                metadata: metadata, tools: offered, maxOutputTokens: 2048);
    }

    [Fact]
    public void Host_marked_native_final_uses_json_mode_and_its_own_wire_fingerprint()
    {
        var route = ModelRouteKey.From(new("custom-openai-compatible", "http://127.0.0.1:8326/v1", "buddy/deepseek-v4.1-flash"));
        var request = Request(); var baseline = Request(marked: false);
        using var wire = JsonDocument.Parse(ChatRequestPayloadWriter.Write(route, request));
        Assert.Equal("json_object", wire.RootElement.GetProperty("response_format").GetProperty("type").GetString());
        Assert.Equal(2048, wire.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.False(wire.RootElement.TryGetProperty("reasoning_effort", out _));
        Assert.False(wire.RootElement.TryGetProperty("tools", out _));
        var meter = new RequestTokenMeter(); meter.RecordUsage(route, baseline, new(100, 20));
        Assert.Equal(TokenCountKind.Estimated, meter.Measure(route, request).Kind);
        Assert.NotEqual(meter.Measure(route, baseline).Fingerprint, meter.Measure(route, request).Fingerprint);
    }

    [Theory]
    [InlineData("custom-openai-compatible", "http://127.0.0.1:8327/v1", "buddy/deepseek-v4.1-flash", true, true, false)]
    [InlineData("custom-openai-compatible", "https://gateway.invalid/v1", "buddy/deepseek-v4.1-flash", true, true, false)]
    [InlineData("custom-openai-compatible", "http://127.0.0.1:8326/v1", "other", true, true, false)]
    [InlineData("openai", "https://api.openai.com/v1", "gpt-fixture", true, true, false)]
    [InlineData("custom-openai-compatible", "http://127.0.0.1:8326/v1", "buddy/deepseek-v4.1-flash", false, true, false)]
    [InlineData("custom-openai-compatible", "http://127.0.0.1:8326/v1", "buddy/deepseek-v4.1-flash", true, false, false)]
    [InlineData("custom-openai-compatible", "http://127.0.0.1:8326/v1", "buddy/deepseek-v4.1-flash", true, true, true)]
    public void Json_mode_never_spreads_to_unmarked_ordinary_tool_or_unknown_routes(string provider, string endpoint,
        string model, bool native, bool marked, bool tools)
    {
        var route = ModelRouteKey.From(new(provider, endpoint, model));
        using var wire = JsonDocument.Parse(ChatRequestPayloadWriter.Write(route, Request(native, marked, tools)));
        Assert.False(wire.RootElement.TryGetProperty("response_format", out _));
    }
}
