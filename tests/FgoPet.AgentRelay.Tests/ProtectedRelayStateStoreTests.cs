using FgoPet.AgentRelay.Storage;
using Xunit;

namespace FgoPet.AgentRelay.Tests;

public sealed class ProtectedRelayStateStoreTests
{
    [Fact]
    public void Grant_and_revoke_survive_store_recreation()
    {
        using var root = new TemporaryDirectory();
        var first = new ProtectedRelayStateStore(root.Path);
        var grant = new RegistrationGrant("codex", "instance-1", Convert.ToBase64String(new byte[32]), DateTimeOffset.UtcNow);
        first.Save(new RelayState(1, Array.Empty<PendingRegistration>(), new[] { grant }));

        var reloaded = new ProtectedRelayStateStore(root.Path).Load();
        Assert.Equal(grant, Assert.Single(reloaded.Grants));
        var revoked = reloaded with { Grants = Array.Empty<RegistrationGrant>() };
        first.Save(revoked);
        Assert.Empty(new ProtectedRelayStateStore(root.Path).Load().Grants);
    }

    [Fact]
    public void Corrupt_state_is_quarantined_and_empty()
    {
        using var root = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(root.Path, ProtectedRelayStateStore.FileName), "not-json");

        var state = new ProtectedRelayStateStore(root.Path).Load();

        Assert.Empty(state.Grants);
        Assert.Single(Directory.GetFiles(root.Path, ProtectedRelayStateStore.FileName + ".corrupt-*"));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "FgoPet-RelayState-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
