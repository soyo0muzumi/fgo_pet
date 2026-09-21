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
    private readonly Func<Action, Task> _dispatchToUi;
    private int _polling;

    public AgentReconnectService(
        IAgentGateway gateway,
        IAgentRepository agents,
        AgentEventProjector projector,
        Func<Action, Task>? dispatchToUi = null)
    {
        _gateway = gateway;
        _agents = agents;
        _projector = projector;
        _dispatchToUi = dispatchToUi ?? (action =>
        {
            action();
            return Task.CompletedTask;
        });
    }

    public async Task<AgentReconnectResult> ReconnectAsync(CancellationToken cancellationToken = default)
    {
        var known = _agents.ListNonTerminalExecutions();
        await _dispatchToUi(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var execution in known)
            {
                _projector.Restore(execution);
            }
        }).ConfigureAwait(false);
        var status = await _gateway.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        if (!status.IsConnected || !_gateway.IsConnected)
        {
            return new AgentReconnectResult(false, known.Count, 0, status);
        }

        var events = await _gateway.QueryKnownStatesAsync(known, cancellationToken).ConfigureAwait(false);
        var applied = await ApplyEventsAsync(events, cancellationToken).ConfigureAwait(false);

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
            return await ApplyEventsAsync(events, cancellationToken).ConfigureAwait(false);
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

    private async Task<int> ApplyEventsAsync(IReadOnlyList<AgentEvent> events, CancellationToken cancellationToken)
    {
        var applied = 0;
        await _dispatchToUi(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var agentEvent in events)
            {
                if (_projector.Apply(agentEvent) == AgentProjectionApplyResult.Applied) applied++;
            }
        }).ConfigureAwait(false);
        // Delivery remains pending until the UI projection and SQLite write succeed.
        if (_gateway is AgentRelayClient relay)
        {
            await relay.AcknowledgeEventsAsync(events, cancellationToken).ConfigureAwait(false);
        }
        return applied;
    }
}
