using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using FgoPet.Core.Todo;

namespace FgoPet.Plugin.Todo;

public sealed class TodoEditorSession : INotifyPropertyChanged
{
    private const int MaxSteps = 20;
    private TodoItem _item;
    private bool _canEdit;
    private bool _isHighlighted;
    private string _draftTitle = string.Empty;
    private string _draftDescription = string.Empty;
    private bool _isEditing;
    private bool _isExpanded;
    private bool _isAddingStep;
    private bool _isConfirmingCompletion;
    private string _newStepDraft = string.Empty;
    private TodoItem? _newStepOriginal;
    private string? _newStepId;
    private TodoItem? _completionOriginal;
    private TodoItem? _editOriginal;
    private readonly ObservableCollection<TodoStepEditor> _steps = new();

    public TodoEditorSession(TodoItem item, bool canEdit, bool isHighlighted)
    {
        _item = item;
        _canEdit = canEdit;
        _isHighlighted = isHighlighted;
        ReconcileSteps(item.Steps);
    }

    public TodoItem Item { get => _item; private set => Set(ref _item, value); }
    public bool CanEdit { get => _canEdit; private set => Set(ref _canEdit, value); }
    public bool IsHighlighted { get => _isHighlighted; private set => Set(ref _isHighlighted, value); }
    public string DraftTitle { get => _draftTitle; set => SetDraft(ref _draftTitle, value); }
    public string DraftDescription { get => _draftDescription; set => SetDraft(ref _draftDescription, value); }
    public bool IsEditing { get => _isEditing; private set => Set(ref _isEditing, value); }
    public bool IsExpanded { get => _isExpanded; set => Set(ref _isExpanded, value); }
    public bool IsAddingStep { get => _isAddingStep; private set => Set(ref _isAddingStep, value); }
    public bool IsConfirmingCompletion { get => _isConfirmingCompletion; private set => Set(ref _isConfirmingCompletion, value); }
    public string NewStepDraft { get => _newStepDraft; set => SetNewStepDraft(value); }
    public bool CanSaveNewStep => IsAddingStep && CanEditSteps && !string.IsNullOrWhiteSpace(NewStepDraft);
    public TodoItem? NewStepOriginal => _newStepOriginal;
    public string? NewStepId => _newStepId;
    public TodoItem? CompletionOriginal => _completionOriginal;
    public bool CanAddStep => CanEditSteps && !IsEditing && !IsAddingStep && _steps.Count < MaxSteps;
    public bool CanSaveDraft => IsEditing && !string.IsNullOrWhiteSpace(DraftTitle);
    public TodoItem? EditOriginal => _editOriginal;
    public bool IsCompleted => Item.Status == TodoStatus.Completed;
    public bool CanComplete => CanEdit && !IsConfirmingCompletion;
    public bool CanEditContent => !IsCompleted && CanEdit && !IsConfirmingCompletion;
    public bool CanEditSteps => !IsCompleted && CanEdit && !IsConfirmingCompletion;
    public bool HasSteps => _steps.Count > 0;
    public bool HasDescription => !string.IsNullOrWhiteSpace(Item.Description);
    public string StepProgressText => HasSteps
        ? $"{_steps.Count(step => step.Item.IsCompleted)} / {_steps.Count} 步"
        : string.Empty;
    public System.Collections.IEnumerable Steps => _steps;
    public int StepCount => _steps.Count;
    public int StepIndex(string id)
    {
        for (var index = 0; index < _steps.Count; index++)
            if (string.Equals(_steps[index].Item.Id, id, StringComparison.Ordinal)) return index;
        return -1;
    }
    public string CompletionLabel => (IsCompleted ? "恢复待办：" : "完成待办：") + Item.Title;
    public int IncompleteStepCount => _steps.Count(step => !step.Item.IsCompleted);
    public string CompletionConfirmationText => $"还有 {IncompleteStepCount} 步未完成，仍要完成任务吗？";

