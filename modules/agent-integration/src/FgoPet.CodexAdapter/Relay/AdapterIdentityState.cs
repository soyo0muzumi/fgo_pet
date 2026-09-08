using System.Security.Cryptography;

namespace FgoPet.CodexAdapter.Relay;

public sealed record AdapterIdentityState(
    string SourceInstanceId, string RequestNonce, string? Credential = null, string? RequestId = null, int SchemaVersion = 1)
{
    public static AdapterIdentityState Create() => new("codex-" + Guid.NewGuid().ToString("N"), NewNonce());
    public static string NewNonce() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
}

public interface IAdapterIdentityStore
{
    AdapterIdentityState LoadOrCreate();
    bool TrySave(AdapterIdentityState expected, AdapterIdentityState updated);
}
