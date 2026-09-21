namespace FgoPet.App.Privacy;

/// <summary>
/// Host-side capability: export the user's own dialogue/memory records to a portable archive.
/// </summary>
/// <remarks>
/// R9 in findings/38-contract-sinking-round2.md. Module UIs used to take the concrete
/// <c>UserDataExportService</c>/<c>UserDataDeletionService</c> from <c>host/DataManagement</c>,
/// which made every module UI depend on the data-management assembly and put
/// <c>data-management</c> inside the module dependency cycle. Depending on these contracts
/// (owned by the leaf assembly <c>FgoPet.HostContracts</c>) breaks that edge.
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