    public void Update(TodoItem item, bool canEdit, bool isHighlighted)
    {
        Item = item;
        ReconcileSteps(item.Steps);
        CanEdit = canEdit;
        IsHighlighted = isHighlighted;
        foreach (var step in _steps) step.RefreshState();
        OnPropertyChanged(nameof(IsCompleted));
        OnPropertyChanged(nameof(CanComplete));
        OnPropertyChanged(nameof(CanEditContent));
        OnPropertyChanged(nameof(CanEditSteps));
        OnPropertyChanged(nameof(IsConfirmingCompletion));
        OnPropertyChanged(nameof(IncompleteStepCount));
        OnPropertyChanged(nameof(CompletionConfirmationText));
        OnPropertyChanged(nameof(CanAddStep));
        OnPropertyChanged(nameof(CanSaveNewStep));
        OnPropertyChanged(nameof(HasSteps));
        OnPropertyChanged(nameof(HasDescription));
        OnPropertyChanged(nameof(StepProgressText));
        OnPropertyChanged(nameof(CompletionLabel));
    }

    private void ReconcileSteps(IReadOnlyList<TodoStep> source)
    {
        var existingById = _steps.ToDictionary(step => step.Item.Id, StringComparer.Ordinal);
        var desired = source.OrderBy(step => step.Order).ToArray();
        for (var index = 0; index < desired.Length; index++)
        {
            var item = desired[index];
            if (!existingById.TryGetValue(item.Id, out var step))
            {
                step = new TodoStepEditor(this, item);
                _steps.Insert(index, step);
            }
            else
            {
                step.Update(item);
                var currentIndex = _steps.IndexOf(step);
                if (currentIndex != index) _steps.Move(currentIndex, index);
            }
        }

        while (_steps.Count > desired.Length)
        {
            _steps.RemoveAt(_steps.Count - 1);
        }
    }

    public void BeginEditing()
    {
        _editOriginal = Item;
        DraftTitle = Item.Title;
        DraftDescription = Item.Description ?? string.Empty;
        IsEditing = true;
        OnPropertyChanged(nameof(CanSaveDraft));
        OnPropertyChanged(nameof(CanAddStep));
    }

    public void BeginAddingStep()
    {
        _newStepOriginal = Item;
        _newStepId = null;
        NewStepDraft = string.Empty;
        IsAddingStep = true;
        foreach (var step in _steps) step.RefreshState();
        OnPropertyChanged(nameof(CanAddStep));
        OnPropertyChanged(nameof(CanSaveNewStep));
    }

    public void AssignNewStepIdIfNeeded()
    {
        _newStepId ??= "step-" + Guid.NewGuid().ToString("N");
    }

    public void EndAddingStep()
    {
        IsAddingStep = false;
        _newStepOriginal = null;
        _newStepId = null;
        NewStepDraft = string.Empty;
        foreach (var step in _steps) step.RefreshState();
        OnPropertyChanged(nameof(CanAddStep));
        OnPropertyChanged(nameof(CanSaveNewStep));
    }

    private void SetNewStepDraft(string? value)
    {
        value ??= string.Empty;
        if (EqualityComparer<string>.Default.Equals(_newStepDraft, value)) return;
        _newStepDraft = value;
        OnPropertyChanged(nameof(NewStepDraft));
        OnPropertyChanged(nameof(CanSaveNewStep));
    }


    public void EndEditing()
    {
        IsEditing = false;
        _editOriginal = null;
        OnPropertyChanged(nameof(CanSaveDraft));
        OnPropertyChanged(nameof(CanAddStep));
    }

    public void BeginCompletionConfirmation()
    {
        _completionOriginal = Item;
        IsConfirmingCompletion = true;
        IsExpanded = true;
        foreach (var step in _steps) step.RefreshState();
        OnPropertyChanged(nameof(CanComplete));
        OnPropertyChanged(nameof(CanEditSteps));
    }

    public void EndCompletionConfirmation()
    {
        IsConfirmingCompletion = false;
        _completionOriginal = null;
        foreach (var step in _steps) step.RefreshState();
        OnPropertyChanged(nameof(CanComplete));
        OnPropertyChanged(nameof(CanEditSteps));
    }

    private void SetDraft(ref string field, string? value, [CallerMemberName] string? propertyName = null)
    {
        value ??= string.Empty;
        if (EqualityComparer<string>.Default.Equals(field, value)) return;
        field = value;
        OnPropertyChanged(propertyName);
        OnPropertyChanged(nameof(CanSaveDraft));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnPropertyChanged(propertyName);
    }
}
