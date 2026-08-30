using FgoPet.CodexAdapter.Mcp;
using FgoPet.CodexAdapter.Hooks;
using FgoPet.CodexAdapter.Relay;

namespace FgoPet.CodexAdapter;

public static class Program
{
    public static async Task Main(string[] args)
    {
        var mode = args.ElementAtOrDefault(0) ?? "mcp";
        var pipeName = Environment.GetEnvironmentVariable("FGO_PET_ADAPTER_PIPE")
            ?? $"fgo-pet-agent-adapter-{Environment.UserName}-v1";
        var credential = Environment.GetEnvironmentVariable("FGO_PET_ADAPTER_CREDENTIAL") ?? string.Empty;
        var sourceInstance = Environment.GetEnvironmentVariable("FGO_PET_AGENT_INSTANCE") ?? "unpaired";
        var taskId = Environment.GetEnvironmentVariable("FGO_PET_AGENT_TASK") ?? "mcp-session";
        var sequence = long.TryParse(Environment.GetEnvironmentVariable("FGO_PET_AGENT_SEQUENCE"), out var configuredSequence)
            && configuredSequence > 0
            ? configuredSequence
            : 1;
        var relay = new CodexRelaySession(pipeName, credential);
        if (string.Equals(mode, "hook", StringComparison.OrdinalIgnoreCase))
        {
            var kind = args.ElementAtOrDefault(1) switch
            {
                "started" => CodexHookKind.Started,
                "resumed" => CodexHookKind.Resumed,
                "attention" => CodexHookKind.Attention,
                "failed" => CodexHookKind.Failed,
                "cancelled" => CodexHookKind.Cancelled,
                _ => (CodexHookKind?)null,
            };
            if (kind is not null)
            {
                await relay.SendEventAsync(CodexHookMapper.Map(
                    new CodexHookObservation(taskId, sequence, kind.Value),
                    "codex",
                    sourceInstance)).ConfigureAwait(false);
            }

            return;
        }

        if (!string.Equals(mode, "mcp", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var server = new CodexMcpServer(relay, "codex", sourceInstance, taskId);
        while (await Console.In.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            Console.WriteLine(await server.HandleAsync(line).ConfigureAwait(false));
        }
    }
}
