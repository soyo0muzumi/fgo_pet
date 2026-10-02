using FgoPet.Infrastructure.Settings;
using Xunit;

namespace FgoPet.Infrastructure.Tests.Settings;

public sealed class JsonSettingsDocumentStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"fgo-document-store-{Guid.NewGuid():N}");

    public JsonSettingsDocumentStoreTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Read_write_and_quarantine_are_opaque_and_atomic()
    {
        var store = new JsonSettingsDocumentStore(_root);
        Assert.Null(store.Read());

        store.Write("{\"schema_version\":2}");
        Assert.Equal("{\"schema_version\":2}", store.Read());

        store.Quarantine();
        Assert.Null(store.Read());
        Assert.Single(Directory.GetFiles(_root, "settings.json.corrupt.*"));
    }

    [Fact]
    public void Write_rejects_blank_documents()
    {
        var store = new JsonSettingsDocumentStore(_root);
        Assert.Throws<ArgumentException>(() => store.Write("   "));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
