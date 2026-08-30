using FgoPet.Core.Agents;
using FgoPet.Core.Archives;
using Xunit;

namespace FgoPet.Core.Tests.Agents;

public sealed class AgentEventTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-08-30T08:00:00Z");

    [Fact]
    public void Event_identity_includes_source_instance_task_and_sequence()
    {
        var codexEvent = new AgentEvent("codex", "source-1", "task-1", 1, AgentEventType.TaskStarted, At);
        var otherSourceEvent = codexEvent with { SourceType = "claude" };
        var otherSequenceEvent = codexEvent with { Sequence = 2 };

        Assert.NotEqual(codexEvent.Identity, otherSourceEvent.Identity);
        Assert.NotEqual(codexEvent.Identity, otherSequenceEvent.Identity);
        Assert.Equal("codex/source-1/task-1/1", codexEvent.Identity);
    }

    [Fact]
    public void Private_event_is_anonymous_but_keeps_status_identity()
    {
        var eventRecord = new AgentEvent(
            "codex",
            "source-1",
            "task-1",
            7,
            AgentEventType.AttentionRequired,
            At,
            "Sensitive task title",
            "Sensitive path and credential",
            IsPrivate: true);

        Assert.True(eventRecord.IsPrivate);
        Assert.Null(eventRecord.Title);
        Assert.Null(eventRecord.Summary);
        Assert.Equal(AgentEventType.AttentionRequired, eventRecord.EventType);
    }

    [Fact]
    public void Capabilities_and_connection_settings_only_expose_opaque_targets()
    {
        var target = new AgentProjectTarget("opaque-project-1", "Personal project");
        var capabilities = new AgentCapabilities(true, true, OpenMode.AppOnly, new[] { target });
        var settings = new AgentConnectionSettings(
            Enabled: true,
            SourceEnabled: new Dictionary<string, bool> { ["codex"] = true },
            ProjectAllowlist: new Dictionary<string, IReadOnlyList<AgentProjectTarget>>
            {
                ["codex"] = new[] { target },
            });

        Assert.True(capabilities.CanCreateTask);
        Assert.Equal(OpenMode.AppOnly, capabilities.OpenMode);
        Assert.True(settings.IsSourceEnabled("codex"));
        Assert.True(settings.IsTargetAllowed("codex", "opaque-project-1"));
        Assert.Equal("Personal project", settings.ProjectAllowlist["codex"][0].DisplayName);
    }

    [Fact]
    public void Work_archive_keeps_only_confirmed_summary_and_coverage_keys()
    {
        var archive = new WorkArchive(
            "archive-1",
            new[] { "todo-1", "todo-2" },
            new[] { "codex" },
            DateOnly.Parse("2026-08-30"),
            "Release work completed.",
            At);

        Assert.Equal("archive-1", archive.ArchiveId);
        Assert.Equal(new[] { "todo-1", "todo-2" }, archive.CoveredTodoKeys);
        Assert.Equal(new[] { "codex" }, archive.SourceTypes);
        Assert.Equal("Release work completed.", archive.Summary);
    }
}
