using System.Runtime.Versioning;
using FgoPet.Core.Focus;

namespace FgoPet.App.Focus;

/// <summary>Commands and observable snapshot contract for the focus timer.</summary>
public interface IFocusSessionService
{
    FocusSession Current { get; }

    event EventHandler? SnapshotChanged;

    event EventHandler? PersistenceFailed;

    void Start(FocusPreset preset, string servantId);

    void Pause();

    void Resume();

    void Stop();

    void Tick();

    void Restore();
}
