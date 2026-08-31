using FgoPet.AgentProtocol;
using FgoPet.Core.Agents;

namespace FgoPet.Infrastructure.Agents;

public sealed record AgentReconnectResult(
    bool Connected,
    int KnownExecutionCount,
    int AppliedEventCount,
    AgentGatewayStatus Status);

public sealed class AgentReconnectService
{
    private readonly IAgentGateway _gateway;
    private readonly IAgentRepository _agents;
    private readonly AgentEventProjector _projector;
    private int _polling;

    public AgentReconnectService(IAgentGateway gateway, IAgentRepository agents, AgentEventProjector projector)
    {
        _gateway = gateway;
        _agents = agents;
        _projector = projector;
    }

    public async Task<AgentReconnectResult> ReconnectAsync(CancellationToken cancellationToken = default)
    {
        var known = _agents.ListNonTerminalExecutions();
        var status = await _gateway.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        if (!status.IsConnected || !_gateway.IsConnected)
        {
            return new AgentReconnectResult(false, known.Count, 0, status);
        }

        var events = await _gateway.QueryKnownStatesAsync(known, cancellationToken).ConfigureAwait(false);
        var applied = 0;
        foreach (var agentEvent in events)
        {
            if (_projector.Apply(agentEvent) == AgentProjectionApplyResult.Applied) applied++;
        }

        return new AgentReconnectResult(true, known.Count, applied, status);
    }

    public async Task<int> PollAsync(CancellationToken cancellationToken = default)
    {
        if (_gateway is not AgentRelayClient relay || Interlocked.Exchange(ref _polling, 1) != 0)
        {
            return 0;
        }

        try
        {
            var events = await relay.PollPendingEventsAsync(cancellationToken).ConfigureAwait(false);
            var applied = 0;
            foreach (var agentEvent in events)
            {
                if (_projector.Apply(agentEvent) == AgentProjectionApplyResult.Applied) applied++;
            }

            return applied;
        }
        catch (IOException) when (!cancellationToken.IsCancellationRequested)
        {
            return 0;
        }
        catch (TimeoutException) when (!cancellationToken.IsCancellationRequested)
        {
            return 0;
        }
        catch (AgentProtocolValidationException) when (!cancellationToken.IsCancellationRequested)
        {
            return 0;
        }
        finally
        {
            Volatile.Write(ref _polling, 0);
        }
    }
}
