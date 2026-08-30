using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using FgoPet.Core.Agents;
using FgoPet.Core.Todo;

namespace FgoPet.App.ViewModels;

public sealed partial class AgentDispatchDialogViewModel : ObservableObject
{
    public AgentDispatchDialogViewModel(TodoItem todo, IAgentRepository agents)
    {
        Todo = todo ?? throw new ArgumentNullException(nameof(todo));
        Connections = new ObservableCollection<PersistedAgentConnection>(agents?.ListConnections() ?? throw new ArgumentNullException(nameof(agents)));
        SelectedConnection = Connections.FirstOrDefault(connection => connection.Enabled && connection.Capabilities.CanCreateTask);
        RefreshTargets();
    }

    public TodoItem Todo { get; }
    public ObservableCollection<PersistedAgentConnection> Connections { get; }
    public ObservableCollection<AgentProjectTarget> Targets { get; } = new();

    [ObservableProperty]
    private PersistedAgentConnection? _selectedConnection;

    [ObservableProperty]
    private AgentProjectTarget? _selectedTarget;

    public bool CanConfirm => SelectedConnection is { Enabled: true, Capabilities.CanCreateTask: true } && SelectedTarget is not null;

    partial void OnSelectedConnectionChanged(PersistedAgentConnection? value)
    {
        RefreshTargets();
        OnPropertyChanged(nameof(CanConfirm));
    }

    partial void OnSelectedTargetChanged(AgentProjectTarget? value) => OnPropertyChanged(nameof(CanConfirm));

    private void RefreshTargets()
    {
        Targets.Clear();
        if (SelectedConnection is null)
        {
            return;
        }

        foreach (var target in SelectedConnection.Capabilities.ProjectTargets)
        {
            Targets.Add(target);
        }

        SelectedTarget = Targets.FirstOrDefault();
    }
}
