namespace FgoPet.Memory.Settings;

public sealed record MemorySettings(bool Enabled)
{
    public static MemorySettings Defaults { get; } = new(true);
}

public interface IMemorySettingsStore
{
    MemorySettings Load();
    void Save(MemorySettings settings);
}
