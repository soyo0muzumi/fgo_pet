using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FgoPet.Extensibility;
using FgoPet.Infrastructure.Dialogue;
using FgoPet.Kernel.Agent;
using FgoPet.Platform.Windows.Secrets;
using Xunit;

namespace FgoPet.Infrastructure.Tests.Dialogue;

public sealed class SqliteAgentRunStoreUnicodeTests
{
    [Theory]
    [InlineData("cjk")]
    [InlineData("emoji")]
    [InlineData("ascii-limit")]
    [InlineData("whitespace")]
    public async Task Protected_payload_preserves_original_bounded_json_after_reopen(string kind)
    {
        using var fixture = new SqliteAgentRunStoreTests.Fixture();
        var initial = fixture.Initial();
        Assert.True(await fixture.Store.TryCreateAsync(initial, fixture.Events(initial, 1), default));
        var raw = kind switch
        {
            "cjk" => "{\"value\":\"" + new string('中', 12000) + "\"}",
            "emoji" => "{\"value\":\"" + string.Concat(Enumerable.Repeat("😀", 6000)) + "\"}",
            "ascii-limit" => "{\"value\":\"" + new string('x', 65524) + "\"}",
            _ => "{ \"value\": \"quote\\\"slash\\\\newline\\n\", \"list\": [1, 2] }",
        };
        Assert.InRange(Encoding.UTF8.GetByteCount(raw), 1, 65536);
        var next = WithPayload(initial, raw);
        Assert.True(await fixture.Store.TryCommitAsync(next, 0, fixture.Events(next, 2), default));
        var reopened = new SqliteAgentRunStore(fixture.Database, new WindowsStateProtector());
        var loaded = await reopened.LoadAsync("run", default);
        Assert.Equal(raw, loaded!.Calls[0].Result!.Payload.GetRawText());
        Assert.Equal(Encoding.UTF8.GetBytes(raw), Encoding.UTF8.GetBytes(loaded.Calls[0].Result!.Payload.GetRawText()));
        Assert.Equal(2, fixture.Count("agent_run_events"));
    }

    [Fact]
    public async Task Legacy_escaped_payload_remains_readable_without_rewriting_the_record()
    {
        using var fixture = new SqliteAgentRunStoreTests.Fixture();
        var initial = fixture.Initial();
        Assert.True(await fixture.Store.TryCreateAsync(initial, fixture.Events(initial, 1), default));
        var escaped = JsonSerializer.Serialize(new { value = "中文😀" });
        var next = WithPayload(initial, escaped);
        Assert.True(await fixture.Store.TryCommitAsync(next, 0, fixture.Events(next, 2), default));
        // Reproduce the previous serializer's protected checkpoint representation.
        var legacy = JsonSerializer.SerializeToUtf8Bytes(next);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("run")));
        var encrypted = new WindowsStateProtector().Protect(legacy, "native-run." + key.ToLowerInvariant() + ".v1");
        using (var connection = fixture.Database.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE agent_runs SET checkpoint=$blob WHERE run_key=$key";
            command.Parameters.AddWithValue("$blob", encrypted);
            command.Parameters.AddWithValue("$key", key);
            Assert.Equal(1, command.ExecuteNonQuery());
        }
        var reopened = new SqliteAgentRunStore(fixture.Database, new WindowsStateProtector());
        var loaded = await reopened.LoadAsync("run", default);
        Assert.Equal("中文😀", loaded!.Calls[0].Result!.Payload.GetProperty("value").GetString());
        Assert.Equal(escaped, loaded.Calls[0].Result!.Payload.GetRawText());
        Assert.Equal(2, fixture.Count("agent_run_events"));
    }

    [Fact]
    public async Task Payload_over_64kib_is_rejected_without_partial_revision_or_journal()
    {
        using var fixture = new SqliteAgentRunStoreTests.Fixture();
        var initial = fixture.Initial();
        Assert.True(await fixture.Store.TryCreateAsync(initial, fixture.Events(initial, 1), default));
        var raw = "{\"value\":\"" + new string('x', 65525) + "\"}";
        Assert.Equal(65537, Encoding.UTF8.GetByteCount(raw));
        var next = WithPayload(initial, raw);
        var error = await Assert.ThrowsAsync<AgentStateException>(() => fixture.Store.TryCommitAsync(next, 0, fixture.Events(next, 2), default).AsTask());
        Assert.Equal("RUN_STATE_UNREADABLE", error.Code);
        Assert.Equal(0, (await fixture.Store.LoadAsync("run", default))!.Snapshot.Revision);
        Assert.Equal(1, fixture.Count("agent_run_events"));
    }

    [Fact]
    public async Task Payload_exceeding_checkpoint_depth_is_rejected_without_partial_revision_or_journal()
    {
        using var fixture = new SqliteAgentRunStoreTests.Fixture();
        var initial = fixture.Initial();
        Assert.True(await fixture.Store.TryCreateAsync(initial, fixture.Events(initial, 1), default));
        // Valid as a standalone JsonElement, but its enclosing checkpoint exceeds MaxDepth=48.
        var next = WithPayload(initial, new string('[', 48) + "0" + new string(']', 48));
        var error = await Assert.ThrowsAsync<AgentStateException>(() => fixture.Store.TryCommitAsync(next, 0, fixture.Events(next, 2), default).AsTask());
        Assert.Equal("RUN_STATE_UNREADABLE", error.Code);
        Assert.Equal(0, (await fixture.Store.LoadAsync("run", default))!.Snapshot.Revision);
        Assert.Equal(1, fixture.Count("agent_run_events"));
    }

    private static AgentRunCheckpoint WithPayload(AgentRunCheckpoint initial, string raw)
    {
        using var document = JsonDocument.Parse(raw);
        return initial with
        {
            Snapshot = initial.Snapshot with { Revision = 1, Status = AgentRunStatus.Running, ToolCalls = 1, StepNumber = 1 },
            Calls = [new(new("call", "fixture.read", "{}"), AgentCallStatus.Completed, true, ToolEffect.ReadOnly,
                new ToolResult(true, document.RootElement.Clone()))],
            NextCallIndex = 1,
            JournalSequence = 2,
        };
    }
}
