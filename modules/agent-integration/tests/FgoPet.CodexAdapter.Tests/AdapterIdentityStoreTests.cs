using System.Security.Cryptography;
using FgoPet.AgentRuntime.Security;
using FgoPet.AgentRuntime.Storage;
using FgoPet.CodexAdapter.Relay;
using Xunit;

namespace FgoPet.CodexAdapter.Tests;

public sealed class AdapterIdentityStoreTests
{
    [Fact]
    public void Identity_and_credential_survive_recreation_without_plaintext_on_disk()
    {
        using var root = new TestRoot();
        var store = new AdapterIdentityStore(root.Path);
        var identity = store.LoadOrCreate();
        var credential = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

        Assert.True(store.TrySave(identity, identity with { Credential = credential, RequestId = "request-1" }));
        var reloaded = new AdapterIdentityStore(root.Path).LoadOrCreate();

        Assert.Equal(identity.SourceInstanceId, reloaded.SourceInstanceId);
        Assert.Equal(identity.RequestNonce, reloaded.RequestNonce);
        Assert.Equal(credential, reloaded.Credential);
        var disk = File.ReadAllText(root.StatePath);
        Assert.DoesNotContain(credential, disk, StringComparison.Ordinal);
        Assert.DoesNotContain(identity.SourceInstanceId, disk, StringComparison.Ordinal);
    }

    [Fact]
    public void Stale_adapter_instance_cannot_overwrite_a_newer_credential()
    {
        using var root = new TestRoot();
        var first = new AdapterIdentityStore(root.Path);
        var second = new AdapterIdentityStore(root.Path);
        var original = first.LoadOrCreate();
        Assert.Equal(original, second.LoadOrCreate());
        var credential = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

        Assert.True(first.TrySave(original, original with { Credential = credential }));
        Assert.False(second.TrySave(original, original with { RequestId = "stale-request" }));
        Assert.Equal(credential, second.LoadOrCreate().Credential);
    }

    [Fact]
    public async Task Concurrent_first_loads_share_one_durable_identity()
    {
        using var root = new TestRoot();
        var identities = await Task.WhenAll(
            Task.Run(() => new AdapterIdentityStore(root.Path).LoadOrCreate()),
            Task.Run(() => new AdapterIdentityStore(root.Path).LoadOrCreate()));

        Assert.Equal(identities[0], identities[1]);
        Assert.Equal(identities[0], new AdapterIdentityStore(root.Path).LoadOrCreate());
    }

    [Fact]
    public void Unsupported_identity_schema_is_quarantined_without_reusing_its_credential()
    {
        using var root = new TestRoot();
        new AtomicProtectedJsonStore<AdapterIdentityState>(root.StatePath, new DpapiSecretProtector()).Save(
            new AdapterIdentityState("old-source", new string('a', 64), Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), SchemaVersion: 2));

        var fresh = new AdapterIdentityStore(root.Path).LoadOrCreate();

        Assert.Null(fresh.Credential);
        Assert.NotEqual("old-source", fresh.SourceInstanceId);
        Assert.Single(Directory.GetFiles(System.IO.Path.GetDirectoryName(root.StatePath)!, "*.corrupt-*"));
    }

    private sealed class TestRoot : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "FgoPet-Adapter-Tests", Guid.NewGuid().ToString("N"));
        public string StatePath => System.IO.Path.Combine(Path, "CodexAdapter", "adapter-state.v1.json");
        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
