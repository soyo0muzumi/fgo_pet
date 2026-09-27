namespace FgoPet.Core.Packs;

public enum PackErrorCode
{
    PackageArchiveInvalid,
    PackagePathEscapesRoot,
    PackageTooLarge,
    ManifestMalformed,
    SchemaUnsupported,
    AppVersionIncompatible,
    AssetMissing,
    AssetHashMismatch,
    ImageDecodeFailed,
    ImageHasNoVisibleAlpha,
    CompositionOutOfBounds,
    ExpressionMappingInvalid,
}

/// <summary>Stable, package-level error record. Carried inside <see cref="PackFailureException"/> or results.</summary>
public sealed record PackFailure(PackErrorCode Code, string Message, string? RelativePath = null)
{
    public override string ToString()
        => RelativePath is null
            ? $"{Code}: {Message}"
            : $"{Code}: {Message} ({RelativePath})";
}