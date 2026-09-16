using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using FgoPet.App.Services;
using FgoPet.Core.Todo;
using FgoPet.Core.Agents;

namespace FgoPet.App.Views;

public partial class TodoWorkspaceView : UserControl
{
    private const int MaxSteps = 20;
    private readonly TodoApplicationService _service;
    private readonly IAgentRepository? _agents;
    private string? _editingId;
    private Row? _editingRow;
    private Row.StepRow? _editingStep;
    private Row? _addingStepRow;
    private string? _savingRowId;
    private string? _savingStepsRowId;
    private bool _stepMoveActivationPending;
    private bool _saveActivationPending;
    private bool _suppressEmptySaveActivation;
    private bool _composing;
    private bool _refreshPending;
    private bool _focusPending;
    private int _focusAttempt;
    private string? _focusTodoId;
    private string? _highlightedTodoId;
    private readonly HashSet<string> _sessionCompletedIds = new(StringComparer.Ordinal);
    private readonly ObservableCollection<Row> _activeRows = new();
    private readonly ObservableCollection<Row> _historyRows = new();
    private TodoItem? _undo;
    private readonly DispatcherTimer _undoTimer = new() { Interval = TimeSpan.FromSeconds(8) };
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly DispatcherTimer _highlightTimer = new() { Interval = TimeSpan.FromSeconds(3) };

