using FgoPet.Core.Portraits;

namespace FgoPet.App.Portraits;

/// <summary>
/// Bounded snapshot cache holding exactly the current appearance plus one recent
/// appearance. Loading a third appearance evicts the oldest unreferenced snapshot.
/// </summary>
public sealed class PortraitSnapshotCache
{
    public const int Capacity = 2;

    private readonly Dictionary<string, PortraitSnapshot> _snapshots = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _recent = new();

    public PortraitSnapshot? TryGet(PortraitSelection selection)
    {
        var key = Key(selection);
        return _snapshots.TryGetValue(key, out var snapshot) ? snapshot : null;
    }

    public void Put(PortraitSelection selection, PortraitSnapshot snapshot)
    {
        var key = Key(selection);
        if (_snapshots.ContainsKey(key))
        {
            _recent.Remove(key);
        }
        else if (_snapshots.Count >= Capacity)
        {
            var oldest = _recent.First!.Value;
            _recent.RemoveFirst();
            _snapshots.Remove(oldest);
        }

        _recent.AddLast(key);
        _snapshots[key] = snapshot;
    }

    public int Count => _snapshots.Count;

    private static string Key(PortraitSelection selection) =>
        $"{selection.PackageId}|{selection.PackageVersion ?? "*"}|{selection.AppearanceId}";
}