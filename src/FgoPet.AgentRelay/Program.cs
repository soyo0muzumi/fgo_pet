namespace FgoPet.AgentRelay;

public static class Program
{
    public static async Task Main(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        var adapterPipe = args.ElementAtOrDefault(0) ?? RelayPipeNames.AdapterForCurrentUser();
        var appPipe = args.ElementAtOrDefault(1) ?? RelayPipeNames.AppForCurrentUser();
        var credential = args.ElementAtOrDefault(2) ?? string.Empty;
        await new RelayHost().RunAsync(adapterPipe, appPipe, credential, cancellation.Token).ConfigureAwait(false);
    }
}
