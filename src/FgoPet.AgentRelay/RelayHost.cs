using FgoPet.AgentRelay.Pipes;
using FgoPet.AgentRelay.Registration;
using FgoPet.AgentRelay.Routing;
using FgoPet.AgentRelay.Storage;

namespace FgoPet.AgentRelay;

public sealed class RelayHost
{
    public RelayHost()
    {
        Store = new RelayStore();
        Registration = new RegistrationService(Store);
        Router = new RelayRouter(Store, Registration);
    }

    public RelayStore Store { get; }
    public RegistrationService Registration { get; }
    public RelayRouter Router { get; }

    public Task RunAsync(string adapterPipeName, string appPipeName, string adapterCredential, CancellationToken cancellationToken)
    {
        var adapter = new AdapterPipeServer(Router, adapterPipeName, adapterCredential);
        var app = new AppPipeServer(Router, appPipeName, adapterCredential);
        return Task.WhenAll(adapter.RunAsync(cancellationToken), app.RunAsync(cancellationToken));
    }
}

public static class RelayPipeNames
{
    public static string AdapterForCurrentUser(string suffix = "v1") => $"fgo-pet-agent-adapter-{Environment.UserName}-{suffix}";
    public static string AppForCurrentUser(string suffix = "v1") => $"fgo-pet-agent-app-{Environment.UserName}-{suffix}";
}
