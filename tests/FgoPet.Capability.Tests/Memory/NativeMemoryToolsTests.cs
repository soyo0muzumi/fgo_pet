using System.Text;
using System.Text.Json;
using FgoPet.Core.Memory;
using FgoPet.Extensibility;
using FgoPet.Plugin.Memory;
using Xunit;

namespace FgoPet.Capability.Tests.Memory;

public sealed class NativeMemoryToolsTests
{
    [Fact]
    public async Task Search_uses_host_scope_and_filters_untrusted_recall_results_before_projection()
    {
        var recall = new FakeRecall(
            Memory("generic", "role-a", "general fact", isEnabled: true, version: 3),
            Memory("current", "role-a", "project fact", isEnabled: true, projectId: "project-a", version: 7),
            Memory("disabled", "role-a", "disabled secret", isEnabled: false),
            Memory("wrong-role", "role-b", "other role secret", isEnabled: true),
            Memory("wrong-role-project", "role-b", "other role project secret", isEnabled: true, projectId: "project-a"),
            Memory("wrong-project", "role-a", "other project secret", isEnabled: true, projectId: "project-b"));
        var tool = Tool(recall);

        var result = await Invoke(tool, new ToolScope("conversation-1", "role-a", "project-a"),
            new { query = "fact" });

        Assert.True(result.Success);
        Assert.Equal(ToolEffect.ReadOnly, tool.Descriptor.Effect);
        Assert.Equal(new MemoryScope("role-a", "project-a"), recall.LastScope);
        Assert.Equal(1, result.Payload.GetProperty("revision").GetInt64());
        Assert.Equal(new[] { ("generic", 3), ("current", 7) },
            ReadItems(result).Select(item => (item.Id, item.Version)));
        Assert.DoesNotContain("disabled secret", result.Payload.GetRawText());
        Assert.DoesNotContain("other role secret", result.Payload.GetRawText());
        Assert.DoesNotContain("other role project secret", result.Payload.GetRawText());
        Assert.DoesNotContain("other project secret", result.Payload.GetRawText());
    }

    [Fact]
    public async Task Search_rejects_model_arguments_that_try_to_override_the_host_scope()
    {
        var recall = new FakeRecall(Memory("memory-1", "role-b", "other role secret", isEnabled: true));
        var tool = Tool(recall);

        var result = await Invoke(tool, Scope(), new { query = "fact", roleId = "role-b", projectId = "project-b" });

        AssertInvalidArguments(result);
        Assert.Equal(0, recall.CallCount);
    }

    [Fact]
    public async Task Search_returns_unavailable_without_recalling_when_disabled()
    {
        var recall = new FakeRecall(Memory("memory-1", "role-a", "private fact", isEnabled: true));
        var tool = Assert.Single(NativeMemoryTools.Create(recall, () => false));

        var result = await Invoke(tool, Scope(), new { query = "fact" });

        Assert.False(result.Success);
        Assert.Equal("MEMORY_UNAVAILABLE", result.ErrorCode);
        Assert.Equal(ToolExecutionState.NotExecuted, result.ExecutionState);
        Assert.Equal("{}", result.Payload.GetRawText());
        Assert.Equal(0, recall.CallCount);
    }

    [Fact]
    public async Task Search_discards_owner_results_if_disabled_during_retrieval()
    {
        var enabled = true;
        var recall = new FakeRecall(Memory("memory-1", "role-a", "private fact", isEnabled: true))
        {
            OnQuery = () => enabled = false
        };
        var tool = Assert.Single(NativeMemoryTools.Create(recall, () => enabled));

        var result = await Invoke(tool, Scope(), new { query = "fact" });

        Assert.False(result.Success);
        Assert.Equal("MEMORY_UNAVAILABLE", result.ErrorCode);
        Assert.Equal(ToolExecutionState.NotExecuted, result.ExecutionState);
        Assert.Equal("{}", result.Payload.GetRawText());
        Assert.Equal(1, recall.CallCount);
        Assert.DoesNotContain("private fact", result.Payload.GetRawText());
    }

