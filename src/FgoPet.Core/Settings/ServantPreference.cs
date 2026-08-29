using FgoPet.Core.Dialogue;

namespace FgoPet.Core.Settings;

public enum AddressMode
{
    PackageDefault,
    UserDefined,
}

public sealed record ServantPreference
{
    public ServantPreference(AddressMode addressMode, string? addressText = null)
    {
        AddressMode = addressMode;
        AddressText = addressMode switch
        {
            AddressMode.PackageDefault when string.IsNullOrWhiteSpace(addressText) => null,
            AddressMode.PackageDefault => throw new ArgumentException(
                "Package default mode cannot carry a custom address.", nameof(addressText)),
            AddressMode.UserDefined => Phase3Validation.Text(addressText ?? string.Empty, nameof(addressText), 128),
            _ => throw new ArgumentOutOfRangeException(nameof(addressMode)),
        };
    }

    public AddressMode AddressMode { get; }
    public string? AddressText { get; }
}
