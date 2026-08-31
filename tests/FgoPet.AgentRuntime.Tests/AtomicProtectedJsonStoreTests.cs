using System.Text;
using FgoPet.AgentRuntime.Security;
using FgoPet.AgentRuntime.Storage;
using Xunit;

namespace FgoPet.AgentRuntime.Tests;

public sealed class AtomicProtectedJsonStoreTests
{
    [Fact]
    public void Save_and_load_round_trip_without_plaintext_payload()
    {
        using var root = new TemporaryDirectory();
        var path = Path.Combine(root.Path, "state.json");
        var protector = new RecordingProtector();
        var store = new AtomicProtectedJsonStore<SampleState>(path, protector);

        store.Save(new SampleState("secret", 42));

        var loaded = store.Load();
        Assert.Equal(new SampleState("secret", 42), loaded);
        Assert.DoesNotContain("secret", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.True(protector.ProtectCount > 0);
        Assert.True(protector.UnprotectCount > 0);
    }

    [Fact]
    public void Corrupt_content_is_quarantined_and_returns_empty_state()
    {
        using var root = new TemporaryDirectory();
        var path = Path.Combine(root.Path, "state.json");
        File.WriteAllText(path, "not-json");
        var store = new AtomicProtectedJsonStore<SampleState>(path, new RecordingProtector());

        Assert.Equal(default, store.Load());
        Assert.Single(Directory.GetFiles(root.Path, "state.json.corrupt-*"));
    }

    [Fact]
    public void Oversized_state_is_rejected_before_replacing_previous_file()
    {
        using var root = new TemporaryDirectory();
        var path = Path.Combine(root.Path, "state.json");
        var store = new AtomicProtectedJsonStore<SampleState>(path, new RecordingProtector(), maxBytes: 512);
        store.Save(new SampleState("small", 1));
        var before = File.ReadAllBytes(path);

        Assert.Throws<InvalidDataException>(() => store.Save(new SampleState(new string('x', 4096), 2)));
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(new SampleState("small", 1), store.Load());
    }

    [Fact]
    public async Task Concurrent_saves_are_serialized_and_leave_a_complete_snapshot()
    {
        using var root = new TemporaryDirectory();
        var store = new AtomicProtectedJsonStore<SampleState>(
            Path.Combine(root.Path, "state.json"), new RecordingProtector());
        await Task.WhenAll(
            Task.Run(() => store.Save(new SampleState("first", 1))),
            Task.Run(() => store.Save(new SampleState("second", 2))));

        var loaded = store.Load();
        Assert.Contains(loaded, new[] { new SampleState("first", 1), new SampleState("second", 2) });
    }

    private sealed record SampleState(string Value, int Number);

    private sealed class RecordingProtector : ISecretProtector
    {
        public int ProtectCount { get; private set; }
        public int UnprotectCount { get; private set; }

        public byte[] Protect(ReadOnlySpan<byte> plaintext)
        {
            ProtectCount++;
            return Encoding.UTF8.GetBytes(Convert.ToBase64String(plaintext));
        }

        public byte[] Unprotect(ReadOnlySpan<byte> protectedData)
        {
            UnprotectCount++;
            return Convert.FromBase64String(Encoding.UTF8.GetString(protectedData));
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "FgoPet-Store-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