    public TodoWorkspaceView(TodoApplicationService service, IAgentRepository? agents = null,
        FgoPet.App.ViewModels.AgentCurrentTaskViewModel? currentTask = null)
    {
        _service = service;
        _agents = agents;
        _ = currentTask; // The legacy execution strip is intentionally no longer rendered here.
        InitializeComponent();
        AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(OnPreviewKeyDown), handledEventsToo: true);
        // Handle both the initial quick-add controls and the dynamically-created
        // row/step editors.  IME composition must guard every editor uniformly.
        TextCompositionManager.AddPreviewTextInputStartHandler(this, OnTextInputStart);
        TextCompositionManager.AddPreviewTextInputHandler(this, OnTextInput);
        Loaded += (_, _) => { _service.Changed += OnChanged; Refresh(); };
        Unloaded += (_, _) =>
        {
            _service.Changed -= OnChanged;
            _undoTimer.Stop();
            _statusTimer.Stop();
            _highlightTimer.Stop();
            StatusText.Text = string.Empty;
            _undo = null;
            _sessionCompletedIds.Clear();
            _activeRows.Clear();
            _historyRows.Clear();
            _focusTodoId = null;
            _highlightedTodoId = null;
            _editingRow = null;
            _editingStep = null;
            _addingStepRow = null;
            _savingRowId = null;
            _savingStepsRowId = null;
            UndoButton.Visibility = Visibility.Collapsed;
        };
        _undoTimer.Tick += (_, _) => { _undoTimer.Stop(); _undo = null; UndoButton.Visibility = Visibility.Collapsed; };
        _statusTimer.Tick += (_, _) => { _statusTimer.Stop(); StatusText.Text = string.Empty; };
        _highlightTimer.Tick += (_, _) =>
        {
            _highlightTimer.Stop();
            _highlightedTodoId = null;
            Refresh();
        };
    }

    private sealed class Row : INotifyPropertyChanged
    {
        private TodoItem _item;
        private bool _canEdit;
        private string? _executionStatus;
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
        private readonly ObservableCollection<StepRow> _steps = new();

        public Row(TodoItem item, bool canEdit, string? executionStatus, bool isHighlighted)
        {
            _item = item;
            _canEdit = canEdit;
            _executionStatus = executionStatus;
            _isHighlighted = isHighlighted;
            ReconcileSteps(item.Steps);
        }

        public TodoItem Item { get => _item; private set => Set(ref _item, value); }
        public bool CanEdit { get => _canEdit; private set => Set(ref _canEdit, value); }
        public string? ExecutionStatus { get => _executionStatus; private set => Set(ref _executionStatus, value); }
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
        public int StepIndex(string id) => _steps
            .Select((step, index) => (step.Item.Id, index))
            .FirstOrDefault(value => string.Equals(value.Id, id, StringComparison.Ordinal)).index;
        public bool HasExecution => ExecutionStatus is not null;
        public string CompletionLabel => (IsCompleted ? "恢复待办：" : "完成待办：") + Item.Title;
        public int IncompleteStepCount => _steps.Count(step => !step.Item.IsCompleted);
        public string CompletionConfirmationText => $"还有 {IncompleteStepCount} 步未完成，仍要完成任务吗？";

        public void Update(TodoItem item, bool canEdit, string? executionStatus, bool isHighlighted)
        {
            Item = item;
            ReconcileSteps(item.Steps);
            CanEdit = canEdit;
            ExecutionStatus = executionStatus;
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
            OnPropertyChanged(nameof(HasExecution));
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
                    step = new StepRow(this, item);
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

        internal sealed class StepRow : INotifyPropertyChanged
        {
            private TodoStep _item;
            private string _draftTitle;
            private bool _isEditing;
            private bool _isConfirmingDelete;

            public StepRow(Row parent, TodoStep item)
            {
                Parent = parent;
                _item = item;
                _draftTitle = item.Title;
            }

            public Row Parent { get; }
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

    private Row MakeRow(TodoItem item, Row? existing = null)
    {
        var execution = _agents?.GetLatestExecutionForTodo(item.Id);
        var canEdit = !_service.IsProtected(item);
        var executionStatus = execution is null ? null : new FgoPet.App.ViewModels.AgentExecutionViewModel(execution).StatusText;
        if (existing is null) return new Row(item, canEdit, executionStatus, item.Id == _highlightedTodoId);
        existing.Update(item, canEdit, executionStatus, item.Id == _highlightedTodoId);
        return existing;
    }

    public void Refresh()
    {
        try
        {
            var activeItems = _service.ListActive();
            var historyItems = _service.ListHistory();
            var historyIds = historyItems.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
            _sessionCompletedIds.RemoveWhere(id => !historyIds.Contains(id));
            if (_highlightedTodoId is not null && !activeItems.Concat(historyItems).Any(item => item.Id == _highlightedTodoId))
                _highlightedTodoId = null;
            var retained = historyItems.Where(item => _sessionCompletedIds.Contains(item.Id));
            ReconcileRows(_activeRows, activeItems.Concat(retained));
            ReconcileRows(_historyRows, historyItems.Where(item => !_sessionCompletedIds.Contains(item.Id)));
            ActiveItems.ItemsSource = _activeRows;
            CompletedItems.ItemsSource = _historyRows;
            EmptyText.Visibility = _activeRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (_focusTodoId is not null) QueueFocusAttempt();
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            // Never leave an old, possibly protected snapshot actionable after a failed reread.
            _sessionCompletedIds.Clear();
            _activeRows.Clear();
            _historyRows.Clear();
            EmptyText.Visibility = Visibility.Collapsed;
            SetStatus("读取待办失败，请稍后重新打开。");
        }
    }

    /// <summary>Starts a fresh visible-page session without recreating the view.</summary>
    public void EnterView()
    {
        _sessionCompletedIds.Clear();
        _statusTimer.Stop();
        StatusText.Text = string.Empty;
        _highlightedTodoId = null;
        _highlightTimer.Stop();
        Refresh();
    }

    private void ReconcileRows(ObservableCollection<Row> rows, IEnumerable<TodoItem> source)
    {
        var items = source.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var desiredIds = rows.Select(row => row.Item.Id).Where(items.ContainsKey).ToList();
        desiredIds.AddRange(items.Keys.Where(id => !desiredIds.Contains(id)));
        for (var index = 0; index < desiredIds.Count; index++)
        {
            var id = desiredIds[index];
            var row = rows.FirstOrDefault(candidate => candidate.Item.Id == id);
            if (row is null)
            {
                rows.Insert(index, MakeRow(items[id]));
            }
            else
            {
                MakeRow(items[id], row);
                var currentIndex = rows.IndexOf(row);
                if (currentIndex != index) rows.Move(currentIndex, index);
            }
        }
        while (rows.Count > desiredIds.Count) rows.RemoveAt(rows.Count - 1);
    }

    private void OnChanged()
    {
        if (_refreshPending) return;
        _refreshPending = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _refreshPending = false;
            Refresh();
        }), DispatcherPriority.DataBind);
    }

    public void FocusTodo(string? id)
    {
        if (string.IsNullOrEmpty(id)) return;
        _focusTodoId = id;
        _focusAttempt = 0;
        QueueFocusAttempt();
    }

    private void QueueFocusAttempt()
    {
        if (_focusPending || _focusTodoId is null) return;
        _focusPending = true;
        Dispatcher.BeginInvoke(new Action(TryFocusTodo), DispatcherPriority.Loaded);
    }

    private void TryFocusTodo()
    {
        _focusPending = false;
        var id = _focusTodoId;
        if (id is null) return;

        UpdateLayout();
        var list = _activeRows.FirstOrDefault(row => row.Item.Id == id) is not null
            ? ActiveItems : CompletedItems;
        var row = list.Items.Cast<Row>().FirstOrDefault(candidate => candidate.Item.Id == id);
        if (row is null)
        {
            if (_refreshPending || _focusAttempt++ < 2) { QueueFocusAttempt(); return; }
            _focusTodoId = null;
            SetStatus("这条待办已不存在。");
            return;
        }

        if (list == CompletedItems) CompletedSection.IsExpanded = true;
        UpdateLayout();
        if (list.ItemContainerGenerator.ContainerFromItem(row) is not FrameworkElement container)
        {
            if (_focusAttempt++ < 2) { QueueFocusAttempt(); return; }
            _focusTodoId = null;
            SetStatus("这条待办暂时无法定位。");
            return;
        }

        container.BringIntoView();
        container.Focusable = true;
        container.Focus();
    }
    public void BeginAdd()
    {
        if (_editingRow is not null || _editingStep is not null || _addingStepRow is not null)
        {
            SetStatus("请先保存或取消当前编辑。");
            if (_editingRow is not null) FocusRow(_editingRow);
            else if (_editingStep is not null) FocusStepEditor(_editingStep);
            else FocusNewStepEditor(_addingStepRow!);
            return;
        }
        QuickAddEditor.Visibility = Visibility.Visible;
        TitleInput.Focus();
    }
    private void OnBeginAdd(object sender, RoutedEventArgs e) => BeginAdd();
    private void OnEdit(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Row row }) return;
        if (_editingRow is not null || _editingStep is not null || _addingStepRow is not null)
        {
            SetStatus("请先保存或取消当前编辑。");
            if (_editingRow is not null) FocusRow(_editingRow);
            else if (_editingStep is not null) FocusStepEditor(_editingStep);
            else FocusNewStepEditor(_addingStepRow!);
            return;
        }
        if (QuickAddEditor.Visibility == Visibility.Visible)
        {
            SetStatus("请先保存或取消新增草稿。");
            TitleInput.Focus();
            return;
        }

        _editingRow = row;
        row.BeginEditing();
        FocusRowEditor(row);
    }

    private void OnBeginStepEdit(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Row.StepRow step }) return;
        if (_editingRow is not null || _editingStep is not null || _addingStepRow is not null || QuickAddEditor.Visibility == Visibility.Visible)
        {
            SetStatus("请先保存或取消当前编辑。");
            if (_editingRow is not null) FocusRow(_editingRow);
            else if (_editingStep is not null) FocusStepEditor(_editingStep);
            else if (_addingStepRow is not null) FocusNewStepEditor(_addingStepRow);
            else TitleInput.Focus();
            e.Handled = true;
            return;
        }
        if (!step.CanEditSteps)
        {
            SetStatus(step.Parent.IsCompleted ? "已完成任务需先恢复后编辑。" : "任务当前受保护，请先核对。");
            e.Handled = true;
            return;
        }

        _editingStep = step;
        step.BeginEditing();
        FocusStepEditor(step);
        e.Handled = true;
    }

    private void OnSaveStepEdit(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Row.StepRow step }) return;
        SaveStepEdit(step);
        e.Handled = true;
    }

    private void OnCancelStepEdit(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Row.StepRow step }) return;
        CancelStepEdit(step);
        e.Handled = true;
    }

    private void OnBeginDeleteStep(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Row.StepRow step }) return;
        if (_editingRow is not null || _editingStep is not null || _addingStepRow is not null || QuickAddEditor.Visibility == Visibility.Visible)
        {
            SetStatus("请先保存或取消当前编辑。");
            e.Handled = true;
            return;
        }
        if (!step.CanDelete)
        {
            SetStatus(step.Parent.IsCompleted ? "已完成任务需先恢复后编辑。" : "任务当前受保护，请先核对。");
            e.Handled = true;
            return;
        }

        step.BeginDeleteConfirmation();
        FocusStepDeleteCancel(step);
        e.Handled = true;
    }

    private void OnConfirmDeleteStep(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Row.StepRow step } || !step.IsConfirmingDelete) return;
        var row = step.Parent;
        var expected = row.Item;
        var ordered = expected.Steps.OrderBy(item => item.Order).ToArray();
        var targetIndex = Array.FindIndex(ordered, item => item.Id == step.Item.Id);
        if (targetIndex < 0) return;
        var focusId = targetIndex + 1 < ordered.Length
            ? ordered[targetIndex + 1].Id
            : targetIndex > 0 ? ordered[targetIndex - 1].Id : null;
        var remaining = ordered.Where(item => item.Id != step.Item.Id)
            .Select((item, index) => new TodoStep(item.Id, item.Title, index, item.IsCompleted))
            .ToArray();

        if (!TrySaveSteps(row, expected, remaining))
        {
            e.Handled = true;
            return;
        }

        step.CancelDeleteConfirmation();
        SetStatus("已删除步骤");
        Refresh();
        FocusStepButtonById(row, focusId);
        e.Handled = true;
    }

    private void OnCancelDeleteStep(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Row.StepRow step } || !step.IsConfirmingDelete) return;
        CancelStepDelete(step);
        e.Handled = true;
    }

    private void CancelStepDelete(Row.StepRow step)
    {
        if (!step.IsConfirmingDelete) return;
        step.CancelDeleteConfirmation();
        SetStatus("已取消删除步骤");
        Refresh();
        FocusStepMoreButton(step);
    }

    private void OnMoveStepUp(object sender, RoutedEventArgs e)
    {
        MoveStep(sender, -1);
        e.Handled = true;
    }

    private void OnMoveStepDown(object sender, RoutedEventArgs e)
    {
        MoveStep(sender, 1);
        e.Handled = true;
    }

    private void MoveStep(object sender, int delta)
    {
        if (sender is not FrameworkElement { Tag: Row.StepRow step }) return;
        if (_stepMoveActivationPending) return;
        if (_editingRow is not null || _editingStep is not null || _addingStepRow is not null || QuickAddEditor.Visibility == Visibility.Visible)
        {
            SetStatus("请先保存或取消当前编辑。");
            return;
        }
        if (!step.CanReorder)
        {
            SetStatus(step.Parent.IsCompleted ? "已完成任务需先恢复后编辑。" : "任务当前受保护，请先核对。");
            return;
        }

        var row = step.Parent;
        var expected = row.Item;
        var ordered = expected.Steps.OrderBy(item => item.Order).ToArray();
        var index = Array.FindIndex(ordered, item => item.Id == step.Item.Id);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= ordered.Length)
        {
            SetStatus(delta < 0 ? "已经是第一步。" : "已经是最后一步。");
            return;
        }

        _stepMoveActivationPending = true;
        Dispatcher.BeginInvoke(new Action(() => _stepMoveActivationPending = false), DispatcherPriority.Background);
        (ordered[index], ordered[target]) = (ordered[target], ordered[index]);
        var reordered = ordered
            .Select((item, order) => new TodoStep(item.Id, item.Title, order, item.IsCompleted))
            .ToArray();
        if (!TrySaveSteps(row, expected, reordered)) return;

        SetStatus("已保存");
        Refresh();
        FocusStepButtonById(row, step.Item.Id);
    }

    private void SaveStepEdit(Row.StepRow step)
    {
        if (!ReferenceEquals(_editingStep, step) || step.EditOriginal is null) return;
        if (!step.CanSaveTitle)
        {
            SetStatus("请输入步骤标题；草稿已保留。");
            FocusStepEditor(step);
            return;
        }

        var expected = step.EditOriginal;
        var updatedSteps = expected.Steps.Select(old => old.Id == step.Item.Id
            ? new TodoStep(old.Id, step.DraftTitle, old.Order, old.IsCompleted)
            : old).ToArray();
        if (!TrySaveSteps(step.Parent, expected, updatedSteps))
        {
            FocusStepEditor(step);
            return;
        }

        step.EndEditing();
        _editingStep = null;
        SetStatus("已保存");
        Refresh();
        FocusStepButton(step);
    }

    private void CancelStepEdit(Row.StepRow step)
    {
        if (!ReferenceEquals(_editingStep, step)) return;
        step.EndEditing();
        _editingStep = null;
        SetStatus("已取消步骤编辑");
        Refresh();
        FocusStepButton(step);
    }

    private void OnBeginAddStep(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Row row }) return;
        if (_addingStepRow is not null)
        {
            if (ReferenceEquals(_addingStepRow, row)) FocusNewStepEditor(row);
            else SetStatus("请先保存或取消当前步骤编辑。");
            e.Handled = true;
            return;
        }
        if (_editingRow is not null || _editingStep is not null || _addingStepRow is not null || QuickAddEditor.Visibility == Visibility.Visible)
        {
            SetStatus("请先保存或取消当前编辑。");
            if (_editingRow is not null) FocusRow(_editingRow);
            else if (_editingStep is not null) FocusStepEditor(_editingStep);
            else if (_addingStepRow is not null) FocusNewStepEditor(_addingStepRow);
            else TitleInput.Focus();
            e.Handled = true;
            return;
        }
        if (!row.CanEditSteps)
        {
            SetStatus(row.IsCompleted ? "已完成任务需先恢复后编辑。" : "任务当前受保护，请先核对。");
            e.Handled = true;
            return;
        }
        if (row.Item.Steps.Count >= MaxSteps)
        {
            SetStatus("步骤已达到 20 项上限。");
            e.Handled = true;
            return;
        }

        row.BeginAddingStep();
        _addingStepRow = row;
        FocusNewStepEditor(row);
        e.Handled = true;
    }

    private void OnSaveNewStep(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Row row }) return;
        SaveNewStep(row);
        e.Handled = true;
    }

    private void OnCancelNewStep(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Row row }) return;
        CancelNewStep(row);
        e.Handled = true;
    }

    private void SaveNewStep(Row row)
    {
        if (!ReferenceEquals(_addingStepRow, row) || row.NewStepOriginal is null) return;
        if (!row.CanSaveNewStep)
        {
            SetStatus("请输入步骤标题；草稿已保留。");
            FocusNewStepEditor(row);
            return;
        }

        var expected = row.NewStepOriginal;
        if (expected.Steps.Count >= MaxSteps)
        {
            SetStatus("步骤已达到 20 项上限；草稿已保留。");
            FocusNewStepEditor(row);
            return;
        }

        row.AssignNewStepIdIfNeeded();
        var steps = expected.Steps
            .Append(new TodoStep(row.NewStepId!, row.NewStepDraft, expected.Steps.Count, false))
            .ToArray();
        if (!TrySaveSteps(row, expected, steps))
        {
            FocusNewStepEditor(row);
            return;
        }

        row.EndAddingStep();
        _addingStepRow = null;
        SetStatus("已保存");
        Refresh();
        FocusAddStepButton(row);
    }

    private void CancelNewStep(Row row)
    {
        if (!ReferenceEquals(_addingStepRow, row)) return;
        row.EndAddingStep();
        _addingStepRow = null;
        SetStatus("已取消添加步骤");
        Refresh();
        FocusAddStepButton(row);
    }
    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (_saveActivationPending || (string.IsNullOrWhiteSpace(TitleInput.Text) && _suppressEmptySaveActivation))
        {
            UpdateSaveButtonState();
            return;
        }
        _saveActivationPending = true;
        UpdateSaveButtonState();
        try
        {
            TodoItem? created = null;
            if (_editingId is null)
            {
                created = _service.Create(TitleInput.Text, DescriptionInput.Text, TodoPriority.Normal, null);
            }
            else
            {
                _focusTodoId = null;
                _highlightedTodoId = null;
                _highlightTimer.Stop();
                _service.Update(_editingId, TitleInput.Text, DescriptionInput.Text);
            }
            ResetEditor();
            _suppressEmptySaveActivation = true;
            SetStatus("已保存"); AddTaskButton.Focus();
            if (created is not null)
            {
                _highlightedTodoId = created.Id;
                _highlightTimer.Stop();
                _highlightTimer.Start();
                FocusTodo(created.Id);
            }
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _saveActivationPending = false;
                UpdateSaveButtonState();
            }), DispatcherPriority.Background);
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            _saveActivationPending = false;
            UpdateSaveButtonState();
            SetStatus(ex is ArgumentException ? "请输入标题，并检查内容长度；输入已保留。" : "保存失败，输入已保留；任务可能仍有活动执行。");
        }
    }
    private void OnTitleTextChanged(object sender, TextChangedEventArgs e) => OnEditorTextChanged(sender, e);
    private void OnEditorTextChanged(object sender, TextChangedEventArgs e)
    {
        _suppressEmptySaveActivation = false;
        UpdateSaveButtonState();
    }
    private void UpdateSaveButtonState() => SaveButton.IsEnabled = !_saveActivationPending && !string.IsNullOrWhiteSpace(TitleInput.Text);
    private void OnRevealDescription(object sender, RoutedEventArgs e) { ShowDescription(); DescriptionInput.Focus(); }
    private void SetStatus(string text)
    {
        StatusText.Text = text;
        _statusTimer.Stop();
        if (!string.IsNullOrWhiteSpace(text)) _statusTimer.Start();
    }
    private void OnCancel(object sender, RoutedEventArgs e) { ResetEditor(); _suppressEmptySaveActivation = false; SetStatus("已取消"); AddTaskButton.Focus(); }

    private void OnRowSave(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Row row }) SaveRow(row);
    }

    private void OnRowCancel(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Row row }) return;
        row.EndEditing();
        if (ReferenceEquals(_editingRow, row)) _editingRow = null;
        SetStatus("已取消编辑");
        Refresh();
        FocusRow(row);
    }

    private void OnStepComplete(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox checkBox || checkBox.Tag is not Row.StepRow step) return;

        var row = step.Parent;
        var expected = row.Item;
        if (_editingRow is not null || _editingStep is not null)
        {
            checkBox.IsChecked = step.Item.IsCompleted;
            SetStatus("请先保存或取消当前编辑。");
            e.Handled = true;
            return;
        }
        if (!row.CanEditSteps)
        {
            // IsChecked is intentionally OneWay, but a mouse/keyboard activation can
            // still change the control's visual state before this handler runs.
            checkBox.IsChecked = step.Item.IsCompleted;
            SetStatus(row.IsCompleted ? "已完成任务需先恢复后编辑。" : "任务当前受保护，请先核对。");
            e.Handled = true;
            return;
        }

        var targetId = step.Item.Id;
        var steps = expected.Steps.Select(current => current.Id == targetId
            ? new TodoStep(current.Id, current.Title, current.Order, !current.IsCompleted)
            : current).ToArray();
        TrySaveSteps(row, expected, steps);
        e.Handled = true;
    }

    private bool TrySaveSteps(Row row, TodoItem expected, IReadOnlyList<TodoStep> steps)
    {
        if (_savingStepsRowId is not null) return false;
        _savingStepsRowId = row.Item.Id;
        try
        {
            var updated = _service.UpdateSteps(expected, steps);
            row.Update(updated, !_service.IsProtected(updated), row.ExecutionStatus, updated.Id == _highlightedTodoId);
            SetStatus("已保存");
            return true;
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            // Re-read the committed value and put the row back on that snapshot. This
            // also makes the OneWay checkbox visibly truthful after a failed click.
            var persisted = _service.Get(expected.Id) ?? expected;
            row.Update(persisted, !_service.IsProtected(persisted), row.ExecutionStatus,
                persisted.Id == _highlightedTodoId);
            RestoreStepVisuals(row, persisted);
            SetStatus(ex is InvalidOperationException &&
                (ex.Message.Contains("变化", StringComparison.Ordinal) || ex.Message.Contains("核对", StringComparison.Ordinal))
                ? "任务已变化或受保护，请重新核对；步骤未保存。"
                : "步骤保存失败，状态已还原。");
            return false;
        }
        finally
        {
            _savingStepsRowId = null;
        }
    }

    private void RestoreStepVisuals(Row row, TodoItem persisted)
    {
        foreach (var checkBox in FindVisualChildren<CheckBox>(this))
        {
            if (checkBox.Tag is not Row.StepRow step || !ReferenceEquals(step.Parent, row)) continue;
            var actual = persisted.Steps.FirstOrDefault(item => item.Id == step.Item.Id);
            checkBox.IsChecked = actual?.IsCompleted ?? false;
        }
    }

    private void SaveRow(Row row)
    {
        if (_savingRowId is not null) return;
        if (!row.CanSaveDraft)
        {
            SetStatus("请输入标题；草稿已保留。");
            return;
        }

        _savingRowId = row.Item.Id;
        try
        {
            var current = _service.Get(row.Item.Id);
            if (current is null || row.EditOriginal is null || !TodoItemValueComparer.Equals(current, row.EditOriginal) || _service.IsProtected(current))
                throw new InvalidOperationException("任务已变化或受保护，请重新核对。草稿已保留。");

            var updated = _service.Update(row.Item.Id, row.DraftTitle, row.DraftDescription);
            row.Update(updated, !_service.IsProtected(updated), row.ExecutionStatus, updated.Id == _highlightedTodoId);
            row.EndEditing();
            if (ReferenceEquals(_editingRow, row)) _editingRow = null;
            SetStatus("已保存");
            Refresh();
            FocusRow(row);
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            SetStatus(ex is InvalidOperationException &&
                (ex.Message.Contains("变化", StringComparison.Ordinal) || ex.Message.Contains("保护", StringComparison.Ordinal))
                ? "任务已变化或受保护，请重新核对；草稿已保留。"
                : ex is ArgumentException ? "请输入标题，并检查内容长度；草稿已保留。" : "保存失败，草稿已保留。");
        }
        finally
        {
            _savingRowId = null;
        }
    }

    private void FocusRowEditor(Row row)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            var editor = FindVisualChildren<TextBox>(this).FirstOrDefault(textBox => ReferenceEquals(textBox.Tag, row));
            editor?.Focus();
        }), DispatcherPriority.Loaded);
    }

    private void FocusStepEditor(Row.StepRow step)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            var editor = FindVisualChildren<TextBox>(this).FirstOrDefault(textBox => ReferenceEquals(textBox.Tag, step));
            editor?.Focus();
        }), DispatcherPriority.Loaded);
    }

    private void FocusNewStepEditor(Row row)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            var editor = FindVisualChildren<TextBox>(this).FirstOrDefault(textBox =>
                ReferenceEquals(textBox.DataContext, row) &&
                System.Windows.Automation.AutomationProperties.GetName(textBox) == "新步骤标题");
            editor?.Focus();
        }), DispatcherPriority.Loaded);
    }

    private void FocusStepButton(Row.StepRow step)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            var button = FindVisualChildren<Button>(this).FirstOrDefault(candidate =>
                ReferenceEquals(candidate.Tag, step) &&
                System.Windows.Automation.AutomationProperties.GetName(candidate) == "编辑步骤");
            button?.Focus();
        }), DispatcherPriority.Loaded);
    }

    private void FocusStepMoreButton(Row.StepRow step)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            var button = FindVisualChildren<Button>(this).FirstOrDefault(candidate =>
                ReferenceEquals(candidate.Tag, step) &&
                System.Windows.Automation.AutomationProperties.GetName(candidate) == "更多步骤操作");
            button?.Focus();
        }), DispatcherPriority.Loaded);
    }

    private void FocusStepButtonById(Row row, string? stepId)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (stepId is not null)
            {
                var button = FindVisualChildren<Button>(this).FirstOrDefault(candidate =>
                    candidate.Tag is Row.StepRow step && ReferenceEquals(step.Parent, row) && step.Item.Id == stepId &&
                    System.Windows.Automation.AutomationProperties.GetName(candidate) == "编辑步骤");
                if (button is not null)
                {
                    button.Focus();
                    return;
                }
            }
            FocusAddStepButton(row);
        }), DispatcherPriority.Loaded);
    }

    private void FocusAddStepButton(Row row)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            var button = FindVisualChildren<Button>(this).FirstOrDefault(candidate =>
                ReferenceEquals(candidate.Tag, row) &&
                System.Windows.Automation.AutomationProperties.GetName(candidate) == "添加步骤");
            button?.Focus();
        }), DispatcherPriority.Loaded);
    }

    private void FocusCompletionCancel(Row row)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            var button = FindVisualChildren<Button>(this).FirstOrDefault(candidate =>
                candidate.Tag is Row candidateRow && ReferenceEquals(candidateRow, row) &&
                System.Windows.Automation.AutomationProperties.GetName(candidate) == "取消完成");
            button?.Focus();
        }), DispatcherPriority.Loaded);
    }

    private void FocusStepDeleteCancel(Row.StepRow step)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            var button = FindVisualChildren<Button>(this).FirstOrDefault(candidate =>
                candidate.Tag is Row.StepRow candidateStep && ReferenceEquals(candidateStep, step) &&
                System.Windows.Automation.AutomationProperties.GetName(candidate) == "取消删除步骤");
            button?.Focus();
        }), DispatcherPriority.Loaded);
    }

    private void FocusCompletionButton(Row row)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            var button = FindVisualChildren<CheckBox>(this).FirstOrDefault(candidate =>
                candidate.Tag is Row candidateRow && ReferenceEquals(candidateRow, row));
            button?.Focus();
        }), DispatcherPriority.Loaded);
    }

    private void FocusRow(Row row)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            var list = _activeRows.Contains(row) ? ActiveItems : CompletedItems;
            if (list.ItemContainerGenerator.ContainerFromItem(row) is FrameworkElement container)
            {
                container.Focusable = true;
                container.Focus();
            }
        }), DispatcherPriority.Loaded);
    }
    private void ShowDescription()
    {
        OptionalDescriptionPanel.Visibility = Visibility.Visible;
        RevealDescriptionButton.Visibility = Visibility.Collapsed;
    }
    private void ResetEditor()
    {
        _editingId = null;
        TitleInput.Text = "";
        DescriptionInput.Text = "";
        EditorHeading.Text = "快速新增";
        OptionalDescriptionPanel.Visibility = Visibility.Collapsed;
        RevealDescriptionButton.Visibility = Visibility.Visible;
        QuickAddEditor.Visibility = Visibility.Collapsed;
    }
    private void OnTextInputStart(object sender, TextCompositionEventArgs e) => _composing = true;
    private void OnTextInput(object sender, TextCompositionEventArgs e) => _composing = false;
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && !_composing && !e.IsRepeat)
        {
            var completing = _activeRows.FirstOrDefault(row => row.IsConfirmingCompletion);
            if (completing is not null)
            {
                CancelCompletionConfirmation(completing);
                e.Handled = true;
                return;
            }

            var deleting = _activeRows.Concat(_historyRows)
                .SelectMany(row => row.Steps.Cast<Row.StepRow>())
                .FirstOrDefault(step => step.IsConfirmingDelete);
            if (deleting is not null)
            {
                CancelStepDelete(deleting);
                e.Handled = true;
                return;
            }
        }

        if (e.OriginalSource is TextBox stepEditor && stepEditor.Tag is Row.StepRow step && step.IsEditing)
        {
            if (e.Key == Key.Escape && !_composing)
            {
                CancelStepEdit(step);
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Enter && !_composing && !e.IsRepeat && Keyboard.Modifiers == ModifierKeys.None)
            {
                SaveStepEdit(step);
                e.Handled = true;
                return;
            }
            return;
        }
        if (e.OriginalSource is TextBox newStepEditor && newStepEditor.DataContext is Row newStep && newStep.IsAddingStep)
        {
            if (e.Key == Key.Escape && !_composing)
            {
                CancelNewStep(newStep);
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Enter && !_composing && !e.IsRepeat && Keyboard.Modifiers == ModifierKeys.None)
            {
                SaveNewStep(newStep);
                e.Handled = true;
                return;
            }
            return;
        }
        if (e.OriginalSource is TextBox rowEditor && rowEditor.Tag is Row row && row.IsEditing)
        {
            if (e.Key == Key.Escape && !_composing)
            {
                OnRowCancel(rowEditor, new RoutedEventArgs());
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Enter && ReferenceEquals(rowEditor, FindVisualChildren<TextBox>(this)
                    .FirstOrDefault(textBox => ReferenceEquals(textBox.Tag, row))) &&
                !_composing && !e.IsRepeat && Keyboard.Modifiers == ModifierKeys.None)
            {
                SaveRow(row);
                e.Handled = true;
                return;
            }
            return;
        }

        if (QuickAddEditor.Visibility != Visibility.Visible) return;
        if (e.Key == Key.Escape && !_composing && (TitleInput.IsKeyboardFocusWithin || DescriptionInput.IsKeyboardFocusWithin))
        {
            OnCancel(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }
        if (e.Key != Key.Enter || _composing || e.Key == Key.ImeProcessed || e.IsRepeat || Keyboard.Modifiers != ModifierKeys.None) return;
        if (!TitleInput.IsKeyboardFocusWithin || !SaveButton.IsEnabled) return;
        OnSave(this, new RoutedEventArgs());
        e.Handled = true;
    }
    private void OnCopy(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Row row }) return;
        try { Clipboard.SetText(row.Item.Title + (string.IsNullOrWhiteSpace(row.Item.Description) ? "" : "\n\n" + row.Item.Description)); SetStatus("已复制任务说明"); }
        catch (System.Runtime.InteropServices.ExternalException) { SetStatus("复制失败，请重试。"); }
    }
    private void OnComplete(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Row row }) return;
        if (_editingRow is not null || _editingStep is not null || QuickAddEditor.Visibility == Visibility.Visible)
        {
            SetStatus("请先保存或取消当前编辑。");
            if (_editingRow is not null) FocusRow(_editingRow);
            else if (_editingStep is not null) FocusStepEditor(_editingStep);
            else TitleInput.Focus();
            e.Handled = true;
            return;
        }
        if (row.IsCompleted)
        {
            OnReopen(row);
            return;
        }

        if (row.IsConfirmingCompletion) return;

        var incomplete = row.Item.Steps.Count(step => !step.IsCompleted);
        if (incomplete > 0)
        {
            if (sender is CheckBox checkbox) checkbox.IsChecked = false;
            row.BeginCompletionConfirmation();
            SetStatus(string.Empty);
            Refresh();
            FocusCompletionCancel(row);
            e.Handled = true;
            return;
        }

        try
        {
            _undo = _service.Complete(row.Item.Id, confirmIncompleteSteps: false);
            _sessionCompletedIds.Add(row.Item.Id);
            SetStatus("已完成");
            UndoButton.Visibility = Visibility.Visible;
            _undoTimer.Stop();
            _undoTimer.Start();
            Refresh();
        }
        catch (Exception ex) when (IsRecoverable(ex)) { SetStatus(CompletionError(ex)); Refresh(); }
    }

    private void OnConfirmCompletion(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Row row } || !ReferenceEquals(row, _activeRows.FirstOrDefault(item => item.Item.Id == row.Item.Id)) ||
            !row.IsConfirmingCompletion || row.CompletionOriginal is null) return;

        var expected = row.CompletionOriginal;
        var current = _service.Get(expected.Id);
        if (current is null)
        {
            SetStatus("任务已不存在，请取消后重新核对。");
            return;
        }
        if (!TodoItemValueComparer.Equals(current, expected))
        {
            SetStatus("任务已变化，请取消后重新核对。");
            Refresh();
            FocusCompletionCancel(row);
            return;
        }

        try
        {
            _undo = _service.Complete(expected.Id, confirmIncompleteSteps: true);
            row.EndCompletionConfirmation();
            _sessionCompletedIds.Add(row.Item.Id);
            SetStatus("已完成");
            UndoButton.Visibility = Visibility.Visible;
            _undoTimer.Stop();
            _undoTimer.Start();
            Refresh();
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            SetStatus(CompletionError(ex));
            Refresh();
            FocusCompletionCancel(row);
        }
        e.Handled = true;
    }

    private void OnCancelCompletion(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Row row } || !row.IsConfirmingCompletion) return;
        CancelCompletionConfirmation(row);
        e.Handled = true;
    }

    private void CancelCompletionConfirmation(Row row)
    {
        row.EndCompletionConfirmation();
        SetStatus("已取消完成");
        Refresh();
        FocusCompletionButton(row);
    }

    private static string CompletionError(Exception ex) => ex.Message.Contains("核对", StringComparison.Ordinal)
        ? "任务当前受保护，请先核对。"
        : ex.Message.Contains("变化", StringComparison.Ordinal)
            ? "任务已变化，请取消后重新核对。"
            : "保存失败，任务仍未完成。";

    private void OnReopen(Row row)
    {
        try
        {
            _service.Reopen(row.Item.Id);
            _sessionCompletedIds.Remove(row.Item.Id);
            if (_undo?.Id == row.Item.Id)
            {
                _undo = null;
                _undoTimer.Stop();
                UndoButton.Visibility = Visibility.Collapsed;
            }
            SetStatus("已恢复");
            Refresh();
        }
        catch (Exception ex) when (IsRecoverable(ex)) { SetStatus("无法恢复，任务可能仍有活动执行。"); Refresh(); }
    }
    private void OnUndo(object sender, RoutedEventArgs e)
    {
        if (_undo is null) return;
        try { _service.UndoCompletion(_undo); _sessionCompletedIds.Remove(_undo.Id); SetStatus("已撤销完成"); Refresh(); }
        catch (Exception ex) when (IsRecoverable(ex)) { SetStatus("任务已变化或暂时不可写，无法撤销。"); }
        _undo = null; _undoTimer.Stop(); UndoButton.Visibility = Visibility.Collapsed;
    }
    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Row row }) return;
        if (MessageBox.Show(Window.GetWindow(this), "删除待办“" + row.Item.Title + "”？此操作不能撤销。", "删除待办", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        try { if (_focusTodoId == row.Item.Id) _focusTodoId = null; if (_highlightedTodoId == row.Item.Id) _highlightedTodoId = null; _service.Delete(row.Item.Id); SetStatus("已删除"); }
        catch (Exception ex) when (IsRecoverable(ex)) { SetStatus("删除失败，任务可能仍有活动执行。"); }
    }
    private void OnTodoContextMenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu || menu.PlacementTarget is not FrameworkElement { Tag: Row row }) return;
        menu.DataContext = row;
        foreach (var item in menu.Items.OfType<MenuItem>())
        {
            item.Tag = row;
            item.IsEnabled = row.CanEdit;
        }
    }
    private void OnMore(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button) return;
        menu.PlacementTarget = button;
        menu.IsOpen = true;
    }

    private void OnStepContextMenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu || menu.PlacementTarget is not FrameworkElement { Tag: Row.StepRow step }) return;
        menu.DataContext = step;
        foreach (var item in menu.Items.OfType<MenuItem>())
        {
            item.Tag = step;
            item.IsEnabled = item.Header?.ToString() switch
            {
                "上移步骤" => step.CanMoveUp && CanRunStepMutation(),
                "下移步骤" => step.CanMoveDown && CanRunStepMutation(),
                "删除步骤" => step.CanDelete && CanRunStepMutation(),
                _ => false
            };
        }
    }
    private bool CanRunStepMutation() =>
        _editingRow is null && _editingStep is null && _addingStepRow is null &&
        QuickAddEditor.Visibility != Visibility.Visible;
    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            foreach (var child in FindVisualChildren<T>(VisualTreeHelper.GetChild(root, index))) yield return child;
        }
    }

    private static bool IsRecoverable(Exception ex) => ex is ArgumentException or InvalidOperationException or
        System.IO.IOException or UnauthorizedAccessException or System.Data.Common.DbException or KeyNotFoundException;
}
