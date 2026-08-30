using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FgoPet.App.Services;
using FgoPet.Core.Agents;
using FgoPet.Core.Todo;

namespace FgoPet.App.ViewModels;

public sealed partial class AgentDispatchDialogViewModel : ObservableObject
{
    private readonly AgentDispatchService? _dispatchService;

    public AgentDispatchDialogViewModel(TodoItem todo, IAgentRepository agents, AgentDispatchService? dispatchService = null)
    {
        Todo = todo ?? throw new ArgumentNullException(nameof(todo));
        Connections = new ObservableCollection<PersistedAgentConnection>(agents?.ListConnections() ?? throw new ArgumentNullException(nameof(agents)));
        _dispatchService = dispatchService;
        SelectedConnection = Connections.FirstOrDefault(connection => connection.Enabled && connection.Capabilities.CanCreateTask);
        RefreshTargets();
        ConfirmCommand = new AsyncRelayCommand(ConfirmAsync, () => CanConfirm);
    }

    public TodoItem Todo { get; }
    public ObservableCollection<PersistedAgentConnection> Connections { get; }
    public ObservableCollection<AgentProjectTarget> Targets { get; } = new();
    public IAsyncRelayCommand ConfirmCommand { get; }

    public AgentDispatchResult? LastResult { get; private set; }
    public event Action<AgentDispatchResult>? DispatchCompleted;

    [ObservableProperty]
    private PersistedAgentConnection? _selectedConnection;

    [ObservableProperty]
    private AgentProjectTarget? _selectedTarget;

    public bool CanConfirm => SelectedConnection is { Enabled: true, Capabilities.CanCreateTask: true } && SelectedTarget is not null;

    partial void OnSelectedConnectionChanged(PersistedAgentConnection? value)
    {
        RefreshTargets();
        OnPropertyChanged(nameof(CanConfirm));
        ConfirmCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedTargetChanged(AgentProjectTarget? value)
    {
        OnPropertyChanged(nameof(CanConfirm));
        ConfirmCommand.NotifyCanExecuteChanged();
    }

    private async Task ConfirmAsync()
    {
        if (_dispatchService is null || SelectedConnection is null || SelectedTarget is null || !CanConfirm)
        {
            return;
        }

        LastResult = await _dispatchService.DispatchAsync(
            Todo,
            SelectedConnection.SourceType,
            SelectedTarget.TargetId,
            confirmed: true);
        OnPropertyChanged(nameof(LastResult));
        DispatchCompleted?.Invoke(LastResult);
    }

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
