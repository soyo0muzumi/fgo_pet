using CommunityToolkit.Mvvm.ComponentModel;
using FgoPet.App.Dialogue;
using FgoPet.Core.Todo;

namespace FgoPet.App.ViewModels;

public sealed partial class TodoProposalViewModel : ObservableObject
{
    private readonly TodoProposalService _service;

    public TodoProposalViewModel(TodoProposal proposal, TodoProposalService service)
    {
        Proposal = proposal ?? throw new ArgumentNullException(nameof(proposal));
        _service = service ?? throw new ArgumentNullException(nameof(service));
        Title = proposal.Title;
        Description = proposal.Description ?? string.Empty;
        Priority = proposal.Priority;
        DueAt = proposal.DueAt;
    }

    public TodoProposal Proposal { get; }

    [ObservableProperty]
    private string _title;

    [ObservableProperty]
    private string _description;

    [ObservableProperty]
    private TodoPriority _priority;

    [ObservableProperty]
    private DateTimeOffset? _dueAt;

    [ObservableProperty]
    private bool _isRemoved;

    [ObservableProperty]
    private string _errorText = string.Empty;

    public event Action<TodoProposalViewModel>? Closed;

    public TodoItem Confirm()
    {
        if (IsRemoved)
        {
            throw new InvalidOperationException("This Todo proposal has been removed.");
        }

        var todo = _service.Confirm(new TodoProposal(Title, Description, Priority, DueAt));
        ErrorText = string.Empty;
        Closed?.Invoke(this);
        return todo;
    }

    public void Remove()
    {
        if (IsRemoved)
        {
            return;
        }

        IsRemoved = true;
        Closed?.Invoke(this);
    }
}
