using FgoPet.App.Portraits;

namespace FgoPet.App.Runtime;

/// <summary>Composes current module state without owning business actions.</summary>
public sealed class AppRuntime
{
    public ActiveRoleState? ActiveRole { get; private set; }

    public PortraitState? Portrait { get; private set; }

    public event EventHandler<AppStateChangedEventArgs<ActiveRoleState>>? ActiveRoleChanged;

    public event EventHandler<AppStateChangedEventArgs<PortraitState>>? PortraitChanged;

    public void SetActiveRole(ActiveRoleState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        ActiveRole = state;
        ActiveRoleChanged?.Invoke(this, new AppStateChangedEventArgs<ActiveRoleState>(state));
    }

    public void SetPortrait(PortraitState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        Portrait = state;
        PortraitChanged?.Invoke(this, new AppStateChangedEventArgs<PortraitState>(state));
    }
}
