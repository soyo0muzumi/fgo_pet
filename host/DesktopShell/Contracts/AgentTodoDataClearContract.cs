namespace FgoPet.App.Services;

/// <summary>
/// Host-side capability: clear agent/todo execution data as part of a guided reset.
/// </summary>
/// <remarks>R9 in findings/38-contract-sinking-round2.md — see <c>IUserDataExporter</c> for why.</remarks>
public interface IAgentTodoDataClearer
{
    Task ClearAgentTodoDataAsync(CancellationToken cancellationToken = default);
}
