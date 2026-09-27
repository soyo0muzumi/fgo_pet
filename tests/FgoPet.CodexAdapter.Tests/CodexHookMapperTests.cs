using FgoPet.CodexAdapter.Hooks;
using FgoPet.AgentProtocol.Messages;
using Xunit;

namespace FgoPet.CodexAdapter.Tests;

public sealed class CodexHookMapperTests
{
    [Theory]
    [InlineData(CodexHookKind.Started, "task_started")]
    [InlineData(CodexHookKind.Resumed, "task_resumed")]
    [InlineData(CodexHookKind.Attention, "attention_required")]
    [InlineData(CodexHookKind.Failed, "task_failed")]
    [InlineData(CodexHookKind.Cancelled, "task_cancelled")]
    public void Maps_only_deterministic_lifecycle_facts(CodexHookKind kind, string eventType)
    {
        var message = CodexHookMapper.Map(new CodexHookObservation("task-1", 3, kind, "safe status"), "codex", "source-1");

        Assert.Equal(eventType, message.EventType);
        Assert.Equal("safe status", message.Summary);
        Assert.Null(message.Title);
    }
}