    [Fact]
    public async Task Search_returns_unavailable_if_disabled_after_projection_before_return()
    {
        var recall = new FakeRecall(Memory("memory-1", "role-a", "private fact", isEnabled: true));
        var enabledChecks = 0;
        var tool = Assert.Single(NativeMemoryTools.Create(recall, () => ++enabledChecks < 5));

        var result = await Invoke(tool, Scope(), new { query = "fact" });

        Assert.False(result.Success);
        Assert.Equal("MEMORY_UNAVAILABLE", result.ErrorCode);
        Assert.Equal(ToolExecutionState.NotExecuted, result.ExecutionState);
        Assert.Equal("{}", result.Payload.GetRawText());
        Assert.Equal(1, recall.CallCount);
        Assert.DoesNotContain("private fact", result.Payload.GetRawText());
    }

    [Fact]
    public async Task Search_enforces_requested_limits_and_rejects_values_above_caps()
    {
        var recall = new FakeRecall(Enumerable.Range(1, 10)
            .Select(index => Memory($"memory-{index}", "role-a", $"fact {index}", isEnabled: true)).ToArray());
        var tool = Tool(recall);

        var limited = await Invoke(tool, Scope(), new { query = "fact", maxItems = 2, maxChars = 6000 });

        Assert.True(limited.Success);
        Assert.Equal(2, ReadItems(limited).Count);
        Assert.Equal(2, recall.LastMaxItems);
        Assert.Equal(6000, recall.LastMaxChars);
        Assert.True(limited.Payload.GetProperty("truncated").GetBoolean());

        var tooManyItems = await Invoke(tool, Scope(), new { query = "fact", maxItems = 9 });
        var tooManyChars = await Invoke(tool, Scope(), new { query = "fact", maxChars = 6001 });
        var tooLongQuery = await Invoke(tool, Scope(), new { query = new string('q', 2001) });

        AssertInvalidArguments(tooManyItems);
        AssertInvalidArguments(tooManyChars);
        AssertInvalidArguments(tooLongQuery);
    }

    [Fact]
    public async Task Search_caps_total_text_by_unicode_scalar_and_keeps_serialized_output_bounded()
    {
        var recall = new FakeRecall(Enumerable.Range(1, 8)
            .Select(index => Memory($"memory-{index}", "role-a", new string('猫', 2000), isEnabled: true)).ToArray());
        var tool = Tool(recall);

        var result = await Invoke(tool, Scope(), new { query = "fact", maxItems = 8, maxChars = 6000 });

        Assert.True(result.Success);
        var items = ReadItems(result);
        Assert.Equal(3, items.Count);
        Assert.Equal(6000, items.Sum(item => item.Text.EnumerateRunes().Count()));
        Assert.True(result.Payload.GetProperty("truncated").GetBoolean());
        Assert.InRange(Encoding.UTF8.GetByteCount(result.Payload.GetRawText()), 1, 48 * 1024);
    }

    [Fact]
    public async Task Search_returns_not_executed_cancellation_without_querying_when_already_cancelled()
    {
        var recall = new FakeRecall(Memory("memory-1", "role-a", "private fact", isEnabled: true));
        var tool = Tool(recall);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await Invoke(tool, Scope(), new { query = "fact" }, cancellation.Token);

        Assert.False(result.Success);
        Assert.Equal("TOOL_CANCELLED", result.ErrorCode);
        Assert.Equal(ToolExecutionState.NotExecuted, result.ExecutionState);
        Assert.Equal("{}", result.Payload.GetRawText());
        Assert.Equal(0, recall.CallCount);
    }

