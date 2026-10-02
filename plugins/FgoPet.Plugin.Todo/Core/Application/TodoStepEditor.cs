using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using FgoPet.Core.Todo;

namespace FgoPet.Plugin.Todo;

public sealed class TodoStepEditor : INotifyPropertyChanged
{
    private TodoStep _item;
    private string _draftTitle;
    private bool _isEditing;
    private bool _isConfirmingDelete;

    public TodoStepEditor(TodoEditorSession parent, TodoStep item)
    {
        Parent = parent;
        _item = item;
        _draftTitle = item.Title;
    }

    public TodoEditorSession Parent { get; }
    public TodoStep Item { get => _item; private set => Set(ref _item, value); }
    public string DraftTitle
    {
        get => _draftTitle;
        set
        {
            Set(ref _draftTitle, value ?? string.Empty);
            OnPropertyChanged(nameof(CanSaveTitle));
        }
    }
    public bool IsEditing { get => _isEditing; private set => Set(ref _isEditing, value); }
    public bool IsConfirmingDelete { get => _isConfirmingDelete; private set => Set(ref _isConfirmingDelete, value); }
    public bool CanEditSteps => Parent.CanEditSteps && !Parent.IsEditing && !Parent.IsAddingStep && !IsConfirmingDelete;
    public bool CanDelete => CanEditSteps && !IsEditing && !IsConfirmingDelete;
    public bool CanMoveUp => CanReorder && Parent.StepIndex(Item.Id) > 0;
    public bool CanMoveDown => CanReorder && Parent.StepIndex(Item.Id) >= 0 && Parent.StepIndex(Item.Id) < Parent.StepCount - 1;
    public bool CanReorder => CanEditSteps && !IsEditing && !IsConfirmingDelete;
    public bool CanSaveTitle => IsEditing && CanEditSteps && !string.IsNullOrWhiteSpace(DraftTitle);
    public TodoItem? EditOriginal { get; private set; }
    public string CompletionLabel => (Item.IsCompleted ? "已完成步骤：" : "未完成步骤：") + Item.Title;

    public void Update(TodoStep item)
    {
        Item = item;
        if (!IsEditing) DraftTitle = item.Title;
        OnPropertyChanged(nameof(CanEditSteps));
        OnPropertyChanged(nameof(CanDelete));
        OnPropertyChanged(nameof(CanMoveUp));
        OnPropertyChanged(nameof(CanMoveDown));
        OnPropertyChanged(nameof(CanReorder));
        OnPropertyChanged(nameof(CanSaveTitle));
        OnPropertyChanged(nameof(CompletionLabel));
    }

    public void RefreshState()
    {
        OnPropertyChanged(nameof(CanEditSteps));
        OnPropertyChanged(nameof(CanDelete));
        OnPropertyChanged(nameof(CanMoveUp));
        OnPropertyChanged(nameof(CanMoveDown));
        OnPropertyChanged(nameof(CanReorder));
        OnPropertyChanged(nameof(CanSaveTitle));
    }

    public void BeginEditing()
    {
        EditOriginal = Parent.Item;
        DraftTitle = Item.Title;
        IsEditing = true;
        IsConfirmingDelete = false;
        OnPropertyChanged(nameof(CanSaveTitle));
    }

    public void EndEditing()
    {
        IsEditing = false;
        EditOriginal = null;
        DraftTitle = Item.Title;
        OnPropertyChanged(nameof(CanSaveTitle));
    }

    public void BeginDeleteConfirmation()
    {
        IsConfirmingDelete = true;
        OnPropertyChanged(nameof(CanEditSteps));
        OnPropertyChanged(nameof(CanDelete));
    }

    public void CancelDeleteConfirmation()
    {
        IsConfirmingDelete = false;
        OnPropertyChanged(nameof(CanEditSteps));
        OnPropertyChanged(nameof(CanDelete));
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
