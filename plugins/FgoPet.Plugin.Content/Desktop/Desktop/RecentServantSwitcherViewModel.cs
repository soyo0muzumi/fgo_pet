using CommunityToolkit.Mvvm.ComponentModel;
using FgoPet.App.Servants;
using FgoPet.Core.Panels;
using FgoPet.Core.Portraits;

namespace FgoPet.App.Panels;

/// <summary>Lightweight recent-servant switcher (3-5 entries) that delegates to the portrait controller.</summary>
public sealed partial class RecentServantSwitcherViewModel : ObservableObject
{
    private const int Capacity = 5;

    private readonly IPortraitController _controller;
    private readonly IRoleActivationService? _activation;

    public RecentServantSwitcherViewModel(IPortraitController controller, IRoleActivationService? activation = null)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _activation = activation;
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

    public async Task ActivateAsync(PortraitSelection selection, CancellationToken cancellationToken)
    {
        if (_activation is not null)
        {
            var result = await _activation.ActivateAsync(selection, cancellationToken).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                throw new InvalidOperationException(result.Error ?? "角色包激活失败。");
            }

            return;
        }

        await _controller.ActivateAsync(selection, cancellationToken).ConfigureAwait(false);
    }
}