    [Fact]
    public async Task Search_does_not_return_data_if_cancellation_arrives_during_owner_query()
    {
        using var cancellation = new CancellationTokenSource();
        var recall = new FakeRecall(Memory("memory-1", "role-a", "private fact", isEnabled: true))
        {
            OnQuery = cancellation.Cancel
        };
        var tool = Tool(recall);

        var result = await Invoke(tool, Scope(), new { query = "fact" }, cancellation.Token);

        Assert.False(result.Success);
        Assert.Equal("TOOL_CANCELLED", result.ErrorCode);
        Assert.Equal(ToolExecutionState.NotExecuted, result.ExecutionState);
        Assert.Equal("{}", result.Payload.GetRawText());
    }

    [Fact]
    public async Task Search_returns_a_safe_error_when_the_recall_owner_fails()
    {
        var recall = new FakeRecall(Memory("memory-1", "role-a", "private fact", isEnabled: true))
        {
            QueryFailure = new InvalidOperationException("private memory text")
        };
        var tool = Tool(recall);

        var result = await Invoke(tool, Scope(), new { query = "fact" });

        Assert.False(result.Success);
        Assert.Equal("MEMORY_SEARCH_FAILED", result.ErrorCode);
        Assert.Equal(ToolExecutionState.NotExecuted, result.ExecutionState);
        Assert.Equal("{}", result.Payload.GetRawText());
        Assert.DoesNotContain("private memory text", result.Payload.GetRawText());
    }

    [Fact]
    public async Task Search_rejects_a_negative_snapshot_revision_with_a_safe_error()
    {
        var recall = new FakeRecall(Memory("memory-1", "role-a", "private fact", isEnabled: true))
        {
            Revision = -1
        };
        var tool = Tool(recall);

        var result = await Invoke(tool, Scope(), new { query = "fact" });

        Assert.False(result.Success);
        Assert.Equal("MEMORY_SEARCH_FAILED", result.ErrorCode);
        Assert.Equal(ToolExecutionState.NotExecuted, result.ExecutionState);
        Assert.Equal("{}", result.Payload.GetRawText());
    }

    private static IToolProvider Tool(IMemoryRecall recall) =>
        Assert.Single(NativeMemoryTools.Create(recall));

    private static ToolScope Scope() => new("conversation-1", "role-a", "project-a");

    private static async Task<ToolResult> Invoke(IToolProvider tool, ToolScope scope, object arguments,
        CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.SerializeToElement(arguments);
        return await tool.InvokeAsync(new ToolInvocation(scope, json), cancellationToken);
    }

    private static IReadOnlyList<(string Id, int Version, string Text)> ReadItems(ToolResult result) =>
        result.Payload.GetProperty("items").EnumerateArray()
            .Select(item => (item.GetProperty("memoryId").GetString()!, item.GetProperty("version").GetInt32(),
                item.GetProperty("text").GetString()!))
            .ToArray();

    private static StoredMemory Memory(string id, string roleId, string text, bool isEnabled, string? projectId = null,
        int version = 1) =>
        new(id, roleId, text, isEnabled, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
            projectId: projectId, version: version);

    private static void AssertInvalidArguments(ToolResult result)
    {
        Assert.False(result.Success);
        Assert.Equal("TOOL_INVALID_ARGUMENTS", result.ErrorCode);
        Assert.Equal(ToolExecutionState.NotExecuted, result.ExecutionState);
    }

    private sealed class FakeRecall(params StoredMemory[] items) : IMemoryRecall
    {
        public MemoryScope? LastScope { get; private set; }
        public int LastMaxItems { get; private set; }
        public int LastMaxChars { get; private set; }
        public int CallCount { get; private set; }
        public long Revision { get; init; } = 1;
        public Action? OnQuery { get; init; }
        public Exception? QueryFailure { get; init; }

        public MemoryRecallSnapshot Query(MemoryScope scope, string query, int maxItems = 8, int maxChars = 6000)
        {
            CallCount++;
            LastScope = scope;
            LastMaxItems = maxItems;
            LastMaxChars = maxChars;
            OnQuery?.Invoke();
            if (QueryFailure is not null) throw QueryFailure;
            return new MemoryRecallSnapshot(Revision, items);
        }
    }
}
