using System.Text.Json;
using FgoPet.Extensibility;
using FgoPet.Kernel.Agent;
using Xunit;

namespace FgoPet.Agent.Tests.Tools;

public sealed class UnknownResultEvidenceTests
{
    [Fact]
    public void Unknown_command_keeps_bounded_private_evidence_without_claiming_completion()
    {
        var result = new ToolResult(false, JsonSerializer.SerializeToElement(new { exitCode = (int?)null, stdout = "fixture-private-output" }), "SHELL_TIMEOUT")
            { ExecutionState = ToolExecutionState.Unknown };
        var outcome = ToolResultNormalizer.Normalize(ToolEffect.Command, result);
        Assert.Equal(ToolExecutionOutcomeKind.ExecutionUnknown, outcome.Kind);
        Assert.False(outcome.Result!.Success);
        Assert.Equal(JsonValueKind.Null, outcome.Result.Payload.GetProperty("exitCode").ValueKind);
        Assert.Equal("fixture-private-output", outcome.Result.Payload.GetProperty("stdout").GetString());
        Assert.Null(outcome.Result.Conversation);
    }
    [Fact]
    public void Oversized_unknown_evidence_still_stops_as_unknown()
    {
        var outcome = ToolResultNormalizer.Normalize(ToolEffect.Command,
            new(false, JsonSerializer.SerializeToElement(new { value = new string('x', 65536) }), "SHELL_TIMEOUT") { ExecutionState = ToolExecutionState.Unknown });
        Assert.Equal(ToolExecutionOutcomeKind.ExecutionUnknown, outcome.Kind);
        Assert.Equal(ToolExecutionState.Unknown, outcome.Result!.ExecutionState);
        Assert.Equal("{}", outcome.Result.Payload.GetRawText());
    }
}
