using Row = FgoPet.Plugin.Todo.TodoEditorSession;
using FgoPet.Plugin.Todo;
using FgoPet.UiSdk;
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

namespace FgoPet.App.Views;

public partial class TodoWorkspaceView : UserControl, IWorkspaceSurface, IAsyncDisposable
{
    private const int MaxSteps = 20;
    private readonly TodoApplicationService _service;
    private volatile bool _disposed;
    private bool _uiDisposed;
    private bool _subscribed;
    private string? _editingId;
    private Row? _editingRow;
    private TodoStepEditor? _editingStep;
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

    public TodoWorkspaceView(TodoApplicationService service)
    {
        _service = service;
        InitializeComponent();
        AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(OnPreviewKeyDown), handledEventsToo: true);
        // Handle both the initial quick-add controls and the dynamically-created
        // row/step editors.  IME composition must guard every editor uniformly.
        TextCompositionManager.AddPreviewTextInputStartHandler(this, OnTextInputStart);
        TextCompositionManager.AddPreviewTextInputHandler(this, OnTextInput);
        Loaded += (_, _) =>
        {
            if (_disposed) return;
            if (!_subscribed) { _service.Changed += OnChanged; _subscribed = true; }
            Refresh();
        };
        Unloaded += (_, _) =>
        {
            _service.Changed -= OnChanged;
            _subscribed = false;
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

    public void Navigate(WorkspaceNavigation navigation)
    {
        if (_disposed) return;
        if (navigation.Kind == WorkspaceNavigationKind.NewItem) BeginAdd();
        else if (navigation.Kind == WorkspaceNavigationKind.ExistingItem) FocusTodo(navigation.ItemId);
        else if (navigation.Kind == WorkspaceNavigationKind.Activate) EnterView();
        else Refresh();
    }

    private void FenceDisposal()
    {
        _disposed = true;
        _service.Changed -= OnChanged;
        _subscribed = false;
    }

    private void DisposeUi()
    {
        if (_uiDisposed) return;
        _uiDisposed = true;
        IsEnabled = false;
        _undoTimer.Stop(); _statusTimer.Stop(); _highlightTimer.Stop();
    }

    public void Dispose()
    {
        FenceDisposal();
        if (Dispatcher.CheckAccess()) DisposeUi();
        else if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke((Action)DisposeUi);
    }

    public async ValueTask DisposeAsync()
    {
        FenceDisposal();
        if (Dispatcher.CheckAccess()) DisposeUi();
        else if (!Dispatcher.HasShutdownStarted)
        {
            try { await Dispatcher.InvokeAsync(DisposeUi).Task.ConfigureAwait(false); }
            catch (OperationCanceledException) when (Dispatcher.HasShutdownStarted) { }
        }
    }

    private Row MakeRow(TodoItem item, Row? existing = null)
    {
        var canEdit = !_service.IsProtected(item);
        if (existing is null) return new Row(item, canEdit, item.Id == _highlightedTodoId);
        existing.Update(item, canEdit, item.Id == _highlightedTodoId);
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
        if (_disposed || _refreshPending || Dispatcher.HasShutdownStarted) return;
        _refreshPending = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _refreshPending = false;
            if (!_disposed) Refresh();
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
        if (sender is not FrameworkElement { Tag: TodoStepEditor step }) return;
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
        if (sender is not FrameworkElement { Tag: TodoStepEditor step }) return;
        SaveStepEdit(step);
        e.Handled = true;
    }

    private void OnCancelStepEdit(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: TodoStepEditor step }) return;
        CancelStepEdit(step);
        e.Handled = true;
    }

    private void OnBeginDeleteStep(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: TodoStepEditor step }) return;
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
        if (sender is not FrameworkElement { Tag: TodoStepEditor step } || !step.IsConfirmingDelete) return;
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
        if (sender is not FrameworkElement { Tag: TodoStepEditor step } || !step.IsConfirmingDelete) return;
        CancelStepDelete(step);
        e.Handled = true;
    }

    private void CancelStepDelete(TodoStepEditor step)
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
        if (sender is not FrameworkElement { Tag: TodoStepEditor step }) return;
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

    private void SaveStepEdit(TodoStepEditor step)
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

    private void CancelStepEdit(TodoStepEditor step)
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
        if (sender is not CheckBox checkBox || checkBox.Tag is not TodoStepEditor step) return;

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
            row.Update(updated, !_service.IsProtected(updated), updated.Id == _highlightedTodoId);
            SetStatus("已保存");
            return true;
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            // Re-read the committed value and put the row back on that snapshot. This
            // also makes the OneWay checkbox visibly truthful after a failed click.
            var persisted = _service.Get(expected.Id) ?? expected;
            row.Update(persisted, !_service.IsProtected(persisted),
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
            if (checkBox.Tag is not TodoStepEditor step || !ReferenceEquals(step.Parent, row)) continue;
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
            row.Update(updated, !_service.IsProtected(updated), updated.Id == _highlightedTodoId);
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

    private void FocusStepEditor(TodoStepEditor step)
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

    private void FocusStepButton(TodoStepEditor step)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            var button = FindVisualChildren<Button>(this).FirstOrDefault(candidate =>
                ReferenceEquals(candidate.Tag, step) &&
                System.Windows.Automation.AutomationProperties.GetName(candidate) == "编辑步骤");
            button?.Focus();
        }), DispatcherPriority.Loaded);
    }

    private void FocusStepMoreButton(TodoStepEditor step)
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
                    candidate.Tag is TodoStepEditor step && ReferenceEquals(step.Parent, row) && step.Item.Id == stepId &&
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

    private void FocusStepDeleteCancel(TodoStepEditor step)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            var button = FindVisualChildren<Button>(this).FirstOrDefault(candidate =>
                candidate.Tag is TodoStepEditor candidateStep && ReferenceEquals(candidateStep, step) &&
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
                .SelectMany(row => row.Steps.Cast<TodoStepEditor>())
                .FirstOrDefault(step => step.IsConfirmingDelete);
            if (deleting is not null)
            {
                CancelStepDelete(deleting);
                e.Handled = true;
                return;
            }
        }

        if (e.OriginalSource is TextBox stepEditor && stepEditor.Tag is TodoStepEditor step && step.IsEditing)
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
        if (sender is not ContextMenu menu || menu.PlacementTarget is not FrameworkElement { Tag: TodoStepEditor step }) return;
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
