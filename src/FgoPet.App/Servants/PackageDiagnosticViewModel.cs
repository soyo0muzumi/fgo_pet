using FgoPet.Core.Packs;

namespace FgoPet.App.Servants;

/// <summary>
/// A user-visible package error with stable code and a redacted location: only the
/// relative path is shown, never an absolute source path.
/// </summary>
public sealed class PackageDiagnosticViewModel
{
    public PackageDiagnosticViewModel(PackFailure failure)
    {
        Code = failure.Code;
        Heading = $"错误 {Code}";
        Text = Compose(failure);
    }

    public PackErrorCode Code { get; }

    public string Heading { get; }

    public string Text { get; }

    private static string Compose(PackFailure failure)
    {
        var relative = failure.RelativePath;
        return string.IsNullOrWhiteSpace(relative)
            ? $"{failure.Code}"
            : $"{failure.Code} {relative}";
    }
}