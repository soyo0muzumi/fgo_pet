namespace FgoPet.Infrastructure.Secrets;

public interface ICredentialStore
{
    Task SaveAsync(string target, string secret, CancellationToken cancellationToken);

    Task<bool> ExistsAsync(string target, CancellationToken cancellationToken);

    Task DeleteAsync(string target, CancellationToken cancellationToken);
}

/// <summary>Internal provider-facing read capability, never injected into UI view models.</summary>
public interface ICredentialReader
{
    Task<string?> ReadAsync(string target, CancellationToken cancellationToken);
}
