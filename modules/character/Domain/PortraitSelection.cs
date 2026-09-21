namespace FgoPet.Core.Portraits;

/// <summary>Identifies one installed appearance. A null version means "the latest".</summary>
public sealed record PortraitSelection(string PackageId, string AppearanceId, string? PackageVersion = null)
{
    public override string ToString() =>
        PackageVersion is null ? $"{PackageId}/{AppearanceId}" : $"{PackageId}@{PackageVersion}/{AppearanceId}";
}