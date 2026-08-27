using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using FgoPet.Core.Panels;

namespace FgoPet.App.Panels;

/// <summary>
/// Bounded collapsible attached panel state and lists. Dialogue keeps at most 20 items
/// and presents about 6; Todo shows 8 rows and scrolls the overflow. Startup is always
/// <see cref="AttachedPanelState.Collapsed"/>.
/// </summary>
public sealed partial class AttachedPanelViewModel : ObservableObject
{
    public const int DialogueCapacity = 20;
    public const int DialogueVisible = 6;
    public const int TodoVisibleRows = 8;

    private readonly TimeProvider _time;
    private DateTimeOffset _lastInteraction;
    private bool _pointerInside;
    private TimeSpan _idleTimeout = TimeSpan.FromSeconds(30);

    public AttachedPanelViewModel(TimeProvider time)
    {
        _time = time;
        _lastInteraction = time.GetUtcNow();
    }

    [ObservableProperty]
    private AttachedPanelState _state = AttachedPanelState.Collapsed;

    [ObservableProperty]
    private bool _autoCollapseEnabled = true;

    public ObservableCollection<DialogueItemViewModel> Dialogue { get; } = new();

    public ObservableCollection<TodoItemViewModel> Todo { get; } = new();

    public int VisibleDialogueCount => Math.Min(Dialogue.Count, DialogueVisible);

    public int VisibleTodoCount => Math.Min(Todo.Count, TodoVisibleRows);

    public bool TodoOverflows => Todo.Count > TodoVisibleRows;

    public void PortraitClick()
    {
        Interact();
        State = AttachedPanelStateMachine.Transition(State, PanelAction.PortraitClick);
    }

    public void DialogueClick()
    {
        Interact();
        State = AttachedPanelStateMachine.Transition(State, PanelAction.DialogueClick);
    }

    public void TodoClick()
    {
        Interact();
        State = AttachedPanelStateMachine.Transition(State, PanelAction.TodoClick);
    }

    public void Escape()
    {
        Interact();
        State = AttachedPanelStateMachine.Transition(State, PanelAction.Escape);
    }

    public void Collapse()
    {
        Interact();
        State = AttachedPanelStateMachine.Transition(State, PanelAction.Collapse);
    }

    public void AddDialogue(string text)
    {
        Interact();
        Dialogue.Add(new DialogueItemViewModel(text));
        while (Dialogue.Count > DialogueCapacity)
        {
            Dialogue.RemoveAt(0);
        }

        OnPropertyChanged(nameof(VisibleDialogueCount));
    }

    public void AddTodo(string text)
    {
        Interact();
        Todo.Add(new TodoItemViewModel(text));
        OnPropertyChanged(nameof(VisibleTodoCount));
        OnPropertyChanged(nameof(TodoOverflows));
    }

    public void PointerEntered() => _pointerInside = true;

    public void PointerLeft() => _pointerInside = false;

    /// <summary>Periodic tick that applies the 30-second idle collapse.</summary>
    public void Tick()
    {
        State = AttachedPanelStateMachine.ApplyIdle(
            State,
            _time,
            _lastInteraction,
            _idleTimeout,
            AutoCollapseEnabled,
            !_pointerInside);
    }

    private void Interact() => _lastInteraction = _time.GetUtcNow();
}