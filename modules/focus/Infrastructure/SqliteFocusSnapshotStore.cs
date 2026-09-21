using FgoPet.Core.Focus;
using FgoPet.Infrastructure.Focus;

namespace FgoPet.App.Focus;

/// <summary>Adapts the SQLite focus repository to the snapshot store contract.</summary>
public sealed class SqliteFocusSnapshotStore(SqliteFocusRepository repository) : IFocusSnapshotStore
{
    public void SaveSnapshot(FocusSession session) => repository.SaveSnapshot(session);

    public FocusSession? LoadCurrent() => repository.LoadCurrent();
}
