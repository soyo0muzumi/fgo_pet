using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FgoPet.App.Services;
using FgoPet.Core.Todo;
using FgoPet.Core.Agents;

namespace FgoPet.App.Views;

public partial class TodoWorkspaceView : UserControl
{
    private readonly TodoApplicationService _service;
    private readonly IAgentRepository? _agents;
    private string? _editingId;
    private bool _saveActivationPending;
    private TodoItem? _undo;
    private readonly DispatcherTimer _undoTimer = new() { Interval = TimeSpan.FromSeconds(8) };
    public event Action? LegacyRequested;

    public TodoWorkspaceView(TodoApplicationService service, IAgentRepository? agents = null,
        FgoPet.App.ViewModels.AgentCurrentTaskViewModel? currentTask = null)
    {
        _service = service;
        _agents = agents;
        InitializeComponent();
        if (currentTask is not null) LegacyTaskHost.Content = new AgentCurrentTaskStrip { DataContext = currentTask };
        Loaded += (_, _) => { _service.Changed += OnChanged; Refresh(); };
        Unloaded += (_, _) => { _service.Changed -= OnChanged; _undoTimer.Stop(); _undo = null; UndoButton.Visibility = Visibility.Collapsed; };
        _undoTimer.Tick += (_, _) => { _undoTimer.Stop(); _undo = null; UndoButton.Visibility = Visibility.Collapsed; };
    }

    private sealed record Row(TodoItem Item, bool CanEdit, string? ExecutionStatus)
    {
        public bool IsCompleted => Item.Status == TodoStatus.Completed;
        public bool CanComplete => CanEdit && !IsCompleted;
        public bool HasExecution => ExecutionStatus is not null;
        public string CompletionLabel => "完成待办：" + Item.Title;
    }

    private Row MakeRow(TodoItem item)
    {
        var execution = _agents?.GetLatestExecutionForTodo(item.Id);
        return new Row(item, !_service.IsProtected(item), execution is null ? null :
            new FgoPet.App.ViewModels.AgentExecutionViewModel(execution).StatusText);
    }

    public void Refresh()
    {
        try
        {
            var active = _service.ListActive().Select(MakeRow).ToArray();
            ActiveItems.ItemsSource = active;
            CompletedItems.ItemsSource = _service.ListHistory().Select(MakeRow).ToArray();
            EmptyText.Visibility = active.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex) when (IsRecoverable(ex)) { StatusText.Text = "读取待办失败，请稍后重新打开。"; }
    }

    private void OnChanged() => Dispatcher.BeginInvoke(new Action(Refresh));
    public void FocusTodo(string? id)
    {
        if (string.IsNullOrEmpty(id)) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            var list = ActiveItems.Items.Cast<Row>().Any(row => row.Item.Id == id) ? ActiveItems : CompletedItems;
            var row = list.Items.Cast<Row>().FirstOrDefault(row => row.Item.Id == id);
            if (row is null) { StatusText.Text = "这条待办已不存在。"; return; }
            if (list == CompletedItems) CompletedSection.IsExpanded = true;
            UpdateLayout();
            if (list.ItemContainerGenerator.ContainerFromItem(row) is FrameworkElement container)
            { container.BringIntoView(); container.Focusable = true; container.Focus(); }
        }));
    }
    public void BeginAdd()
    {
        TitleInput.Focus();
    }
    private void OnEdit(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Row row }) return;
        if (_editingId is not null || !string.IsNullOrWhiteSpace(TitleInput.Text) || !string.IsNullOrWhiteSpace(DescriptionInput.Text))
        { StatusText.Text = "请先保存或取消当前编辑。"; TitleInput.Focus(); return; }
        _editingId = row.Item.Id; TitleInput.Text = row.Item.Title; DescriptionInput.Text = row.Item.Description ?? "";
        EditorHeading.Text = "编辑待办"; ShowDescription(); TitleInput.Focus();
    }
    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (_saveActivationPending) return;
        _saveActivationPending = true;
        try
        {
            if (_editingId is null) _service.Create(TitleInput.Text, DescriptionInput.Text, TodoPriority.Normal, null);
            else _service.Update(_editingId, TitleInput.Text, DescriptionInput.Text);
            ResetEditor(); StatusText.Text = "已保存"; TitleInput.Focus();
            Dispatcher.BeginInvoke(new Action(() => _saveActivationPending = false), DispatcherPriority.Background);
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            _saveActivationPending = false;
            StatusText.Text = ex is ArgumentException ? "请输入标题，并检查内容长度；输入已保留。" : "保存失败，输入已保留；任务可能仍有活动执行。";
        }
    }
    private void OnRevealDescription(object sender, RoutedEventArgs e) { ShowDescription(); DescriptionInput.Focus(); }
    private void OnCancel(object sender, RoutedEventArgs e) { ResetEditor(); StatusText.Text = "已取消"; TitleInput.Focus(); }
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
    }
    private void OnCopy(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Row row }) return;
        try { Clipboard.SetText(row.Item.Title + (string.IsNullOrWhiteSpace(row.Item.Description) ? "" : "\n\n" + row.Item.Description)); StatusText.Text = "已复制任务说明"; }
        catch (System.Runtime.InteropServices.ExternalException) { StatusText.Text = "复制失败，请重试。"; }
    }
    private void OnComplete(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Row row }) return;
        try { _undo = _service.Complete(row.Item.Id); StatusText.Text = "已完成"; UndoButton.Visibility = Visibility.Visible; _undoTimer.Stop(); _undoTimer.Start(); }
        catch (Exception ex) when (IsRecoverable(ex)) { StatusText.Text = "无法完成，任务可能仍有活动执行。"; Refresh(); }
    }
    private void OnUndo(object sender, RoutedEventArgs e)
    {
        if (_undo is null) return;
        try { _service.UndoCompletion(_undo); StatusText.Text = "已撤销完成"; }
        catch (Exception ex) when (IsRecoverable(ex)) { StatusText.Text = "任务已变化或暂时不可写，无法撤销。"; }
        _undo = null; _undoTimer.Stop(); UndoButton.Visibility = Visibility.Collapsed;
    }
    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Row row }) return;
        if (MessageBox.Show(Window.GetWindow(this), "删除待办“" + row.Item.Title + "”？此操作不能撤销。", "删除待办", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        try { _service.Delete(row.Item.Id); StatusText.Text = "已删除"; }
        catch (Exception ex) when (IsRecoverable(ex)) { StatusText.Text = "删除失败，任务可能仍有活动执行。"; }
    }
    private void OnLegacy(object sender, RoutedEventArgs e) => LegacyRequested?.Invoke();
    private static bool IsRecoverable(Exception ex) => ex is ArgumentException or InvalidOperationException or
        System.IO.IOException or UnauthorizedAccessException or System.Data.Common.DbException or KeyNotFoundException;
}
