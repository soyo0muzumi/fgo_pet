using System.Security.Cryptography;
using System.Text;
using FgoPet.AgentRuntime;
using FgoPet.AgentRuntime.Pipes;
using FgoPet.CodexAdapter.Hooks;
using FgoPet.CodexAdapter.Mcp;
using FgoPet.CodexAdapter.Relay;
using FgoPet.CodexAdapter.AppServer;
using System.Text.Json;

namespace FgoPet.CodexAdapter;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        try
        {
            var mode = args.ElementAtOrDefault(0) ?? "mcp";
            if (mode is "--version" or "describe")
            {
                Console.WriteLine(JsonSerializer.Serialize(new { name = "fgo-pet-codex-adapter", version = "1.0.0", protocol_version = "1" }));
                return 0;
            }
            if (mode is not "mcp" and not "hook" and not "worker" and not "target") return 2;
            var defaults = RelayRuntimeOptions.ForCurrentUser();
            var options = new RelayRuntimeOptions(
                Environment.GetEnvironmentVariable("FGO_PET_PIPE_SUFFIX") ?? defaults.PipeSuffix,
                Environment.GetEnvironmentVariable("FGO_PET_STATE_ROOT") ?? defaults.StateRoot,
                defaults.RelayExecutablePath, defaults.ConnectTimeout, defaults.StartupTimeout);
            if (mode == "target")
            {
                var catalog = new CodexTargetCatalog(options.StateRoot);
                if (args.ElementAtOrDefault(1) == "list") Console.WriteLine(JsonSerializer.Serialize(catalog.List()));
                else if (args.ElementAtOrDefault(1) == "add" && args.Length >= 3)
                    Console.WriteLine(JsonSerializer.Serialize(catalog.Add(args[2], args.ElementAtOrDefault(3) is { } label && label != "--read-only" ? label : null,
                        args.Contains("--read-only", StringComparer.Ordinal))));
                else return 2;
                return 0;
            }
            var bootstrap = new RelayProcessBootstrapper(new DefaultRelayProbe(), new DefaultRelayProcessLauncher(), new DefaultRuntimeDelay());
            var connector = new CodexRelayConnector(new AdapterIdentityStore(options.StateRoot),
                new CodexRelaySession(RelayPipeNames.ForCurrentUser(options).Adapter, options.ConnectTimeout),
                token => bootstrap.EnsureReadyAsync(options, token));
            var taskId = Environment.GetEnvironmentVariable("FGO_PET_AGENT_TASK") ?? "mcp-" + Guid.NewGuid().ToString("N");
            if (mode == "worker")
                await RunWorkerAsync(connector, options, cancellation.Token).ConfigureAwait(false);
            else if (mode == "hook")
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
                if (kind is null) return 2;
                var sequence = long.TryParse(Environment.GetEnvironmentVariable("FGO_PET_AGENT_SEQUENCE"), out var value) && value > 0 ? value : 1;
                await connector.SendEventAsync(CodexHookMapper.Map(new CodexHookObservation(taskId, sequence, kind.Value),
                    "codex", connector.SourceInstanceId), cancellation.Token).ConfigureAwait(false);
            }
            else
            {
                await RunMcpAsync(Console.OpenStandardInput(), Console.Out, connector, taskId, cancellation.Token,
                    Environment.GetEnvironmentVariable("FGO_PET_EXECUTOR_CHILD") == "1" ? null
                        : token => RunWorkerAsync(connector, options, token)).ConfigureAwait(false);
            }
            return 0;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return 0; }
        catch (InvalidDataException error) when (error.Message == "dispatch_journal_full")
        {
            Console.Error.WriteLine("dispatch_journal_full");
            return 1;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or CryptographicException
            or ArgumentException or DecoderFallbackException or JsonException or InvalidOperationException
            or System.ComponentModel.Win32Exception)
        {
            // Stdout is reserved for MCP. Never echo exception text or protected state.
            Console.Error.WriteLine("adapter_start_or_connection_failed");
            return 1;
        }
    }

    public static async Task RunMcpAsync(Stream input, TextWriter output, ICodexRelayConnector connector,
        string taskId, CancellationToken cancellationToken = default, Func<CancellationToken, Task>? executionWorker = null)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var monitoring = MonitorConnectionAsync(connector, lifetime.Token);
        var executing = executionWorker?.Invoke(lifetime.Token) ?? Task.CompletedTask;
        var executionWatch = ObserveExecutionAsync(executing, lifetime);
        var reader = new JsonLineFrameReader(input);
        var server = new CodexMcpServer(connector, taskId);
        try
        {
            while (await reader.ReadAsync(lifetime.Token).ConfigureAwait(false) is { } line)
            {
                var response = await server.HandleAsync(line, lifetime.Token).ConfigureAwait(false);
                if (response.Length == 0) continue;
                await output.WriteLineAsync(response.AsMemory(), lifetime.Token).ConfigureAwait(false);
                await output.FlushAsync(lifetime.Token).ConfigureAwait(false);
            }
        }
        finally
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
            try { await monitoring.ConfigureAwait(false); }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            try { await executionWatch.ConfigureAwait(false); }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        }
    }

    private static async Task ObserveExecutionAsync(Task execution, CancellationTokenSource lifetime)
    {
        try
        {
            await execution.ConfigureAwait(false);
        }
        catch
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task RunWorkerAsync(ICodexRelayConnector connector, RelayRuntimeOptions options, CancellationToken cancellationToken)
    {
        using var owner = RelaySingleInstance.TryAcquire(RelayPipeNames.ForCurrentUser(options).Mutex + ".Executor");
        if (!owner.IsOwner) return;
        var diagnostics = new CodexWorkerDiagnostics(options.StateRoot);
        var worker = new CodexDispatchWorker(connector,
            new CodexTaskExecutor(new CodexTargetCatalog(options.StateRoot), diagnostics: diagnostics),
            options.StateRoot, diagnostics: diagnostics);
        await worker.RunAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task MonitorConnectionAsync(ICodexRelayConnector connector, CancellationToken cancellationToken)
    {
        while (true)
        {
            var result = await connector.EnsureAuthenticatedAsync(cancellationToken).ConfigureAwait(false);
            if (result.Status is AdapterConnectionStatus.Revoked or AdapterConnectionStatus.Rejected or AdapterConnectionStatus.VersionMismatch)
                return;
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        }
    }
}
