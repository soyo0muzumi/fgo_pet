using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using FgoPet.AgentProtocol;
using FgoPet.AgentProtocol.Messages;
using FgoPet.AgentProtocol.Validation;
using FgoPet.AgentRuntime.Security;
using FgoPet.AgentRuntime.Storage;

namespace FgoPet.CodexAdapter.Relay;

/// <summary>Protects the shared MCP/hook identity and rejects stale cross-process writes.</summary>
public sealed class AdapterIdentityStore : IAdapterIdentityStore
{
    private readonly AtomicProtectedJsonStore<AdapterIdentityState> _store;
    private readonly string _mutexName;

    public AdapterIdentityStore(string stateRoot, ISecretProtector? protector = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateRoot);
        var path = Path.GetFullPath(Path.Combine(stateRoot, "CodexAdapter", "adapter-state.v1.json"));
        _store = new AtomicProtectedJsonStore<AdapterIdentityState>(path, protector ?? new DpapiSecretProtector());
        using var identity = WindowsIdentity.GetCurrent();
        var key = (identity.User?.Value ?? throw new InvalidOperationException("Missing Windows user identity.")) + "|" + path.ToUpperInvariant();
        _mutexName = "Local\\FgoPet.AdapterState." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
    }

    public AdapterIdentityState LoadOrCreate() => Locked(LoadCore);

    public bool TrySave(AdapterIdentityState expected, AdapterIdentityState updated)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(updated);
        if (!IsValid(updated) || updated.SourceInstanceId != expected.SourceInstanceId)
            throw new ArgumentException("Invalid adapter identity update.", nameof(updated));
        return Locked(() =>
        {
            if (LoadCore() != expected) return false;
            _store.Save(updated);
            return true;
        });
    }

    private AdapterIdentityState LoadCore()
    {
        var state = _store.Load(validate: IsValid);
        if (state is not null) return state;
        state = AdapterIdentityState.Create();
        _store.Save(state);
        return state;
    }

    private T Locked<T>(Func<T> action)
    {
        using var mutex = new Mutex(false, _mutexName);
        var owns = false;
        try
        {
            try { owns = mutex.WaitOne(TimeSpan.FromSeconds(5)); }
            catch (AbandonedMutexException) { owns = true; }
            if (!owns) throw new IOException("Adapter state is busy.");
            return action();
        }
        finally
        {
            // This synchronous scope acquires and releases on the same thread.
            if (owns) mutex.ReleaseMutex();
        }
    }

    private static bool IsValid(AdapterIdentityState state)
    {
        if (state.SchemaVersion != 1) return false;
        try
        {
            AgentProtocolValidator.Validate(ProtocolEnvelope.Create("identity", "registration_request",
                new RegistrationRequestMessage("codex", "Codex", state.SourceInstanceId, "1", "1", state.RequestNonce)));
            if (state.Credential is not null)
                AgentProtocolValidator.Validate(ProtocolEnvelope.Create("identity", "authenticate",
                    new AuthenticateRequest("codex", state.SourceInstanceId, state.Credential)));
            if (state.RequestId is not null)
                AgentProtocolValidator.Validate(ProtocolEnvelope.Create("identity", "registration_status",
                    new RegistrationStatusRequest(state.RequestId, state.SourceInstanceId, state.RequestNonce)));
            return true;
        }
        catch (AgentProtocolValidationException) { return false; }
    }
}
