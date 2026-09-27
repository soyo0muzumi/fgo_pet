using CommunityToolkit.Mvvm.ComponentModel;
using FgoPet.Core.Agents;

namespace FgoPet.App.ViewModels;

public sealed partial class AgentTargetOptionViewModel : ObservableObject
{
    internal AgentTargetOptionViewModel(AgentTargetDescriptor target, bool isSelected)
    {
        ArgumentNullException.ThrowIfNull(target);
        TargetId = target.TargetId;
        DisplayName = target.DisplayName;
        IsReadOnly = target.IsReadOnly;
        _isSelected = isSelected;
    }

    internal string TargetId { get; }
    public string DisplayName { get; }
    public bool IsReadOnly { get; }
    public bool IsResolved => true;

    [ObservableProperty]
    private bool _isSelected;
}
