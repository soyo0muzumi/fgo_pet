namespace FgoPet.Core.Packs;

/// <summary>Location of one installed appearance: its containing directory with the manifest and runtime.</summary>
public sealed record AppearanceLocation(PackIdentity Identity, string AppearanceId, string AppearanceRoot);