using System.IO.Pipes;
using System.Text.Json;
using FgoPet.CodexAdapter.AppServer;
using Xunit;

namespace FgoPet.CodexAdapter.Tests;

public sealed class JsonRpcLineClientTests
{
    [Fact]
    public async Task Initialization_streamed_notifications_and_approval_denial_share_one_reader()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var name = "fgo-rpc-" + Guid.NewGuid().ToString("N");
        await using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await Task.WhenAll(server.WaitForConnectionAsync(timeout.Token), client.ConnectAsync(timeout.Token));
        var responding = Task.Run(async () =>
        {
            using var reader = new StreamReader(server, leaveOpen: true);
            using var writer = new StreamWriter(server, new System.Text.UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            using var init = JsonDocument.Parse((await reader.ReadLineAsync(timeout.Token))!);
            Assert.Equal("initialize", init.RootElement.GetProperty("method").GetString());
            await writer.WriteLineAsync(JsonSerializer.Serialize(new { id = init.RootElement.GetProperty("id").GetInt64(), result = new { } }));
            using var initialized = JsonDocument.Parse((await reader.ReadLineAsync(timeout.Token))!);
            Assert.Equal("initialized", initialized.RootElement.GetProperty("method").GetString());
            await writer.WriteLineAsync("{\"method\":\"item/started\",\"params\":{\"threadId\":\"thread-1\"}}");
            await writer.WriteLineAsync("{\"id\":9,\"method\":\"item/commandExecution/requestApproval\",\"params\":{}}");
            using var decision = JsonDocument.Parse((await reader.ReadLineAsync(timeout.Token))!);
            Assert.Equal("cancel", decision.RootElement.GetProperty("result").GetProperty("decision").GetString());
        }, timeout.Token);
        await using var rpc = new JsonRpcLineClient(client, client);
        await rpc.InitializeAsync(timeout.Token);
        Assert.Equal("item/started", (await rpc.ReadNotificationAsync(timeout.Token)).GetProperty("method").GetString());
        Assert.Equal("fgo/approvalDenied", (await rpc.ReadNotificationAsync(timeout.Token)).GetProperty("method").GetString());
        await responding;
    }

    [Fact]
    public async Task Closed_stream_rejects_new_requests_without_waiting_for_timeout()
    {
        await using var rpc = new JsonRpcLineClient(new MemoryStream(), new MemoryStream());
        await Assert.ThrowsAsync<EndOfStreamException>(() => rpc.CallAsync("thread/start", new { }).WaitAsync(TimeSpan.FromSeconds(1)));
    }
}
