using System.Security.Cryptography;
using System.Text;

namespace FgoPet.AgentRuntime.Security;

/// <summary>Current-user DPAPI protector shared by the Relay and Adapter state stores.</summary>
public sealed class DpapiSecretProtector : ISecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("FgoPet.AgentRuntime.State.v1");

    public byte[] Protect(ReadOnlySpan<byte> plaintext) =>
        ProtectedData.Protect(plaintext.ToArray(), Entropy, DataProtectionScope.CurrentUser);

    public byte[] Unprotect(ReadOnlySpan<byte> protectedData) =>
        ProtectedData.Unprotect(protectedData.ToArray(), Entropy, DataProtectionScope.CurrentUser);
}
