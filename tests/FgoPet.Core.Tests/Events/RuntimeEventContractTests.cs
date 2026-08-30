using FgoPet.Core.Events;
using FgoPet.Core.Focus;
using Xunit;

namespace FgoPet.Core.Tests.Events;

public sealed class RuntimeEventContractTests
{
    [Fact]
    public void Legacy_events_default_to_system_metadata()
    {
        var runtimeEvent = new RuntimeEvent(
            "event-1",
            "session-1",
            RuntimeEventType.FocusStarted,
            DateTimeOffset.UtcNow,
            1,
            FocusPhase.Focus,
            "servant-mash",
            ElapsedSeconds: 0,
            EffectiveSeconds: 0,
            Priority: 2);

        Assert.Equal(RuntimeEventSource.System, runtimeEvent.Source);
        Assert.Null(runtimeEvent.SubjectId);
        Assert.Null(runtimeEvent.Summary);
        Assert.False(runtimeEvent.IsPrivate);
    }

    [Fact]
    public void External_events_can_carry_optional_codex_metadata()
    {
        var runtimeEvent = new RuntimeEvent(
            "codex-task-1-7",
            "external-codex",
            "task_completed",
            DateTimeOffset.UtcNow,
            0,
            FocusPhase.Focus,
            "servant-mash",
            ElapsedSeconds: 0,
            EffectiveSeconds: 0,
            Priority: 1,
            Source: RuntimeEventSource.Codex,
            SubjectId: "task-1",
            Summary: "任务已完成",
            IsPrivate: false);

        Assert.Equal(RuntimeEventSource.Codex, runtimeEvent.Source);
        Assert.Equal("task-1", runtimeEvent.SubjectId);
        Assert.Equal("任务已完成", runtimeEvent.Summary);
        Assert.False(runtimeEvent.IsPrivate);
    }
}
