namespace FgoPet.AgentRelay.Storage;

public interface IRelayStateStore
{
    RelayState Load();
    void Save(RelayState state);
}

public sealed class InMemoryRelayStateStore : IRelayStateStore
{
    private RelayState _state = RelayState.Empty;
    public RelayState Load() => _state;
    public void Save(RelayState state) => _state = state;
}
