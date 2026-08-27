using CommunityToolkit.Mvvm.ComponentModel;
using FgoPet.Core.Panels;
using FgoPet.Core.Portraits;

namespace FgoPet.App.Panels;

/// <summary>Lightweight recent-servant switcher (3-5 entries) that delegates to the portrait controller.</summary>
public sealed partial class RecentServantSwitcherViewModel : ObservableObject
{
    private const int Capacity = 5;

    private readonly IPortraitController _controller;

    public RecentServantSwitcherViewModel(IPortraitController controller)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
    }

    [ObservableProperty]
    private IReadOnlyList<PortraitSelection> _entries = Array.Empty<PortraitSelection>();

    public void RecordUsed(PortraitSelection selection)
    {
        var updated = Entries.Where(existing => existing != selection).ToList();
        updated.Add(selection);
        if (updated.Count > Capacity)
        {
            updated.RemoveAt(0);
        }

        Entries = updated;
        HasEnough = Entries.Count >= 3;
    }

    [ObservableProperty]
    private bool _hasEnough;

    public async Task ActivateAsync(PortraitSelection selection, CancellationToken cancellationToken) =>
        await _controller.ActivateAsync(selection, cancellationToken).ConfigureAwait(false);
}