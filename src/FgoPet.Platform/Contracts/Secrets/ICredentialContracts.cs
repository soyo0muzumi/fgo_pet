namespace FgoPet.Core.Secrets;

/// <summary>Protected credential write/delete capability exposed to settings UI.</summary>
public interface ICredentialStore
{
    Task SaveAsync(string target, string secret, CancellationToken cancellationToken);

    Task<bool> ExistsAsync(string target, CancellationToken cancellationToken);

    Task DeleteAsync(string target, CancellationToken cancellationToken);
}

/// <summary>Provider-facing read capability; implementations must resolve protected storage.</summary>
public interface ICredentialReader
{
    Task<string?> ReadAsync(string target, CancellationToken cancellationToken);
}