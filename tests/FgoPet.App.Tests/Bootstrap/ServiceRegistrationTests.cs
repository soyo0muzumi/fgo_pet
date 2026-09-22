using FgoPet.App.Bootstrap;
using FgoPet.Character.Settings;
using FgoPet.Core.Agents;
using FgoPet.Dialogue.Settings;
using FgoPet.Infrastructure.Agents;
using FgoPet.Memory.Settings;
using FgoPet.SettingsHost;
using FgoPet.Speech.Settings;
using FgoPet.UiFoundation.Theming;
using FgoPet.Work.Execution.Settings;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FgoPet.App.Tests.Bootstrap;

public sealed class ServiceRegistrationTests
{
    [Fact]
    public void Registers_the_agent_gateway_and_projection_services()
    {
        var services = new ServiceCollection();
        services.AddFgoPet(Array.Empty<string>());

        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IAgentGateway));
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(AgentEventProjector));
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(AgentReconnectService));
    }

    [Fact]
    public void Settings_section_ports_and_document_facade_alias_one_coordinator_singleton()
    {
        using var provider = new ServiceCollection().AddFgoPet([]).BuildServiceProvider();
        var coordinator = provider.GetRequiredService<ApplicationSettingsCoordinator>();

        Assert.Same(coordinator, provider.GetRequiredService<IApplicationSettingsDocument>());
        Assert.Same(coordinator, provider.GetRequiredService<ICharacterSettingsStore>());
        Assert.Same(coordinator, provider.GetRequiredService<IDialogueSettingsStore>());
        Assert.Same(coordinator, provider.GetRequiredService<IMemorySettingsStore>());
        Assert.Same(coordinator, provider.GetRequiredService<IWorkExecutionSettingsStore>());
        Assert.Same(coordinator, provider.GetRequiredService<ISpeechSettingsStore>());
        Assert.Same(coordinator, provider.GetRequiredService<IThemeSettingsStore>());
    }
}
