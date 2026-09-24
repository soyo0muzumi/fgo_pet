namespace FgoPet.Memory.Settings;

public sealed record MemorySettings(bool Enabled)
{
    public static MemorySettings Defaults { get; } = new(true);
    public const int MaxItemsPerServant = 200;
    public const int MaxCharsPerServant = 40_000;
}

public interface IMemorySettingsStore
{
    MemorySettings Load();
    void Save(MemorySettings settings);
}
