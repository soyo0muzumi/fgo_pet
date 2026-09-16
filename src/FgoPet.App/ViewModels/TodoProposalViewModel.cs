using CommunityToolkit.Mvvm.ComponentModel;
using FgoPet.App.Dialogue;
using FgoPet.Core.Todo;

namespace FgoPet.App.ViewModels;

public sealed partial class TodoProposalViewModel : ObservableObject
{
    private readonly TodoProposalService _service;
    private TodoItem? _createdTodo;
    public bool IsAdded => _createdTodo is not null;
    public string? CreatedTodoId => _createdTodo?.Id;

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

    [ObservableProperty]
    private bool _isExpanded;

    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);

    public string DescriptionPreview => Description.Length <= 180
        ? Description
        : Description[..180].TrimEnd() + "…";

    partial void OnDescriptionChanged(string value)
    {
        OnPropertyChanged(nameof(HasDescription));
        OnPropertyChanged(nameof(DescriptionPreview));
    }

    public event Action<TodoProposalViewModel>? Closed;

    public TodoItem Confirm()
    {
        if (_createdTodo is not null) return _createdTodo;
        if (IsRemoved)
        {
            throw new InvalidOperationException("This Todo proposal has been removed.");
        }

        try
        {
            var todo = _service.Confirm(new TodoProposal(Title, Description, Priority, DueAt, Proposal.StepTitles));
            _createdTodo = todo;
            IsExpanded = false;
            OnPropertyChanged(nameof(IsAdded));
            OnPropertyChanged(nameof(CreatedTodoId));
            ErrorText = string.Empty;
            Closed?.Invoke(this);
            return todo;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or
            System.IO.IOException or UnauthorizedAccessException or System.Data.Common.DbException or KeyNotFoundException)
        {
            ErrorText = "添加失败，请检查标题和内容，或稍后重试；草稿已保留。";
            throw;
        }
    }

    public void Remove()
    {
        if (_createdTodo is not null) return;
        if (IsRemoved)
        {
            return;
        }

        IsRemoved = true;
        Closed?.Invoke(this);
    }
}
