using System.Collections.Immutable;
using System.Text;
using FgoPet.Extensibility;
using FgoPet.Kernel.Agent;
using Xunit;

namespace FgoPet.Agent.Tests.Contracts;

public sealed class ProtocolTests
{
    [Theory]
    [InlineData(4, 65536, true)]
    [InlineData(5, 65536, false)]
    [InlineData(1, 65537, false)]
    public void Exact_utf8_argument_and_batch_boundaries(int count, int bytes, bool accepted)
    {
        var arguments = new string('x', bytes);
        var response = Calls(Enumerable.Range(0, count).Select(i => new ModelToolCall("call" + i, "fixture.read", arguments)).ToArray());
        if (accepted) ModelProtocol.ValidateResponse(response, ImmutableHashSet<string>.Empty);
        else Assert.Throws<AgentProtocolException>(() => ModelProtocol.ValidateResponse(response, ImmutableHashSet<string>.Empty));
    }

    [Theory]
    [InlineData(ModelMessageRole.User)]
    [InlineData(ModelMessageRole.System)]
    [InlineData(ModelMessageRole.Tool)]
    public void Only_assistant_messages_can_own_calls(ModelMessageRole role) =>
        Assert.Throws<AgentProtocolException>(() => ModelProtocol.ValidateTranscript(
            [new(role, "", [new("call", "fixture.read", "{}")])]));

    [Theory]
    [InlineData(0, 16, 8)]
    [InlineData(8, -1, 8)]
    [InlineData(8, 16, 0)]
    public void Non_positive_budget_is_rejected(int models, int tools, int skills) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new RunBudget(models, tools, skills));

    [Fact]
    public void Duplicate_ids_reject_entire_batch()
    {
        var response = Calls(new("a", "fixture.read", "{}"), new("a", "fixture.read", "{}"));
        Assert.Equal("MODEL_DUPLICATE_CALL_ID", Assert.Throws<AgentProtocolException>(
            () => ModelProtocol.ValidateResponse(response, ImmutableHashSet<string>.Empty)).Code);
    }

    [Fact]
    public void Previous_step_id_cannot_be_reused() => Assert.Throws<AgentProtocolException>(() =>
        ModelProtocol.ValidateResponse(Calls(new ModelToolCall("a", "fixture.read", "{}")), ImmutableHashSet.Create("a")));

    [Theory]
    [InlineData(false, "stop", "answer")]
    [InlineData(true, "length", "answer")]
    [InlineData(true, "stop", "")]
    public void Incomplete_truncated_or_empty_response_is_not_final(bool complete, string finish, string text) =>
        Assert.Throws<AgentProtocolException>(() => ModelProtocol.ValidateResponse(
            new(new(ModelMessageRole.Assistant, text, []), finish, complete), ImmutableHashSet<string>.Empty));

    [Fact]
    public void Arguments_are_bounded_by_utf8_bytes()
    {
        var json = "{\"text\":\"" + new string('中', 22000) + "\"}";
        Assert.True(Encoding.UTF8.GetByteCount(json) > 65536);
        Assert.Throws<AgentProtocolException>(() => ModelProtocol.ValidateResponse(
            Calls(new ModelToolCall("a", "fixture.read", json)), ImmutableHashSet<string>.Empty));
    }

    [Fact]
    public void Seventeen_calls_are_rejected_before_execution() => Assert.Throws<AgentProtocolException>(() =>
        ModelProtocol.ValidateResponse(Calls(Enumerable.Range(0, 17).Select(i =>
            new ModelToolCall("c" + i, "fixture.read", "{}")).ToArray()), ImmutableHashSet<string>.Empty));

    [Fact]
    public void Valid_ids_with_bad_json_can_receive_parameter_error_observations()
    {
        var call = new ModelToolCall("a", "fixture.read", "{");
        ModelProtocol.ValidateResponse(Calls(call), ImmutableHashSet<string>.Empty);
        Assert.False(call.TryGetArguments(out _));
    }

    [Fact]
    public void Parsed_arguments_have_independent_lifetime()
    {
        var call = new ModelToolCall("a", "fixture.read", "{\"value\":42}");
        Assert.True(call.TryGetArguments(out var arguments));
        Assert.Equal(42, arguments.GetProperty("value").GetInt32());
    }

    [Fact]
    public void Transcript_requires_ordered_complete_call_groups()
    {
        var assistant = Calls(new("a", "fixture.read", "{}"), new("b", "fixture.read", "{}")).AssistantMessage;
        Assert.Throws<AgentProtocolException>(() => ModelProtocol.ValidateTranscript([assistant,
            new(ModelMessageRole.Tool, "{}", [], "b"), new(ModelMessageRole.Tool, "{}", [], "a")]));
        ModelProtocol.ValidateTranscript([new(ModelMessageRole.User, "fixture", []), assistant,
            new(ModelMessageRole.Tool, "{}", [], "a"), new(ModelMessageRole.Tool, "{}", [], "b")]);
    }

    [Fact]
    public void Existing_tool_constructors_remain_compatible()
    {
        var invocation = new ToolInvocation(new("conversation", "role", null), default);
        var result = new ToolResult(false, default, "FIXTURE");
        Assert.Null(invocation.ExecutionContext);
        Assert.Null(result.ExecutionState);
    }

    private static ModelStepResponse Calls(params ModelToolCall[] calls) =>
        new(new(ModelMessageRole.Assistant, "", calls.ToImmutableArray()), "tool_calls", true);
}
