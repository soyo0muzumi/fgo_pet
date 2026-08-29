using FgoPet.App.Settings;
using FgoPet.Core.Settings;
using Xunit;

namespace FgoPet.App.Tests.Settings;

public sealed class PersonalizationViewModelTests
{
    [Fact]
    public void Initial_status_is_empty_until_an_action_produces_feedback()
    {
        var viewModel = new PersonalizationViewModel(new FakeSettingsStore());

        Assert.Empty(viewModel.StatusText);
    }

    [Fact]
    public void Loads_supported_scale_values_and_persisted_personalization()
    {
        var store = new FakeSettingsStore
        {
            Current = AppSettings.Defaults with
            {
                Scale = 0.75,
                Topmost = false,
                AutoCollapseExpandedPanel = false,
            },
        };

        var viewModel = new PersonalizationViewModel(store);

        Assert.Equal([0.50, 0.60, 0.75], viewModel.ScaleOptions);
        Assert.Equal(0.75, viewModel.Scale);
        Assert.False(viewModel.Topmost);
        Assert.False(viewModel.AutoCollapseExpandedPanel);
    }

    [Fact]
    public void Changing_scale_topmost_and_auto_collapse_round_trips_without_touching_profile_or_servant_preferences()
    {
        var preference = new ServantPreference(AddressMode.UserDefined, "御主");
        var store = new FakeSettingsStore
        {
            Current = AppSettings.Defaults with
            {
                Theme = AppTheme.FgoLight,
                UserProfile = new UserProfile("xqj"),
                ServantPreferences = new Dictionary<string, ServantPreference>
                {
                    ["mash_kyrielight"] = preference,
                },
            },
        };
        var viewModel = new PersonalizationViewModel(store);

        viewModel.Scale = 0.60;
        viewModel.Topmost = false;
        viewModel.AutoCollapseExpandedPanel = false;

        Assert.Equal(0.60, store.Current.Scale);
        Assert.False(store.Current.Topmost);
        Assert.False(store.Current.AutoCollapseExpandedPanel);
        Assert.Equal(AppTheme.FgoLight, store.Current.Theme);
        Assert.Equal("xqj", store.Current.UserProfile!.DisplayName);
        Assert.Same(preference, store.Current.ServantPreferences["mash_kyrielight"]);
    }

    [Fact]
    public void Invalid_scale_is_rejected_without_persisting_an_unsupported_value()
    {
        var store = new FakeSettingsStore();
        var viewModel = new PersonalizationViewModel(store);

        viewModel.Scale = 0.70;

        Assert.Equal(0.50, viewModel.Scale);
        Assert.Equal(0.50, store.Current.Scale);
        Assert.NotEmpty(viewModel.ErrorText);
    }

    [Fact]
    public void Reset_restores_defaults_but_does_not_own_or_change_theme()
    {
        var store = new FakeSettingsStore
        {
            Current = AppSettings.Defaults with
            {
                Theme = AppTheme.FgoLight,
                Scale = 0.75,
                Topmost = false,
                AutoCollapseExpandedPanel = false,
            },
        };
        var viewModel = new PersonalizationViewModel(store);

        viewModel.ResetCommand.Execute(null);

        Assert.Equal(0.50, store.Current.Scale);
        Assert.True(store.Current.Topmost);
        Assert.True(store.Current.AutoCollapseExpandedPanel);
        Assert.Equal(AppTheme.FgoLight, store.Current.Theme);
        Assert.Null(typeof(PersonalizationViewModel).GetProperty("Theme"));
    }

    private sealed class FakeSettingsStore : IAppSettingsStore
    {
        public string Location => "memory";

        public AppSettings Current { get; set; } = AppSettings.Defaults;

        public AppSettings Load() => Current;

        public void Save(AppSettings settings) => Current = settings;
    }
}
