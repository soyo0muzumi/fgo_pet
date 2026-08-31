using System.IO.Pipes;
using System.Text;
using FgoPet.AgentProtocol;
using FgoPet.AgentProtocol.Messages;
using FgoPet.AgentProtocol.Validation;
using FgoPet.CodexAdapter.Relay;
using Xunit;

namespace FgoPet.CodexAdapter.Tests;

public sealed class CodexRelaySessionTests
{
    [Fact]
    public async Task Session_returns_a_revoked_authentication_without_sending_the_operation()
    {
        var name = "adapter-session-" + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var serving = Task.Run(async () =>
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await server.WaitForConnectionAsync(deadline.Token);
            using var reader = new StreamReader(server, Encoding.UTF8, false, 1024, true);
            var auth = ProtocolEnvelope.Parse((await reader.ReadLineAsync(deadline.Token))!);
            Assert.Equal("authenticate", auth.MessageType);
            var reply = ProtocolEnvelope.Create(auth.MessageId, "authenticate", new { result = "revoked" });
            await server.WriteAsync(Encoding.UTF8.GetBytes(reply.ToJson() + "\n"), deadline.Token);
            Assert.Null(await reader.ReadLineAsync(deadline.Token));
        });
        var session = new CodexRelaySession(name, TimeSpan.FromSeconds(2));

        var result = await session.SendAsync(ProtocolEnvelope.Create("operation", "status_check", new { include_dispatches = true }),
            new AuthenticateRequest("codex", "source-1", Convert.ToBase64String(new byte[32])));

        Assert.Equal("revoked", result.Payload.GetProperty("result").GetString());
        await serving;
    }

    [Fact]
    public async Task Session_rejects_a_response_for_another_request()
    {
        var name = "adapter-correlation-" + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var serving = Task.Run(async () =>
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await server.WaitForConnectionAsync(deadline.Token);
            using var reader = new StreamReader(server, Encoding.UTF8, false, 1024, true);
            Assert.NotNull(await reader.ReadLineAsync(deadline.Token));
            var reply = ProtocolEnvelope.Create("wrong-id", "registration_status",
                new RegistrationStatusResponse("pending", "request-1", "source-1", null, null));
            await server.WriteAsync(Encoding.UTF8.GetBytes(reply.ToJson() + "\n"), deadline.Token);
        });
        var session = new CodexRelaySession(name, TimeSpan.FromSeconds(2));
        var request = ProtocolEnvelope.Create("request-id", "registration_request",
            new RegistrationRequestMessage("codex", "Codex", "source-1", "1", "1", new string('a', 64)));

        await Assert.ThrowsAsync<AgentProtocolValidationException>(() => session.SendAsync(request));
        await serving;
    }
}
