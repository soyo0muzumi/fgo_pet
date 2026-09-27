namespace FgoPet.Core.Windowing;

/// <summary>Named window placement slots persisted by the app.</summary>
public static class WindowPlacementSlots
{
    public const string Portrait = "portrait";
    public const string Dialogue = "dialogue";
}

public interface IWindowPlacementStore
{
    string Location { get; }
    WindowPlacement? Load();
    void Save(WindowPlacement placement);

    /// <summary>Loads a named slot; the parameterless overloads address the portrait slot.</summary>
    WindowPlacement? Load(string slot);

    void Save(string slot, WindowPlacement placement);
}
