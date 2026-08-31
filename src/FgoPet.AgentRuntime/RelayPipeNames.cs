using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace FgoPet.AgentRuntime;

public sealed record RelayPipeSet(string Adapter, string App, string Mutex);

public static class RelayPipeNames
{
    public static RelayPipeSet ForCurrentUser(RelayRuntimeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        RelayRuntimeOptions.Validate(
            options.PipeSuffix,
            options.StateRoot,
            options.RelayExecutablePath,
            options.ConnectTimeout,
            options.StartupTimeout);

        var user = Environment.UserName;
        var adapter = $"fgo-pet-agent-adapter-{user}-{options.PipeSuffix}";
        var app = $"fgo-pet-agent-app-{user}-{options.PipeSuffix}";
        var sid = WindowsIdentity.GetCurrent().User?.Value
            ?? throw new InvalidOperationException("The current Windows identity has no SID.");
        var sidHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sid))).ToLowerInvariant();
        var mutex = $"Local\\FgoPet.AgentRelay.{sidHash}.{options.PipeSuffix}";
        return new RelayPipeSet(adapter, app, mutex);
    }
}
