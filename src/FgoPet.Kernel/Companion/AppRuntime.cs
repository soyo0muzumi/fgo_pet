namespace FgoPet.App.Runtime;

/// <summary>Owns the current companion identity; rendering state belongs to the renderer.</summary>
public sealed class AppRuntime
{
    public ActiveRoleState? ActiveRole { get; private set; }

    public event EventHandler<AppStateChangedEventArgs<ActiveRoleState>>? ActiveRoleChanged;

    public void SetActiveRole(ActiveRoleState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        ActiveRole = state;
        ActiveRoleChanged?.Invoke(this, new AppStateChangedEventArgs<ActiveRoleState>(state));
    }

}
