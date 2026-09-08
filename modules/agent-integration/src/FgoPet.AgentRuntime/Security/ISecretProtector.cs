namespace FgoPet.AgentRuntime.Security;

/// <summary>Protects bytes at rest without exposing platform-specific details to stores.</summary>
public interface ISecretProtector
{
    byte[] Protect(ReadOnlySpan<byte> plaintext);
    byte[] Unprotect(ReadOnlySpan<byte> protectedData);
}
