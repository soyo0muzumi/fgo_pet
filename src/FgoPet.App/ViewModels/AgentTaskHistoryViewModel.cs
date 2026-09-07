using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FgoPet.Core.Agents;
using FgoPet.Infrastructure.Persistence;

namespace FgoPet.App.ViewModels;

public enum AgentTaskHistoryFilter { All, Active, Attention, Completed, Failed, Cancelled }

public sealed partial class AgentTaskHistoryViewModel : ObservableObject
{
    private readonly SqliteAgentRepository _repository;
    private readonly TimeProvider _time;

    public AgentTaskHistoryViewModel(SqliteAgentRepository repository, TimeProvider time)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        RefreshCommand = new RelayCommand(Refresh);
    }

    public ObservableCollection<AgentExecutionViewModel> Items { get; } = new();
    public IRelayCommand RefreshCommand { get; }

    [ObservableProperty]
    private AgentTaskHistoryFilter _filter = AgentTaskHistoryFilter.All;

    public void Refresh()
    {
        IReadOnlyList<AgentExecution> executions;
        try
        {
            executions = _repository.ListNonTerminalExecutions()
            .Concat(_repository.ListTerminalExecutions(_time.GetUtcNow().AddYears(1), 500))
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderByDescending(item => item.UpdatedAt)
            .Where(MatchesFilter)
            .ToArray();
        }
        catch (Exception)
        {
            Items.Clear();
            return;
        }
        Items.Clear();
        foreach (var item in executions.Select(item => new AgentExecutionViewModel(item))) Items.Add(item);
    }

    partial void OnFilterChanged(AgentTaskHistoryFilter value) => Refresh();

    private bool MatchesFilter(AgentExecution item) => Filter switch
    {
        AgentTaskHistoryFilter.Active => item.Status is AgentExecutionStatus.Dispatching or AgentExecutionStatus.Active,
        AgentTaskHistoryFilter.Attention => item.Status is AgentExecutionStatus.Attention or AgentExecutionStatus.DispatchOutcomeUnknown,
        AgentTaskHistoryFilter.Completed => item.Status == AgentExecutionStatus.Completed,
        AgentTaskHistoryFilter.Failed => item.Status == AgentExecutionStatus.Failed,
        AgentTaskHistoryFilter.Cancelled => item.Status == AgentExecutionStatus.Cancelled,
        _ => true,
    };
}
