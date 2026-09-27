namespace FgoPet.Platform.Settings;

public interface ISettingsDocumentStore
{
    string Location { get; }
    string? Read();
    void Write(string document);
    void Quarantine();
}
