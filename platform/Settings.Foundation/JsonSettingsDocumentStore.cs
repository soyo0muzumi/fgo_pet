using FgoPet.Infrastructure.Json;
using FgoPet.Platform.Settings;

namespace FgoPet.Infrastructure.Settings;

public sealed class JsonSettingsDocumentStore : ISettingsDocumentStore
{
    private readonly string _path;

    public JsonSettingsDocumentStore(string storageRoot) =>
        _path = Path.Combine(Path.GetFullPath(storageRoot), "settings.json");

    public string Location => _path;

    public string? Read() => AtomicJson.ReadOrNull(_path);

    public void Write(string document)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(document);
        AtomicJson.Write(_path, document);
    }

    public void Quarantine() => AtomicJson.Quarantine(_path);
}
