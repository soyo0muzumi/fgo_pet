namespace FgoPet.Platform.Secrets;

/// <summary>Local protected state primitive. Purpose separates unrelated persisted envelopes.</summary>
public interface IProtectedStateProtector
{
    byte[] Protect(ReadOnlySpan<byte> plaintext, string purpose);
    byte[] Unprotect(ReadOnlySpan<byte> protectedState, string purpose);
}

public sealed class StateProtectionException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
