namespace FgoPet.Core.Windowing;

public interface IWindowPlacementStore
{
    string Location { get; }
    WindowPlacement? Load();
    void Save(WindowPlacement placement);
}