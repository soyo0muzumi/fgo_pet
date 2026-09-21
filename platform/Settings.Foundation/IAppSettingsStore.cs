namespace FgoPet.Core.Settings;

public interface IAppSettingsStore
{
    string Location { get; }
    AppSettings Load();
    void Save(AppSettings settings);
}