using System.Text.Json;
using FgoPet.AgentProtocol.Messages;
using FgoPet.CodexAdapter.AppServer;
using Xunit;

namespace FgoPet.CodexAdapter.Tests;

public sealed class CodexAppServerClientTests
{
    [Fact]
    public async Task Dispatch_starts_a_thread_then_starts_a_turn_with_confirmed_content()
    {
        var rpc = new FakeRpc();
        var client = new CodexAppServerClient(rpc, new DictionaryTargetResolver(new Dictionary<string, string> { ["target-1"] = "C:\\authorized" }));
        var request = new DispatchTaskRequest("dispatch-1", "todo-1", "Ship it", "Run the approved tests", "high", null, "target-1");

        var result = await client.StartTaskAsync(request);

        Assert.Equal("thread-1", result.TaskId);
        Assert.Equal(new[] { "thread/start", "turn/start" }, rpc.Methods);
        Assert.Equal("thread-1", rpc.Parameters[1].GetProperty("threadId").GetString());
        var input = rpc.Parameters[1].GetProperty("input")[0];
        Assert.Equal("text", input.GetProperty("type").GetString());
        Assert.Contains("Ship it", input.GetProperty("text").GetString(), StringComparison.Ordinal);
        Assert.Contains("Run the approved tests", input.GetProperty("text").GetString(), StringComparison.Ordinal);
        Assert.Equal("on-request", rpc.Parameters[0].GetProperty("approvalPolicy").GetString());
        Assert.Equal("workspace-write", rpc.Parameters[0].GetProperty("sandbox").GetString());
    }

    private sealed class DictionaryTargetResolver(IReadOnlyDictionary<string, string> targets) : ICodexTargetResolver
    {
        public string Resolve(string targetId) => targets.TryGetValue(targetId, out var value) ? value : throw new UnauthorizedAccessException();
    }

    private sealed class FakeRpc : ICodexAppServerRpc
    {
        public List<string> Methods { get; } = new();
        public List<JsonElement> Parameters { get; } = new();

        public Task<JsonElement> CallAsync(string method, object parameters, CancellationToken cancellationToken = default)
        {
            Methods.Add(method);
            Parameters.Add(JsonSerializer.SerializeToElement(parameters));
            return Task.FromResult(method == "thread/start"
                ? JsonSerializer.SerializeToElement(new { thread = new { id = "thread-1" } })
                : JsonSerializer.SerializeToElement(new { turn = new { id = "turn-1", status = "inProgress" } }));
        }
    }
}
