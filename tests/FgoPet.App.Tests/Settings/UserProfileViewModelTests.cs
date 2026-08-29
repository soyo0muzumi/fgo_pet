using FgoPet.App.Settings;
using FgoPet.Core.Settings;
using Xunit;

namespace FgoPet.App.Tests.Settings;

public sealed class UserProfileViewModelTests
{
    [Fact]
    public void Initial_status_is_empty_until_an_action_produces_feedback()
    {
        var viewModel = new UserProfileViewModel(new FakeSettingsStore());

        Assert.Empty(viewModel.StatusText);
    }

    [Fact]
    public void Saving_display_name_changes_only_global_profile_data()
    {
        var preference = new ServantPreference(AddressMode.UserDefined, "御主");
        var store = new FakeSettingsStore
        {
            Current = AppSettings.Defaults with
            {
                Theme = AppTheme.FgoLight,
                UserProfile = new UserProfile("旧名称"),
                ServantPreferences = new Dictionary<string, ServantPreference>
                {
                    ["mash_kyrielight"] = preference,
                },
            },
        };
        var viewModel = new UserProfileViewModel(store)
        {
            DisplayName = "  新名称  ",
        };

        viewModel.SaveCommand.Execute(null);

        Assert.NotNull(store.Saved);
        Assert.Equal("新名称", store.Saved!.UserProfile!.DisplayName);
        Assert.Same(preference, store.Saved.ServantPreferences["mash_kyrielight"]);
        Assert.Equal(AppTheme.FgoLight, store.Saved.Theme);
        Assert.Contains("保存", viewModel.StatusText, StringComparison.Ordinal);
        Assert.Empty(viewModel.ErrorText);
    }

    [Fact]
    public void Blank_display_name_clears_profile_without_touching_servant_preferences()
    {
        var preference = new ServantPreference(AddressMode.UserDefined, "御主");
        var store = new FakeSettingsStore
        {
            Current = AppSettings.Defaults with
            {
                UserProfile = new UserProfile("xqj"),
                ServantPreferences = new Dictionary<string, ServantPreference>
                {
                    ["mash_kyrielight"] = preference,
                },
            },
        };
        var viewModel = new UserProfileViewModel(store) { DisplayName = "  " };

        viewModel.SaveCommand.Execute(null);

        Assert.Null(store.Saved!.UserProfile);
        Assert.Same(preference, store.Saved.ServantPreferences["mash_kyrielight"]);
    }

    [Fact]
    public void Reset_clears_global_profile_only()
    {
        var preference = new ServantPreference(AddressMode.UserDefined, "御主");
        var store = new FakeSettingsStore
        {
            Current = AppSettings.Defaults with
            {
                UserProfile = new UserProfile("xqj"),
                ServantPreferences = new Dictionary<string, ServantPreference>
                {
                    ["mash_kyrielight"] = preference,
                },
            },
        };
        var viewModel = new UserProfileViewModel(store);

        viewModel.ResetCommand.Execute(null);

        Assert.Null(store.Saved!.UserProfile);
        Assert.Same(preference, store.Saved.ServantPreferences["mash_kyrielight"]);
        Assert.Equal(string.Empty, viewModel.DisplayName);
    }

    [Theory]
    [InlineData("\n新名称")]
    [InlineData("新名称\r")]
    public void Display_name_rejects_line_breaks_before_trimming(string rawDisplayName)
    {
        var store = new FakeSettingsStore();
        var viewModel = new UserProfileViewModel(store) { DisplayName = rawDisplayName };

        viewModel.SaveCommand.Execute(null);

        Assert.Null(store.Saved);
        Assert.Contains("换行", viewModel.ErrorText, StringComparison.Ordinal);
    }

    private sealed class FakeSettingsStore : IAppSettingsStore
    {
        public string Location => "memory";

        public AppSettings Current { get; set; } = AppSettings.Defaults;

        public AppSettings? Saved { get; private set; }

        public AppSettings Load() => Current;

        public void Save(AppSettings settings)
        {
            Current = settings;
            Saved = settings;
        }
    }
}
