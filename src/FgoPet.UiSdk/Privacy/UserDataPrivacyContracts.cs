namespace FgoPet.App.Privacy;

/// <summary>
/// Host-side capability: export the user's own dialogue/memory records to a portable archive.
/// </summary>
/// <remarks>
/// Published UI request port. DataManagement owns the export policy and implementation;
/// callers supply only a destination and cancellation, never direct repository access.
/// Existing namespace and command signatures remain unchanged.
/// </remarks>
public interface IUserDataExporter
{
    Task ExportAsync(string destinationPath, CancellationToken cancellationToken);
}

/// <summary>Host-side capability: delete user-owned data, in whole or for one conversation.</summary>
public interface IUserDataDeleter
{
    Task DeleteAllAsync(CancellationToken cancellationToken);

    Task DeleteConversationAsync(string conversationId, string servantId, CancellationToken cancellationToken);
}
