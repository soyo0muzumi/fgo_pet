using FgoPet.Core.Dialogue;
using FgoPet.Kernel.Agent;
using Xunit;

namespace FgoPet.Agent.Tests.Protocol;

public sealed class ModelStepAggregatorTests
{
    [Fact]
    public void Interleaved_calls_are_ordered_and_resolved_only_against_offered_aliases()
    {
        var map = new ModelToolNameMap(["todo.list", "todo.update"]);
        var stream = new ModelStepAggregator(map);
        stream.Add(new("", ToolCallDelta: new(1, "b", map.ToWireName("todo.update"), "{\"x\":")));
        stream.Add(new("", ToolCallDelta: new(0, "a", map.ToWireName("todo.list"), "{}")));
        stream.Add(new("", ToolCallDelta: new(1, argumentsDelta: "1}")));
        stream.Add(new("", IsComplete: true, FinishReason: "tool_calls"));
        var response = stream.Complete();
        Assert.Equal(new[] { "a", "b" }, response.ToolCalls.Select(c => c.CallId));
        Assert.Equal(new[] { "todo.list", "todo.update" }, response.ToolCalls.Select(c => c.Name));
        Assert.Equal("{\"x\":1}", response.ToolCalls[1].ArgumentsJson);
        Assert.All(response.ToolCalls, c => Assert.True(c.IsResolved));
    }

    [Theory]
    [InlineData("todo.list")]
    [InlineData("unknown")]
    public void Unoffered_names_are_unresolved_even_when_canonical_name_exists(string name)
    {
        var stream = new ModelStepAggregator(new ModelToolNameMap(["todo.list"]));
        stream.Add(new("", ToolCallDelta: new(0, "a", name, "{}")));
        stream.Add(new("", true, "tool_calls"));
        var call = Assert.Single(stream.Complete().ToolCalls);
        Assert.False(call.IsResolved);
        Assert.Equal(name, call.Name);
    }

    [Theory]
    [InlineData("id")]
    [InlineData("name")]
    public void Conflicting_identity_fragments_are_rejected(string field)
    {
        var stream = new ModelStepAggregator(new ModelToolNameMap(["first", "second"]));
        stream.Add(new("", ToolCallDelta: new(0, "a", "first", "{}")));
        Assert.Throws<AgentProtocolException>(() => stream.Add(new("", ToolCallDelta:
            field == "id" ? new(0, "b") : new(0, name: "second"))));
    }

    [Theory]
    [InlineData(false, "stop", "text")]
    [InlineData(true, "length", "text")]
    [InlineData(true, "stop", "")]
    [InlineData(true, null, "text")]
    public void Incomplete_truncated_or_empty_answers_are_never_final(bool complete, string? finish, string text)
    {
        var stream = new ModelStepAggregator(new ModelToolNameMap([]));
        stream.Add(new(text, complete, finish));
        Assert.Throws<AgentProtocolException>(() => stream.Complete());
    }

    [Theory]
    [InlineData(null, "first")]
    [InlineData("a", null)]
    public void Missing_call_identity_is_rejected(string? id, string? name)
    {
        var stream = new ModelStepAggregator(new ModelToolNameMap(["first"]));
        stream.Add(new("", ToolCallDelta: new(0, id, name, "{}")));
        stream.Add(new("", true, "tool_calls"));
        Assert.Throws<AgentProtocolException>(() => stream.Complete());
    }

    [Fact]
    public void Duplicate_call_ids_and_noncontiguous_indexes_are_rejected()
    {
        foreach (var index in new[] { 1, 2 })
        {
            var stream = new ModelStepAggregator(new ModelToolNameMap(["first"]));
            stream.Add(new("", ToolCallDelta: new(0, "a", "first", "{}")));
            stream.Add(new("", ToolCallDelta: new(index, "a", "first", "{}")));
            stream.Add(new("", true, "tool_calls"));
            Assert.Throws<AgentProtocolException>(() => stream.Complete());
        }
    }

    [Fact]
    public void Cumulative_arguments_are_bounded_before_completion()
    {
        var stream = new ModelStepAggregator(new ModelToolNameMap(["first"]));
        stream.Add(new("", ToolCallDelta: new(0, "a", "first")));
        Assert.Throws<AgentProtocolException>(() =>
        {
            for (var i = 0; i < 17; i++) stream.Add(new("", ToolCallDelta: new(0, argumentsDelta: new string('x', 4096))));
        });
    }

    [Fact]
    public void Batch_arguments_and_content_obey_utf8_byte_bounds()
    {
        var stream = new ModelStepAggregator(new ModelToolNameMap(["first"]));
        Assert.Throws<AgentProtocolException>(() => {
            for (var call = 0; call < 5; call++) {
                stream.Add(new("", ToolCallDelta: new(call, "call-" + call, "first")));
                for (var part = 0; part < 16; part++)
                    stream.Add(new("", ToolCallDelta: new(call, argumentsDelta: new string('x', 4096))));
            }
        });
        var text = new ModelStepAggregator(new ModelToolNameMap([]));
        Assert.Throws<AgentProtocolException>(() => {
            for (var part = 0; part < 11; part++) text.Add(new(new string('中', 4096)));
        });
    }

    [Fact]
    public void Excessive_index_and_reused_previous_call_id_are_rejected()
    {
        var stream = new ModelStepAggregator(new ModelToolNameMap(["first"]));
        Assert.Throws<AgentProtocolException>(() => stream.Add(new("", ToolCallDelta: new(16, "a", "first", "{}"))));
        var reused = new ModelStepAggregator(new ModelToolNameMap(["first"]));
        reused.Add(new("", ToolCallDelta: new(0, "previous", "first", "{}")));
        reused.Add(new("", true, "tool_calls"));
        Assert.Throws<AgentProtocolException>(() => reused.Complete(new HashSet<string> { "previous" }));
    }

    [Fact]
    public void Invalid_arguments_stay_a_paired_call_for_safe_schema_observation()
    {
        var stream = new ModelStepAggregator(new ModelToolNameMap(["first"]));
        stream.Add(new("", ToolCallDelta: new(0, "a", "first", "invalid")));
        stream.Add(new("", true, "tool_calls"));
        Assert.False(Assert.Single(stream.Complete().ToolCalls).TryGetArguments(out _));
    }

    [Fact]
    public void Alias_collision_is_rejected_and_alias_is_stable_without_offered_tools()
    {
        var alias = ModelToolNameMap.GetWireName("todo.list");
        Assert.Matches("^[A-Za-z0-9_-]{1,64}$", alias);
        Assert.Throws<AgentProtocolException>(() => new ModelToolNameMap(["todo.list", alias]));
        Assert.Equal(alias, new ModelToolNameMap([]).ToWireName("todo.list"));
    }
}
