using FgoPet.AgentRuntime;
using FgoPet.AgentRelay.Storage;

namespace FgoPet.AgentRelay;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            await RunAsync(args).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException) { return 0; }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException
            or System.Security.Cryptography.CryptographicException or ArgumentException or InvalidOperationException)
        {
            Console.Error.WriteLine("relay_start_or_state_failed");
            return 1;
        }
    }

    private static async Task RunAsync(string[] args)
    {
        var defaults = RelayRuntimeOptions.ForCurrentUser();
        var suffix = defaults.PipeSuffix;
        var stateRoot = defaults.StateRoot;
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--pipe-suffix":
                    suffix = NextValue(args, ref index, "--pipe-suffix");
                    break;
                case "--state-root":
                    stateRoot = NextValue(args, ref index, "--state-root");
                    break;
                default:
                    throw new ArgumentException($"Unknown relay argument '{args[index]}'.");
            }
        }

        var options = new RelayRuntimeOptions(
            suffix,
            stateRoot,
            defaults.RelayExecutablePath,
            defaults.ConnectTimeout,
            defaults.StartupTimeout);
        var names = RelayPipeNames.ForCurrentUser(options);
        using var ownership = RelaySingleInstance.TryAcquire(names.Mutex);
        if (!ownership.IsOwner) return;

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        var stateStore = new ProtectedRelayStateStore(Path.Combine(options.StateRoot, "AgentRelay"));
        await new RelayHost(stateStore).RunAsync(names.Adapter, names.App, cancellation.Token).ConfigureAwait(false);
    }

    private static string NextValue(string[] args, ref int index, string option)
    {
        if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
            throw new ArgumentException($"{option} requires a value.");
        return args[index];
    }
}
