namespace FgoPet.Infrastructure.Secrets;

public interface ICredentialStore
{
    Task SaveAsync(string target, string secret, CancellationToken cancellationToken);

    Task<bool> ExistsAsync(string target, CancellationToken cancellationToken);

    Task DeleteAsync(string target, CancellationToken cancellationToken);
}
