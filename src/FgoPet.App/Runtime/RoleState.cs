namespace FgoPet.App.Runtime;

/// <summary>Immutable identity of the role currently active in the application.</summary>
public sealed record ActiveRoleState(
    string PackageId,
    string AppearanceId,
    string PackageVersion,
    string ServantId);
