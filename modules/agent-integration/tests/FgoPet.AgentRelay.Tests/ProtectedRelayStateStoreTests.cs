using FgoPet.AgentProtocol.Messages;
using FgoPet.AgentRuntime.Security;
using FgoPet.AgentRuntime.Storage;
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

    [Fact]
    public void V1_state_migrates_to_v2_with_empty_maintenance_collections()
    {
        using var root = new TemporaryDirectory();
        var protector = new EchoProtector();
        var statePath = Path.Combine(root.Path, ProtectedRelayStateStore.FileName);
        var raw = new AtomicProtectedJsonStore<RelayState>(statePath, protector);
        raw.Save(new RelayState(1));

        var migrated = new ProtectedRelayStateStore(root.Path, protector).Load();

        Assert.Equal(2, migrated.SchemaVersion);
        Assert.Empty(migrated.ArchiveBatches);
        Assert.Empty(migrated.ArchiveTombstones);
        Assert.Empty(migrated.AdapterCapacityReports);
    }

    [Fact]
    public void Invalid_v2_state_is_quarantined_and_empty()
    {
        using var root = new TemporaryDirectory();
        var protector = new EchoProtector();
        var statePath = Path.Combine(root.Path, ProtectedRelayStateStore.FileName);
        var raw = new AtomicProtectedJsonStore<RelayState>(statePath, protector);
        raw.Save(new RelayState(2)
        {
            ArchiveBatches = new[]
            {
                new RelayArchiveBatchState(
                    "batch-1", "codex", "instance-1", DateTimeOffset.UtcNow,
                    (RelayArchiveBatchPhase)99, new[] { Item() }, Hash()),
            },
        });

        var state = new ProtectedRelayStateStore(root.Path, protector).Load();

        Assert.Equal(2, state.SchemaVersion);
        Assert.Empty(state.Grants);
        Assert.Single(Directory.GetFiles(root.Path, ProtectedRelayStateStore.FileName + ".corrupt-*"));
    }

    [Fact]
    public void Protected_round_trip_preserves_archive_prepare_and_commit_state()
    {
        using var root = new TemporaryDirectory();
        var protector = new EchoProtector();
        var batch = new RelayArchiveBatchState(
            "batch-1", "codex", "instance-1", DateTimeOffset.UtcNow,
            RelayArchiveBatchPhase.AwaitingAdapterCommit,
            new[] { Item() }, Hash());
        var original = new RelayState(2, archiveBatches: new[] { batch });
        var store = new ProtectedRelayStateStore(root.Path, protector);
        store.Save(original);

        var reloaded = store.Load();

        var roundTripped = Assert.Single(reloaded.ArchiveBatches);
        Assert.Equal(batch with { Items = null! }, roundTripped with { Items = null! });
        Assert.Equal(batch.Items, roundTripped.Items, new ArchiveItemComparer());
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

    private static AgentArchiveProtocolItem Item() => new(
        "codex", "instance-1", "task-1", "dispatch-1", 2, "completed",
        DateTimeOffset.Parse("2026-08-30T08:00:00Z"), "execution-1", Hash());

    private static string Hash() => new('A', 64);

    private sealed class ArchiveItemComparer : IEqualityComparer<IReadOnlyList<AgentArchiveProtocolItem>>
    {
        public bool Equals(IReadOnlyList<AgentArchiveProtocolItem>? x, IReadOnlyList<AgentArchiveProtocolItem>? y) =>
            x is not null && y is not null && x.SequenceEqual(y);

        public int GetHashCode(IReadOnlyList<AgentArchiveProtocolItem> obj) => obj.Count;
    }

    private sealed class EchoProtector : ISecretProtector
    {
        public byte[] Protect(ReadOnlySpan<byte> plaintext) => plaintext.ToArray();
        public byte[] Unprotect(ReadOnlySpan<byte> protectedData) => protectedData.ToArray();
    }
}
