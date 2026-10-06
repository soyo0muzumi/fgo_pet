using System.Collections.Immutable;
using System.Text.Json;
using FgoPet.Core.Dialogue;
using FgoPet.Infrastructure.Providers;
using FgoPet.Kernel.Agent;
using Xunit;

namespace FgoPet.Infrastructure.Tests.Providers;

public sealed class NativeRequestProtocolTests
{
    private static readonly ModelRouteKey Route = new("fixture", "endpoint", "model", "v1");
    private static ImmutableArray<ModelMessage> Messages(string arguments = "{}", string result = "{\"count\":1}") =>
    [
        new(ModelMessageRole.User, "list", []),
        new(ModelMessageRole.Assistant, "", [new("call-a", "todo.list", arguments)]),
        new(ModelMessageRole.Tool, result, [], "call-a"),
        new(ModelMessageRole.Assistant, "done", [])
    ];

    [Fact]
    public void Actual_wire_retains_complete_call_groups_when_tool_is_no_longer_offered()
    {
        var request = ChatRequest.CreateAgent("mash", "fixture", Messages(), maxOutputTokens: 512);
        using var body = JsonDocument.Parse(ChatRequestPayloadWriter.Write(Route, request));
        var messages = body.RootElement.GetProperty("messages");
        Assert.Equal(4, messages.GetArrayLength());
        Assert.Equal("assistant", messages[1].GetProperty("role").GetString());
        var call = messages[1].GetProperty("tool_calls")[0];
        Assert.Equal("call-a", call.GetProperty("id").GetString());
        Assert.Equal(ModelToolNameMap.GetWireName("todo.list"), call.GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("{}", call.GetProperty("function").GetProperty("arguments").GetString());
        Assert.Equal("tool", messages[2].GetProperty("role").GetString());
        Assert.Equal("call-a", messages[2].GetProperty("tool_call_id").GetString());
        Assert.Equal("done", messages[3].GetProperty("content").GetString());
        Assert.False(body.RootElement.TryGetProperty("tools", out _));
        Assert.Equal(4, request.EffectiveMessageCount);
    }

    [Fact]
    public void Meter_and_wire_share_messages_and_usage_requires_exact_parameters_results_and_metadata()
    {
        var meter = new RequestTokenMeter();
        var request = ChatRequest.CreateAgent("mash", "fixture", Messages(), maxOutputTokens: 512);
        var anchor = meter.Measure(Route, request);
        meter.RecordUsage(Route, request, new ChatUsage(17, 2));
        Assert.Equal(17, meter.Measure(Route, request).InputTokens);
        foreach (var changed in new[] {
            ChatRequest.CreateAgent("mash", "fixture", Messages("{\"filter\":1}"), maxOutputTokens: 512),
            ChatRequest.CreateAgent("mash", "fixture", Messages(result: "{\"count\":2}"), maxOutputTokens: 512),
            ChatRequest.CreateAgent("mash", "fixture", Messages(), metadata: new Dictionary<string,string> { ["fixture"] = "changed" }, maxOutputTokens: 512),
            ChatRequest.CreateAgent("mash", "fixture", Messages(), maxOutputTokens: 1024)
        }) {
            var measured = meter.Measure(Route, changed);
            Assert.NotEqual(anchor.Fingerprint, measured.Fingerprint);
            Assert.Equal(TokenCountKind.Estimated, measured.Kind);
        }
        Assert.Equal(ChatRequestPayloadWriter.WriteInput("model", request), ChatRequestInputEnvelope.Write("model", request));
    }

    [Fact]
    public void Missing_or_mispaired_results_fail_before_serialization()
    {
        Assert.Throws<AgentProtocolException>(() => ChatRequest.CreateAgent("mash", "fixture", Messages()[..2]));
        Assert.Throws<AgentProtocolException>(() => ChatRequest.CreateAgent("mash", "fixture",
            Messages().SetItem(2, new(ModelMessageRole.Tool, "{}", [], "wrong"))));
    }

    [Fact]
    public void Unresolved_wire_name_is_preserved_as_returned_by_provider()
    {
        var messages = Messages().SetItem(1, new(ModelMessageRole.Assistant, "",
            [new("call-a", "unoffered.name", "{}") { IsResolved = false }]));
        using var body = JsonDocument.Parse(ChatRequestPayloadWriter.Write(Route,
            ChatRequest.CreateAgent("mash", "fixture", messages)));
        Assert.Equal("unoffered.name", body.RootElement.GetProperty("messages")[1].GetProperty("tool_calls")[0]
            .GetProperty("function").GetProperty("name").GetString());
    }
}
