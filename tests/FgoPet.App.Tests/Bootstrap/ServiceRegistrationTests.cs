using FgoPet.App.Bootstrap;
using FgoPet.Core.Agents;
using FgoPet.Infrastructure.Agents;
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
}
