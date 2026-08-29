using FgoPet.App.Servants;
using FgoPet.Core.Settings;
using Xunit;

namespace FgoPet.App.Tests.Servants;

public sealed class ServantPreferenceTests
{
    [Fact]
    public async Task Address_preference_is_saved_by_servant_id()
    {
        var settings = new FakeSettings();
        var preferences = new ServantPreferenceService(settings);

        await preferences.SaveAsync("800100", new ServantPreference(AddressMode.UserDefined, "御主"));

        Assert.Equal("御主", (await preferences.LoadAsync("800100")).AddressText);
        Assert.Equal(AddressMode.PackageDefault, (await preferences.LoadAsync("100001")).AddressMode);
    }

    [Fact]
    public async Task Package_default_resolution_prefers_appearance_then_persona_then_neutral()
    {
        var preferences = new ServantPreferenceService(new FakeSettings());

        Assert.Equal("外观称呼", await preferences.ResolveAddressAsync("800100", "外观称呼", "Persona称呼"));
        Assert.Equal("Persona称呼", await preferences.ResolveAddressAsync("800100", null, "Persona称呼"));
        Assert.Equal("御主", await preferences.ResolveAddressAsync("800100", null, null));
    }

    private sealed class FakeSettings : IAppSettingsStore
    {
        private AppSettings _current = AppSettings.Defaults;
        public string Location => "memory";
        public AppSettings Load() => _current;
        public void Save(AppSettings settings) => _current = settings;
    }
}
