using FgoPet.App.Settings;
using FgoPet.Character.Settings;
using FgoPet.Core.Geometry;
using FgoPet.Core.Portraits;
using FgoPet.Core.Settings;
using Xunit;

namespace FgoPet.App.Tests.Settings;

public sealed class PersonalizationViewModelTests
{
    [Fact]
    public void Changing_scale_applies_to_active_portrait()
    {
        var portrait = new FakePortraitController();
        var viewModel = new PersonalizationViewModel(new FakeSettingsStore(), portrait);

        viewModel.Scale = 0.75;

        Assert.Equal(0.75, portrait.LastScale);
    }

    [Fact]
    public void Changing_scale_without_an_active_portrait_shows_an_activation_notice()
    {
        var portrait = new ThrowingPortraitController();
        var store = new FakeSettingsStore();
        var viewModel = new PersonalizationViewModel(store, portrait);

        viewModel.Scale = 0.75;

        Assert.Equal("已保存，激活角色后生效。", viewModel.StatusText);
        Assert.Equal(0.75, store.Current.Scale);
    }

    [Fact]
    public void Persisted_scale_applies_when_the_portrait_later_activates()
    {
        var store = new FakeSettingsStore();
        var viewModel = new PersonalizationViewModel(store, new ThrowingPortraitController());

        viewModel.Scale = 0.60;
        Assert.Equal("已保存，激活角色后生效。", viewModel.StatusText);

        var portrait = new FakePortraitController();
        portrait.SetScale(store.Current.Scale);

        Assert.Equal(0.60, portrait.LastScale);
    }

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
            Current = CharacterSettings.Defaults with
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
            Current = CharacterSettings.Defaults with
            {
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
    public void Reset_restores_character_personalization_defaults()
    {
        var store = new FakeSettingsStore
        {
            Current = CharacterSettings.Defaults with
            {
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
        Assert.Null(typeof(PersonalizationViewModel).GetProperty("Theme"));
    }

    private sealed class FakeSettingsStore : ICharacterSettingsStore
    {
        public CharacterSettings Current { get; set; } = CharacterSettings.Defaults;

        public CharacterSettings Load() => Current;

        public void Save(CharacterSettings settings) => Current = settings;
    }

    private sealed class FakePortraitController : IPortraitController
    {
        public double? LastScale { get; private set; }
        public Task ActivateAsync(PortraitSelection selection, CancellationToken cancellationToken) => Task.CompletedTask;
        public void SetExpression(ExpressionSemantic semantic) { }
        public void SetScale(double scale) => LastScale = scale;
        public void ApplyDpi(Dpi2 dpi) { }
    }

    private sealed class ThrowingPortraitController : IPortraitController
    {
        public Task ActivateAsync(PortraitSelection selection, CancellationToken cancellationToken) => Task.CompletedTask;
        public void SetExpression(ExpressionSemantic semantic) { }
        public void SetScale(double scale) => throw new InvalidOperationException("尚未激活任何画像。");
        public void ApplyDpi(Dpi2 dpi) { }
    }
}
