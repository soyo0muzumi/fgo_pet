using FgoPet.Core.Settings;
using Xunit;

namespace FgoPet.Core.Tests.Settings;

public sealed class ServantPreferenceTests
{
    [Fact]
    public void Address_preference_has_only_two_modes()
    {
        var preference = new ServantPreference(AddressMode.UserDefined, "御主");

        Assert.Equal(AddressMode.UserDefined, preference.AddressMode);
        Assert.Equal("御主", preference.AddressText);
    }

    [Fact]
    public void Package_default_mode_does_not_store_a_second_custom_address()
    {
        var preference = new ServantPreference(AddressMode.PackageDefault);

        Assert.Equal(AddressMode.PackageDefault, preference.AddressMode);
        Assert.Null(preference.AddressText);
    }

    [Fact]
    public void User_defined_mode_requires_a_bounded_address()
    {
        Assert.Throws<ArgumentException>(() => new ServantPreference(AddressMode.UserDefined, " "));
        Assert.Throws<ArgumentException>(() => new ServantPreference(AddressMode.UserDefined, new string('a', 129)));
    }
}
