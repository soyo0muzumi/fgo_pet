using System.Security.Cryptography;
using System.Text;
using FgoPet.Platform.Secrets;

namespace FgoPet.Platform.Windows.Secrets;

/// <summary>DPAPI CurrentUser, with purpose separation and bounded envelopes. No plaintext fallback.</summary>
public sealed class WindowsStateProtector : IProtectedStateProtector
{
    private const int MaxPlaintextBytes = 4 * 1024 * 1024;
    private const int MaxProtectedBytes = MaxPlaintextBytes + 64 * 1024;

    public byte[] Protect(ReadOnlySpan<byte> plaintext, string purpose)
    {
        if (plaintext.Length is <= 0 or > MaxPlaintextBytes) throw new StateProtectionException("STATE_PROTECTION_INVALID_INPUT");
        var entropy = Entropy(purpose);
        var input = plaintext.ToArray();
        try { return ProtectedData.Protect(input, entropy, DataProtectionScope.CurrentUser); }
        catch (CryptographicException) { throw new StateProtectionException("STATE_PROTECTION_FAILED"); }
        finally { CryptographicOperations.ZeroMemory(input); }
    }

    public byte[] Unprotect(ReadOnlySpan<byte> protectedState, string purpose)
    {
        if (protectedState.Length is <= 0 or > MaxProtectedBytes) throw new StateProtectionException("STATE_PROTECTION_INVALID_INPUT");
        var entropy = Entropy(purpose);
        try
        {
            var plaintext = ProtectedData.Unprotect(protectedState.ToArray(), entropy, DataProtectionScope.CurrentUser);
            if (plaintext.Length is > 0 and <= MaxPlaintextBytes) return plaintext;
            CryptographicOperations.ZeroMemory(plaintext);
            throw new StateProtectionException("STATE_PROTECTION_INVALID_INPUT");
        }
        catch (CryptographicException) { throw new StateProtectionException("STATE_PROTECTION_FAILED"); }
    }

    private static byte[] Entropy(string purpose)
    {
        if (purpose is not { Length: > 0 and <= 128 }
            || !purpose.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-'))
            throw new StateProtectionException("STATE_PROTECTION_INVALID_PURPOSE");
        return SHA256.HashData(Encoding.UTF8.GetBytes("FgoPet.protected-state.v1:" + purpose));
    }
}
