using System.Text.Json;
using FgoPet.Extensibility;
using FgoPet.Kernel.Agent;
using Xunit;

namespace FgoPet.Agent.Tests.Tools;

public sealed class ToolResultNormalizerTests
{
    [Theory]
    [InlineData(null)]
    [InlineData(ToolExecutionState.Unknown)]
    public void Unproven_command_result_halts_even_if_provider_reports_success(ToolExecutionState? state)
    {
        var outcome = ToolResultNormalizer.Normalize(ToolEffect.Command,
            new ToolResult(true, JsonSerializer.SerializeToElement(new { ok = true })) { ExecutionState = state });
        Assert.Equal(ToolExecutionOutcomeKind.ExecutionUnknown, outcome.Kind);
        Assert.Equal("TOOL_EXECUTION_UNKNOWN", outcome.ErrorCode);
        Assert.False(outcome.Result!.Success);
    }

    [Theory]
    [InlineData(ToolExecutionState.Committed, true)]
    [InlineData(ToolExecutionState.NotExecuted, false)]
    public void Proven_command_result_preserves_owner_evidence(ToolExecutionState state, bool success)
    {
        var outcome = ToolResultNormalizer.Normalize(ToolEffect.Command,
            new ToolResult(success, default, success ? null : "VERSION_CONFLICT") { ExecutionState = state });
        Assert.Equal(ToolExecutionOutcomeKind.Completed, outcome.Kind);
        Assert.Equal(state, outcome.Result!.ExecutionState);
        Assert.Equal(success, outcome.Result.Success);
    }

    [Fact]
    public void Successful_command_claiming_not_executed_is_unknown()
    {
        var outcome = ToolResultNormalizer.Normalize(ToolEffect.Command,
            new ToolResult(true, default) { ExecutionState = ToolExecutionState.NotExecuted });
        Assert.Equal(ToolExecutionOutcomeKind.ExecutionUnknown, outcome.Kind);
    }
}
