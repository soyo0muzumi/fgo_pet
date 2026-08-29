using FgoPet.Infrastructure.Secrets;
using Xunit;

namespace FgoPet.Infrastructure.Tests.Secrets;

public sealed class CredentialStoreContractTests
{
    [Fact]
    public async Task Credential_store_can_save_check_and_delete_without_read_secret_api()
    {
        var store = new InMemoryCredentialStore();

        await store.SaveAsync("fgo-pet/openai", "secret-value", CancellationToken.None);

        Assert.True(await store.ExistsAsync("fgo-pet/openai", CancellationToken.None));
        await store.DeleteAsync("fgo-pet/openai", CancellationToken.None);
        Assert.False(await store.ExistsAsync("fgo-pet/openai", CancellationToken.None));
    }

    private sealed class InMemoryCredentialStore : ICredentialStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

        public Task SaveAsync(string target, string secret, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _values[target] = secret;
            return Task.CompletedTask;
        }

        public Task<bool> ExistsAsync(string target, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_values.ContainsKey(target));
        }

        public Task DeleteAsync(string target, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _values.Remove(target);
            return Task.CompletedTask;
        }
    }
}
