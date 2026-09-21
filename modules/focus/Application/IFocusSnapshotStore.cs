using FgoPet.Core.Focus;

namespace FgoPet.App.Focus;

/// <summary>Session snapshot persistence boundary used by the orchestration service.</summary>
public interface IFocusSnapshotStore
{
    void SaveSnapshot(FocusSession session);

    FocusSession? LoadCurrent();
}
