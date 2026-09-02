using System.Text.Json;
using System.Text.Json.Serialization;
using FgoPet.Core.Backup;
using FgoPet.Core.Portraits;
using FgoPet.Infrastructure.Packs;

namespace FgoPet.Infrastructure.Backup;

/// <summary>Serializes and validates the package IDs/versions used by a private backup.</summary>
public sealed class BackupPackageReferencesCodec
{
    public string Serialize(BackupPackageReferences references)
    {
        ArgumentNullException.ThrowIfNull(references);
        ValidateSelection(references.Selected);
        ValidateSelection(references.LastKnownGood);
        return JsonSerializer.Serialize(new PackageReferencesDto(
            1,
            SelectionDto.FromModel(references.Selected),
            SelectionDto.FromModel(references.LastKnownGood)));
    }

    public BackupPackageReferences Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new BackupException(BackupFailureCode.PackageReferencesInvalid, "Backup package references are empty.");
        }

        try
        {
            var dto = JsonSerializer.Deserialize<PackageReferencesDto>(json)
                ?? throw new JsonException("Package references are empty.");
            if (dto.SchemaVersion != 1)
            {
                throw new BackupException(BackupFailureCode.PackageReferencesInvalid, "Backup package reference schema is unsupported.");
            }

            var selected = dto.Selected?.ToModel();
            var lastKnownGood = dto.LastKnownGood?.ToModel();
            ValidateSelection(selected);
            ValidateSelection(lastKnownGood);
            return new BackupPackageReferences(selected, lastKnownGood);
        }
        catch (BackupException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            throw new BackupException(BackupFailureCode.PackageReferencesInvalid, "Backup package references are invalid.", exception);
        }
    }

    private static void ValidateSelection(PortraitSelection? selection)
    {
        if (selection is null)
        {
            return;
        }

        if (!IsSafeReference(selection.PackageId)
            || !IsSafeReference(selection.AppearanceId)
            || (selection.PackageVersion is not null && !IsSafeReference(selection.PackageVersion)))
        {
            throw new BackupException(BackupFailureCode.PackageReferencesInvalid, "Backup package references are invalid.");
        }
    }

    private static bool IsSafeReference(string? value) =>
        value is { Length: > 0 and <= 256 }
        && !value.Contains('\0')
        && !value.Contains('/')
        && !value.Contains('\\')
        && !Path.IsPathRooted(value);

    private sealed record PackageReferencesDto(
        [property: JsonPropertyName("schema_version")] int SchemaVersion,
        [property: JsonPropertyName("selected")] SelectionDto? Selected,
        [property: JsonPropertyName("last_known_good")] SelectionDto? LastKnownGood);

    private sealed record SelectionDto(
        [property: JsonPropertyName("package_id")] string? PackageId,
        [property: JsonPropertyName("appearance_id")] string? AppearanceId,
        [property: JsonPropertyName("package_version")] string? PackageVersion)
    {
        public PortraitSelection? ToModel() =>
            PackageId is null || AppearanceId is null
                ? throw new JsonException("Package selection is incomplete.")
                : new PortraitSelection(PackageId, AppearanceId, PackageVersion);

        public static SelectionDto? FromModel(PortraitSelection? selection) => selection is null
            ? null
            : new SelectionDto(selection.PackageId, selection.AppearanceId, selection.PackageVersion);
    }
}
