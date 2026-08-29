using FgoPet.App.Dialogue;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Packs;
using FgoPet.Core.Settings;
using FgoPet.Infrastructure.Providers;

namespace FgoPet.App.Tests.Dialogue;

/// <summary>In-memory settings store for dialogue presentation tests.</summary>
public sealed class TestSettingsStore(AppSettings initial) : IAppSettingsStore
{
    public AppSettings Current { get; set; } = initial;

    public string Location => "memory";

    public AppSettings Load() => Current;

    public void Save(AppSettings settings) => Current = settings;

    public static TestSettingsStore WithModelConnection() => new(AppSettings.Defaults with
    {
        ModelConnection = new ModelConnectionSettings("test", "https://example.test/v1", "test-model"),
    });
}
