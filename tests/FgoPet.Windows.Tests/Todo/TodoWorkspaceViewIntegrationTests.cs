using System.Runtime.ExceptionServices;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FgoPet.App.Services;
using FgoPet.App.Views;
using FgoPet.Core.Agents;
using FgoPet.Core.Archives;
using FgoPet.Core.Todo;
using Xunit;

namespace FgoPet.Windows.Tests.Todo;

[Trait("Category", "WindowsIntegration")]
public sealed class TodoWorkspaceViewIntegrationTests
{
    [Fact]
    public void Quick_add_is_collapsed_until_begin_add_and_reveals_optional_description_on_demand()
    {
        StaRun(() =>
        {
            var repository = new FakeTodoRepository();
            var view = ShowView(repository);
            try
            {
                var editor = Assert.IsAssignableFrom<FrameworkElement>(view.FindName("QuickAddEditor"));
                var add = Assert.IsType<Button>(view.FindName("AddTaskButton"));
                Assert.Equal(Visibility.Collapsed, editor.Visibility);
                Assert.IsAssignableFrom<Geometry>(add.Content);
                Assert.Equal("添加任务", System.Windows.Automation.AutomationProperties.GetName(add));

                view.BeginAdd();

                Assert.Equal(Visibility.Visible, editor.Visibility);
                Assert.Same(view.FindName("TitleInput"), Keyboard.FocusedElement);
                Assert.Equal(Visibility.Collapsed, Assert.IsAssignableFrom<FrameworkElement>(view.FindName("OptionalDescriptionPanel")).Visibility);

                Click(view, "添加备注");

                Assert.Equal(Visibility.Visible, Assert.IsAssignableFrom<FrameworkElement>(view.FindName("OptionalDescriptionPanel")).Visibility);
                Assert.True(Assert.IsType<TextBox>(view.FindName("TitleInput")).IsVisible);
            }
            finally { CloseView(view); }
        });
    }

    [Fact]
    public void Step_progress_is_hidden_for_empty_and_shows_completed_over_total()
    {
        StaRun(() =>
        {
            var repository = new FakeTodoRepository();
            repository.Save(ItemWithSteps("steps-empty", 0, 0));
            repository.Save(ItemWithSteps("steps-partial", 5, 2));
            repository.Save(ItemWithSteps("steps-complete", 5, 5));
            var view = ShowView(repository);
            try
            {
                var rows = Assert.IsType<ItemsControl>(view.FindName("ActiveItems"));
                var empty = GetRowExpander(rows, "steps-empty");
                var partial = GetRowExpander(rows, "steps-partial");
                var complete = GetRowExpander(rows, "steps-complete");

                Assert.DoesNotContain(FindVisualChildren<TextBlock>(empty), textBlock =>
                    textBlock.Visibility == Visibility.Visible && textBlock.Text.Contains("步", StringComparison.Ordinal));
                Assert.Contains(FindVisualChildren<TextBlock>(partial), textBlock =>
                    textBlock.Visibility == Visibility.Visible && textBlock.Text == "2 / 5 步");
                Assert.Contains(FindVisualChildren<TextBlock>(complete), textBlock =>
                    textBlock.Visibility == Visibility.Visible && textBlock.Text == "5 / 5 步");

                partial.IsExpanded = true;
                complete.IsExpanded = true;
                view.UpdateLayout();
                Assert.Equal(5, FindVisualChildren<CheckBox>(partial).Count(IsStepCheckBox));
                Assert.Equal(5, FindVisualChildren<CheckBox>(complete).Count(IsStepCheckBox));
                Assert.Equal(2, FindVisualChildren<CheckBox>(partial).Count(checkBox => IsStepCheckBox(checkBox) && checkBox.IsChecked == true));
                Assert.Equal(5, FindVisualChildren<CheckBox>(complete).Count(checkBox => IsStepCheckBox(checkBox) && checkBox.IsChecked == true));
                Assert.Contains(FindVisualChildren<TextBlock>(partial), textBlock => textBlock.Text == "步骤 1");
                Assert.Contains(FindVisualChildren<TextBlock>(complete), textBlock => textBlock.Text == "步骤 5");
            }
            finally { CloseView(view); }
        });
    }

    [Fact]
    public void Clicking_a_step_saves_only_that_step_and_keeps_the_parent_planned()
    {
        StaRun(() =>
        {
            var repository = new FakeTodoRepository();
            var original = ItemWithSteps("step-toggle", 2, 0);
            repository.Items.Add(original);
            var view = ShowView(repository);
            try
            {
                var rows = Assert.IsType<ItemsControl>(view.FindName("ActiveItems"));
                var row = GetRowExpander(rows, original.Id);
                row.IsExpanded = true;
                view.UpdateLayout();
                var step = GetStepCompletionControl(row, original.Steps[1].Id);

                Assert.True(step.IsEnabled);
                Assert.False(step.IsChecked);
                step.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));

                var saved = repository.Get(original.Id)!;
                Assert.Equal(TodoStatus.Planned, saved.Status);
                Assert.False(saved.Steps[0].IsCompleted);
                Assert.True(saved.Steps[1].IsCompleted);
                Assert.Equal(original.Steps[1].Title, saved.Steps[1].Title);
                Assert.Equal(1, repository.SaveCount);
                Assert.True(step.IsChecked);
            }
            finally { CloseView(view); }
        });
    }

    [Fact]
    public void Failed_step_save_restores_the_visible_checkbox_and_keeps_persisted_state()
    {
        StaRun(() =>
        {
            var repository = new FakeTodoRepository { SaveFailure = new IOException("simulated") };
            var original = ItemWithSteps("step-failure", 1, 0);
            repository.Items.Add(original);
            var view = ShowView(repository);
            try
            {
                var rows = Assert.IsType<ItemsControl>(view.FindName("ActiveItems"));
                var row = GetRowExpander(rows, original.Id);
                row.IsExpanded = true;
                view.UpdateLayout();
                var step = GetStepCompletionControl(row, original.Steps[0].Id);

                step.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));

                Assert.False(repository.Get(original.Id)!.Steps[0].IsCompleted);
                Assert.False(step.IsChecked);
                Assert.Contains("失败", Assert.IsType<TextBlock>(view.FindName("StatusText")).Text);
            }
            finally { CloseView(view); }
        });
    }

    [Fact]
    public void Completing_all_steps_does_not_complete_the_parent()
    {
        StaRun(() =>
        {
            var repository = new FakeTodoRepository();
            var original = ItemWithSteps("step-parent", 2, 0);
            repository.Items.Add(original);
            var view = ShowView(repository);
            try
            {
                var rows = Assert.IsType<ItemsControl>(view.FindName("ActiveItems"));
                var row = GetRowExpander(rows, original.Id);
                row.IsExpanded = true;
                view.UpdateLayout();
                GetStepCompletionControl(row, original.Steps[0].Id)
                    .RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
                GetStepCompletionControl(row, original.Steps[1].Id)
                    .RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));

                Assert.Equal(TodoStatus.Planned, repository.Get(original.Id)!.Status);
                Assert.Equal(2, repository.Get(original.Id)!.Steps.Count(step => step.IsCompleted));
                Assert.False(GetParentCompletionControl(rows, original.Id).IsChecked);
            }
            finally { CloseView(view); }
        });
    }

    [Fact]
    public void Completed_and_active_rows_disable_step_completion()
    {
        StaRun(() =>
        {
            var repository = new FakeTodoRepository();
            var active = ItemWithSteps("step-active", 1, 0).Activate(DateTimeOffset.UtcNow);
            var completed = ItemWithSteps("step-completed", 1, 1).Complete(DateTimeOffset.UtcNow);
            repository.Items.Add(active);
            repository.Items.Add(completed);
            var view = ShowView(repository);
            try
            {
                var activeRow = GetRowExpander(Assert.IsType<ItemsControl>(view.FindName("ActiveItems")), active.Id);
                activeRow.IsExpanded = true;
                view.UpdateLayout();
                Assert.False(GetStepCompletionControl(activeRow, active.Steps[0].Id).IsEnabled);

                var completedSection = Assert.IsType<Expander>(view.FindName("CompletedSection"));
                completedSection.IsExpanded = true;
                view.UpdateLayout();
                var completedRow = GetRowExpander(Assert.IsType<ItemsControl>(view.FindName("CompletedItems")), completed.Id);
                completedRow.IsExpanded = true;
                view.UpdateLayout();
                Assert.False(GetStepCompletionControl(completedRow, completed.Steps[0].Id).IsEnabled);
            }
            finally { CloseView(view); }
        });
    }

    [Fact]
    public void Renaming_a_step_changes_only_its_title_and_preserves_identity_order_and_completion()
    {
        StaRun(() =>
        {
            var repository = new FakeTodoRepository();
            var original = ItemWithSteps("step-rename", 2, 1);
            repository.Items.Add(original);
            var view = ShowView(repository);
            try
            {
                var row = GetRowExpander(Assert.IsType<ItemsControl>(view.FindName("ActiveItems")), original.Id);
                row.IsExpanded = true;
                view.UpdateLayout();
                var edit = GetStepEditButton(row, original.Steps[1].Id);
                edit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpDispatcher();

                var input = GetStepTitleInput(row, original.Steps[1].Id);
                input.Text = "检查发布";
                GetStepActionButton(row, original.Steps[1].Id, "保存步骤编辑")
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                var saved = repository.Get(original.Id)!;
                Assert.Equal(1, repository.SaveCount);
                Assert.Equal(original.Steps[0].Id, saved.Steps[0].Id);
                Assert.Equal(original.Steps[0].Title, saved.Steps[0].Title);
                Assert.True(saved.Steps[0].IsCompleted);
                Assert.Equal(original.Steps[1].Id, saved.Steps[1].Id);
                Assert.Equal("检查发布", saved.Steps[1].Title);
                Assert.Equal(original.Steps[1].Order, saved.Steps[1].Order);
                Assert.False(saved.Steps[1].IsCompleted);
                Assert.Equal(Visibility.Collapsed, GetStepTitleInput(row, original.Steps[1].Id).Visibility);
            }
            finally { CloseView(view); }
        });
    }

    [Fact]
    public void Cancelling_step_rename_does_not_write_and_returns_focus_to_step_edit_button()
    {
        StaRun(() =>
        {
            var repository = new FakeTodoRepository();
            var original = ItemWithSteps("step-rename-cancel", 1, 0);
            repository.Items.Add(original);
            var view = ShowView(repository);
            try
            {
                var row = GetRowExpander(Assert.IsType<ItemsControl>(view.FindName("ActiveItems")), original.Id);
                row.IsExpanded = true;
                view.UpdateLayout();
                GetStepEditButton(row, original.Steps[0].Id).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpDispatcher();
                GetStepTitleInput(row, original.Steps[0].Id).Text = "丢弃的标题";
                GetStepActionButton(row, original.Steps[0].Id, "取消步骤编辑")
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpDispatcher();

                Assert.Equal(0, repository.SaveCount);
                Assert.Equal(original.Steps[0].Title, repository.Get(original.Id)!.Steps[0].Title);
                Assert.Equal("编辑步骤", System.Windows.Automation.AutomationProperties.GetName(Assert.IsType<Button>(Keyboard.FocusedElement)));
            }
            finally { CloseView(view); }
        });
    }

    [Fact]
    public void Blank_step_rename_is_rejected_and_keeps_the_draft_visible()
    {
        StaRun(() =>
        {
            var repository = new FakeTodoRepository();
            var original = ItemWithSteps("step-rename-blank", 1, 0);
            repository.Items.Add(original);
            var view = ShowView(repository);
            try
            {
                var row = GetRowExpander(Assert.IsType<ItemsControl>(view.FindName("ActiveItems")), original.Id);
                row.IsExpanded = true;
                view.UpdateLayout();
                GetStepEditButton(row, original.Steps[0].Id).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpDispatcher();
                var input = GetStepTitleInput(row, original.Steps[0].Id);
                input.Text = "   ";
                GetStepActionButton(row, original.Steps[0].Id, "保存步骤编辑")
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                Assert.Equal(0, repository.SaveCount);
                Assert.Equal("   ", input.Text);
                Assert.Equal(Visibility.Visible, input.Visibility);
                Assert.Contains("请输入步骤标题", Assert.IsType<TextBlock>(view.FindName("StatusText")).Text);
            }
            finally { CloseView(view); }
        });
    }

    [Fact]
    public void Failed_step_rename_keeps_the_draft_and_editor_visible()
    {
        StaRun(() =>
        {
            var repository = new FakeTodoRepository { SaveFailure = new IOException("simulated") };
            var original = ItemWithSteps("step-rename-failure", 1, 0);
            repository.Items.Add(original);
            var view = ShowView(repository);
            try
            {
                var row = GetRowExpander(Assert.IsType<ItemsControl>(view.FindName("ActiveItems")), original.Id);
                row.IsExpanded = true;
                view.UpdateLayout();
                GetStepEditButton(row, original.Steps[0].Id).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpDispatcher();
                var input = GetStepTitleInput(row, original.Steps[0].Id);
                input.Text = "保留这个步骤草稿";
                GetStepActionButton(row, original.Steps[0].Id, "保存步骤编辑")
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                Assert.Equal(0, repository.SaveCount);
                Assert.Equal("保留这个步骤草稿", input.Text);
                Assert.Equal(Visibility.Visible, input.Visibility);
                Assert.Equal(original.Steps[0].Title, repository.Get(original.Id)!.Steps[0].Title);
                Assert.Contains("步骤保存失败", Assert.IsType<TextBlock>(view.FindName("StatusText")).Text);
            }
            finally { CloseView(view); }
        });
    }

    [Fact]
    public void Refresh_does_not_swallow_an_open_step_rename_draft()
    {
        StaRun(() =>
        {
            var repository = new FakeTodoRepository();
            var original = ItemWithSteps("step-rename-refresh", 1, 0);
            repository.Items.Add(original);
            var view = ShowView(repository);
            try
            {
                var row = GetRowExpander(Assert.IsType<ItemsControl>(view.FindName("ActiveItems")), original.Id);
                row.IsExpanded = true;
                view.UpdateLayout();
                GetStepEditButton(row, original.Steps[0].Id).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpDispatcher();
                var input = GetStepTitleInput(row, original.Steps[0].Id);
                input.Text = "刷新后仍保留";

                view.Refresh();
                view.UpdateLayout();

                var refreshed = GetStepTitleInput(row, original.Steps[0].Id);
                Assert.Equal("刷新后仍保留", refreshed.Text);
                Assert.Equal(Visibility.Visible, refreshed.Visibility);
                Assert.Equal(0, repository.SaveCount);
            }
            finally { CloseView(view); }
        });
    }

    [Fact]
    public void Cancelling_quick_add_clears_the_draft_without_writing()
    {
        StaRun(() =>
        {
            var repository = new FakeTodoRepository();
            var view = ShowView(repository);
            try
            {
                view.BeginAdd();
                var title = Assert.IsType<TextBox>(view.FindName("TitleInput"));
                var description = Assert.IsType<TextBox>(view.FindName("DescriptionInput"));
                title.Text = "Draft";
                Click(view, "添加备注");
                description.Text = "Private draft details";

                Click(view, "取消");

                Assert.Empty(repository.Items);
                Assert.Equal(string.Empty, title.Text);
                Assert.Equal(string.Empty, description.Text);
                Assert.Equal(Visibility.Collapsed, Assert.IsAssignableFrom<FrameworkElement>(view.FindName("OptionalDescriptionPanel")).Visibility);
                Assert.Equal(Visibility.Collapsed, Assert.IsAssignableFrom<FrameworkElement>(view.FindName("QuickAddEditor")).Visibility);
                Assert.Same(view.FindName("AddTaskButton"), Keyboard.FocusedElement);
            }
            finally { CloseView(view); }
        });
    }

    [Fact]
    public void Adding_step_to_empty_task_creates_one_incomplete_step_with_generated_id()
    {
        StaRun(() =>
        {
            var repository = new FakeTodoRepository();
            var original = ItemWithSteps("add-step-empty", 0, 0);
            repository.Items.Add(original);
            var view = ShowView(repository);
            try
            {
                var row = GetRowExpander(Assert.IsType<ItemsControl>(view.FindName("ActiveItems")), original.Id);
                row.IsExpanded = true;
                view.UpdateLayout();
                var add = GetAddStepButton(row);

                Assert.Equal("添加步骤", System.Windows.Automation.AutomationProperties.GetName(add));
                Assert.Equal("添加步骤", add.ToolTip);
                add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                view.UpdateLayout();
                var input = GetNewStepTitleInput(row);
                input.Text = "准备发布";
                GetAddStepActionButton(row, "保存步骤").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                var saved = repository.Get(original.Id)!;
                var step = Assert.Single(saved.Steps);
                Assert.StartsWith("step-", step.Id, StringComparison.Ordinal);
                Assert.Equal(0, step.Order);
                Assert.Equal("准备发布", step.Title);
                Assert.False(step.IsCompleted);
                Assert.Equal(1, repository.SaveCount);
                Assert.Equal(Visibility.Collapsed, GetNewStepEditor(row).Visibility);
            }
            finally { CloseView(view); }
        });
    }

    [Fact]
    public void Adding_step_to_nineteen_step_task_reaches_twenty_and_maximum_rejects_next_add()
    {
        StaRun(() =>
        {
            var repository = new FakeTodoRepository();
            var original = ItemWithSteps("add-step-limit", 19, 0);
            repository.Items.Add(original);
            var view = ShowView(repository);
            try
            {
                var row = GetRowExpander(Assert.IsType<ItemsControl>(view.FindName("ActiveItems")), original.Id);
                row.IsExpanded = true;
                view.UpdateLayout();
                GetAddStepButton(row).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var input = GetNewStepTitleInput(row);
                input.Text = "第 20 步";
                GetAddStepActionButton(row, "保存步骤").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                Assert.Equal(20, repository.Get(original.Id)!.Steps.Count);
                Assert.Equal(19, repository.Get(original.Id)!.Steps[^1].Order);
                view.UpdateLayout();
                var add = GetAddStepButton(row);
                Assert.False(add.IsEnabled);
                add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(Visibility.Collapsed, GetNewStepEditor(row).Visibility);
                Assert.Equal(20, repository.Get(original.Id)!.Steps.Count);
            }
            finally { CloseView(view); }
        });
    }

    [Fact]
    public void Cancelling_new_step_does_not_write_and_double_activation_does_not_duplicate_editor()
    {
        StaRun(() =>
        {
            var repository = new FakeTodoRepository();
            var original = ItemWithSteps("add-step-cancel", 0, 0);
            repository.Items.Add(original);
            var view = ShowView(repository);
            try
            {
                var row = GetRowExpander(Assert.IsType<ItemsControl>(view.FindName("ActiveItems")), original.Id);
                row.IsExpanded = true;
                view.UpdateLayout();
                var add = GetAddStepButton(row);
                add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Single(FindVisualChildren<TextBox>(row).Where(textBox =>
                    System.Windows.Automation.AutomationProperties.GetName(textBox) == "新步骤标题"));
                GetNewStepTitleInput(row).Text = "不会保存";
                GetAddStepActionButton(row, "取消添加步骤").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                Assert.Empty(repository.Get(original.Id)!.Steps);
                Assert.Equal(0, repository.SaveCount);
                Assert.Equal(Visibility.Collapsed, GetNewStepEditor(row).Visibility);
            }
            finally { CloseView(view); }
        });
    }

    [Fact]
    public void Failed_new_step_save_keeps_input_and_retries_with_the_same_draft_id()
    {
        StaRun(() =>
        {
            var repository = new FakeTodoRepository { SaveFailure = new IOException("temporary") };
            var original = ItemWithSteps("add-step-failure", 0, 0);
            repository.Items.Add(original);
            var view = ShowView(repository);
            try
            {
                var row = GetRowExpander(Assert.IsType<ItemsControl>(view.FindName("ActiveItems")), original.Id);
                row.IsExpanded = true;
                view.UpdateLayout();
                GetAddStepButton(row).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var input = GetNewStepTitleInput(row);
                input.Text = "重试步骤";
                GetAddStepActionButton(row, "保存步骤").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                Assert.Equal("重试步骤", GetNewStepTitleInput(row).Text);
                Assert.Empty(repository.Get(original.Id)!.Steps);
                var firstAttemptId = Assert.Single(repository.LastAttempt!.Steps).Id;
                repository.SaveFailure = null;
                GetAddStepActionButton(row, "保存步骤").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                var saved = repository.Get(original.Id)!;
                var step = Assert.Single(saved.Steps);
                Assert.Equal(firstAttemptId, step.Id);
                Assert.Equal("重试步骤", step.Title);
                Assert.Equal(1, repository.SaveCount);
            }
            finally { CloseView(view); }
        });
    }

    [Fact]
    public void Deleting_a_step_requires_confirmation_and_reindexes_remaining_steps()
    {
        StaRun(() =>
        {
            var repository = new FakeTodoRepository();
            var original = ItemWithSteps("delete-step", 3, 1);
            repository.Items.Add(original);
            var view = ShowView(repository);
            try
            {
                var row = GetRowExpander(Assert.IsType<ItemsControl>(view.FindName("ActiveItems")), original.Id);
                row.IsExpanded = true;
                view.UpdateLayout();
                var more = GetStepMoreButton(row, original.Steps[1].Id);
                var delete = GetStepMenuItem(more, "删除步骤");
                delete.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                more.ContextMenu.IsOpen = false;
                Assert.Equal(0, repository.SaveCount);
                Assert.Contains(FindVisualChildren<Button>(row), button =>
                    System.Windows.Automation.AutomationProperties.GetName(button) == "确认删除步骤");

                GetStepDeleteActionButton(row, original.Steps[1].Id, "确认删除步骤")
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                var saved = repository.Get(original.Id)!;
                Assert.Equal(1, repository.SaveCount);
                Assert.Equal(2, saved.Steps.Count);
                Assert.Equal(original.Steps[0].Id, saved.Steps[0].Id);
                Assert.Equal(original.Steps[2].Id, saved.Steps[1].Id);
                Assert.Equal(0, saved.Steps[0].Order);
                Assert.Equal(1, saved.Steps[1].Order);
                Assert.True(saved.Steps[0].IsCompleted);
                Assert.False(saved.Steps[1].IsCompleted);
            }
            finally { CloseView(view); }
        });
    }

    [Fact]
    public void Confirmation_buttons_render_their_text_and_default_focus_stays_on_cancel()
    {
        StaRun(() =>
        {
            var repository = new FakeTodoRepository();
            var original = ItemWithSteps("confirmation-buttons", 2, 1);
            repository.Items.Add(original);
            var view = ShowView(repository);
            try
            {
                var rows = Assert.IsType<ItemsControl>(view.FindName("ActiveItems"));
                var row = GetRowExpander(rows, original.Id);
                row.IsExpanded = true;
                view.UpdateLayout();

                GetParentCompletionControl(rows, original.Id)
                    .RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
                view.UpdateLayout();
                PumpDispatcher();

                var complete = FindConfirmationButton(row, original.Id, "仍然完成");
                var completeCancel = FindConfirmationButton(row, original.Id, "取消完成");
                Assert.Null(complete.ContentTemplate);
                Assert.Equal(double.NaN, complete.Width);
                Assert.Equal(32, complete.MinHeight);
                Assert.Equal(new Thickness(10, 4, 10, 4), complete.Padding);
                Assert.Contains(FindVisualChildren<TextBlock>(complete), text => text.Text == "仍然完成");
                Assert.Contains(FindVisualChildren<TextBlock>(completeCancel), text => text.Text == "取消");
                Assert.Same(completeCancel, Keyboard.FocusedElement);
                completeCancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                var more = GetStepMoreButton(row, original.Steps[0].Id);
                GetStepMenuItem(more, "删除步骤")
                    .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                more.ContextMenu!.IsOpen = false;
                view.UpdateLayout();
                PumpDispatcher();

                var delete = GetStepDeleteActionButton(row, original.Steps[0].Id, "确认删除步骤");
                var deleteCancel = GetStepDeleteActionButton(row, original.Steps[0].Id, "取消删除步骤");
                Assert.Null(delete.ContentTemplate);
                Assert.Equal(double.NaN, delete.Width);
                Assert.Equal(32, delete.MinHeight);
                Assert.Equal(new Thickness(10, 4, 10, 4), delete.Padding);
                Assert.Contains(FindVisualChildren<TextBlock>(delete), text => text.Text == "确认删除");
                Assert.Contains(FindVisualChildren<TextBlock>(deleteCancel), text => text.Text == "取消");
                Assert.Same(deleteCancel, Keyboard.FocusedElement);

                deleteCancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(0, repository.SaveCount);
                Assert.Equal(TodoStatus.Planned, repository.Get(original.Id)!.Status);
                Assert.Equal(2, repository.Get(original.Id)!.Steps.Count);
            }
            finally { CloseView(view); }
        });
    }

    [Fact]
    public void Escape_on_parent_completion_confirmation_cancels_from_either_confirmation_button_and_restores_focus()
    {
        StaRun(() =>
        {
            var repository = new FakeTodoRepository();
            var original = ItemWithSteps("completion-escape", 2, 1);
            repository.Items.Add(original);
            var view = ShowView(repository);
            try
            {
                var rows = Assert.IsType<ItemsControl>(view.FindName("ActiveItems"));
                var row = GetRowExpander(rows, original.Id);
                var completion = GetParentCompletionControl(rows, original.Id);

                completion.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
                view.UpdateLayout();
                var cancel = FindConfirmationButton(row, original.Id, "取消完成");
                Keyboard.Focus(cancel);
                var cancelEscape = CreateKeyEvent(view, Key.Escape);
                cancel.RaiseEvent(cancelEscape);
                PumpDispatcher();

                Assert.True(cancelEscape.Handled);
                Assert.Equal(Visibility.Collapsed, FindVisualChildren<StackPanel>(row)
                    .Single(panel => panel.Name == "CompletionConfirmation").Visibility);
                Assert.Equal(0, repository.SaveCount);
                Assert.Same(completion, Keyboard.FocusedElement);

                completion.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
                view.UpdateLayout();
                var confirm = FindConfirmationButton(row, original.Id, "仍然完成");
                Keyboard.Focus(confirm);
                var confirmEscape = CreateKeyEvent(view, Key.Escape);
                confirm.RaiseEvent(confirmEscape);
                PumpDispatcher();

                Assert.True(confirmEscape.Handled);
                Assert.Equal(Visibility.Collapsed, FindVisualChildren<StackPanel>(row)
                    .Single(panel => panel.Name == "CompletionConfirmation").Visibility);
                Assert.Equal(0, repository.SaveCount);
                Assert.Same(completion, Keyboard.FocusedElement);
            }
            finally { CloseView(view); }
        });
    }

    [Fact]
    public void Escape_on_step_delete_confirmation_cancels_from_either_confirmation_button_and_restores_focus()
    {
        StaRun(() =>
        {
            var repository = new FakeTodoRepository();
            var original = ItemWithSteps("delete-escape", 2, 0);
            repository.Items.Add(original);
            var view = ShowView(repository);
            try
            {
                var row = GetRowExpander(Assert.IsType<ItemsControl>(view.FindName("ActiveItems")), original.Id);
                row.IsExpanded = true;
                view.UpdateLayout();
                var more = GetStepMoreButton(row, original.Steps[0].Id);
                GetStepMenuItem(more, "删除步骤").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                more.ContextMenu!.IsOpen = false;
                view.UpdateLayout();

                var cancel = GetStepDeleteActionButton(row, original.Steps[0].Id, "取消删除步骤");
                Keyboard.Focus(cancel);
                var cancelEscape = CreateKeyEvent(view, Key.Escape);
                cancel.RaiseEvent(cancelEscape);
                PumpDispatcher();

                Assert.True(cancelEscape.Handled);
                Assert.Equal(Visibility.Collapsed, GetStepDeleteConfirmationPanel(row, original.Steps[0].Id).Visibility);
                Assert.Equal(0, repository.SaveCount);
                Assert.Same(more, Keyboard.FocusedElement);

                GetStepMenuItem(more, "删除步骤").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                more.ContextMenu!.IsOpen = false;
                view.UpdateLayout();
                var confirm = GetStepDeleteActionButton(row, original.Steps[0].Id, "确认删除步骤");
                Keyboard.Focus(confirm);
                var confirmEscape = CreateKeyEvent(view, Key.Escape);
                confirm.RaiseEvent(confirmEscape);
                PumpDispatcher();

                Assert.True(confirmEscape.Handled);
                Assert.Equal(Visibility.Collapsed, GetStepDeleteConfirmationPanel(row, original.Steps[0].Id).Visibility);
                Assert.Equal(0, repository.SaveCount);
                Assert.Same(more, Keyboard.FocusedElement);
            }
            finally { CloseView(view); }
        });
    }

    [Fact]
    public void Cancelling_step_delete_does_not_write_and_protected_rows_disable_delete()
    {
        StaRun(() =>
        {
            var repository = new FakeTodoRepository();
            var original = ItemWithSteps("delete-cancel", 1, 0);
            var active = ItemWithSteps("delete-active", 1, 0).Activate(DateTimeOffset.UtcNow);
            var completed = ItemWithSteps("delete-completed", 1, 1).Complete(DateTimeOffset.UtcNow);
            repository.Items.Add(original);
            repository.Items.Add(active);
            repository.Items.Add(completed);
            var view = ShowView(repository);
            try
            {
                var row = GetRowExpander(Assert.IsType<ItemsControl>(view.FindName("ActiveItems")), original.Id);
                row.IsExpanded = true;
                view.UpdateLayout();
                var more = GetStepMoreButton(row, original.Steps[0].Id);
                GetStepMenuItem(more, "删除步骤")
                    .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                GetStepDeleteActionButton(row, original.Steps[0].Id, "取消删除步骤")
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(0, repository.SaveCount);

                var activeRow = GetRowExpander(Assert.IsType<ItemsControl>(view.FindName("ActiveItems")), active.Id);
                activeRow.IsExpanded = true;
                view.UpdateLayout();
                Assert.False(GetStepMoreButton(activeRow, active.Steps[0].Id).IsEnabled);

                var completedSection = Assert.IsType<Expander>(view.FindName("CompletedSection"));
                completedSection.IsExpanded = true;
                view.UpdateLayout();
                var completedRow = GetRowExpander(Assert.IsType<ItemsControl>(view.FindName("CompletedItems")), completed.Id);
                completedRow.IsExpanded = true;
                view.UpdateLayout();
                Assert.False(GetStepMoreButton(completedRow, completed.Steps[0].Id).IsEnabled);
            }
            finally { CloseView(view); }
        });
    }

    [Fact]
    public void Moving_steps_updates_only_order_and_keeps_ids_titles_and_completion()
    {
        StaRun(() =>
        {
            var repository = new FakeTodoRepository();
            var original = ItemWithSteps("move-step", 3, 1);
            repository.Items.Add(original);
            var view = ShowView(repository);
            try
            {
                var row = GetRowExpander(Assert.IsType<ItemsControl>(view.FindName("ActiveItems")), original.Id);
                row.IsExpanded = true;
                view.UpdateLayout();
                var middle = GetStepMoreButton(row, original.Steps[1].Id);
                var up = GetStepMenuItem(middle, "上移步骤");
                Assert.True(up.IsEnabled);
                up.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                middle.ContextMenu!.IsOpen = false;

                var saved = repository.Get(original.Id)!;
                Assert.Equal(1, repository.SaveCount);
                Assert.Equal(new[] { original.Steps[1].Id, original.Steps[0].Id, original.Steps[2].Id }, saved.Steps.Select(step => step.Id));
                Assert.Equal(new[] { original.Steps[1].Title, original.Steps[0].Title, original.Steps[2].Title }, saved.Steps.Select(step => step.Title));
                Assert.Equal(new[] { 0, 1, 2 }, saved.Steps.Select(step => step.Order));
                Assert.Equal(new[] { false, true, false }, saved.Steps.Select(step => step.IsCompleted));

                var first = GetStepMoreButton(row, original.Steps[1].Id);
                Assert.False(GetStepMenuItem(first, "上移步骤").IsEnabled);
                var last = GetStepMoreButton(row, original.Steps[2].Id);
                Assert.False(GetStepMenuItem(last, "下移步骤").IsEnabled);
            }
            finally { CloseView(view); }
        });
    }

    [Fact]
    public void Failed_step_reorder_keeps_persisted_order_and_disables_reorder_for_protected_rows()
    {
        StaRun(() =>
        {
            var repository = new FakeTodoRepository { SaveFailure = new IOException("simulated") };
            var original = ItemWithSteps("move-failure", 2, 0);
            var active = ItemWithSteps("move-active", 2, 0).Activate(DateTimeOffset.UtcNow);
            repository.Items.Add(original);
            repository.Items.Add(active);
            var view = ShowView(repository);
            try
            {
                var row = GetRowExpander(Assert.IsType<ItemsControl>(view.FindName("ActiveItems")), original.Id);
                row.IsExpanded = true;
                view.UpdateLayout();
                var more = GetStepMoreButton(row, original.Steps[1].Id);
                var up = GetStepMenuItem(more, "上移步骤");
                Assert.True(up.IsEnabled);
                up.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Assert.Equal(new[] { original.Steps[0].Id, original.Steps[1].Id }, repository.Get(original.Id)!.Steps.Select(step => step.Id));
                Assert.Contains("步骤保存失败", Assert.IsType<TextBlock>(view.FindName("StatusText")).Text);

                var activeRow = GetRowExpander(Assert.IsType<ItemsControl>(view.FindName("ActiveItems")), active.Id);
                activeRow.IsExpanded = true;
                view.UpdateLayout();
                Assert.False(GetStepMenuItem(GetStepMoreButton(activeRow, active.Steps[1].Id), "上移步骤").IsEnabled);
            }
            finally { CloseView(view); }
        });
    }

    [Fact]
    public void Completing_a_parent_with_incomplete_steps_requires_confirmation_and_cancel_writes_nothing()
    {
        StaRun(() =>
        {
            var repository = new FakeTodoRepository();
            var original = ItemWithSteps("complete-confirm", 2, 1);
            repository.Items.Add(original);
            var view = ShowView(repository);
            try
            {
                var rows = Assert.IsType<ItemsControl>(view.FindName("ActiveItems"));
                var row = GetRowExpander(rows, original.Id);
                row.IsExpanded = true;
                view.UpdateLayout();
                GetParentCompletionControl(rows, original.Id).RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
                view.UpdateLayout();

                Assert.Equal(TodoStatus.Planned, repository.Get(original.Id)!.Status);
                Assert.Contains(FindVisualChildren<TextBlock>(row), text => text.Text.Contains("还有 1 步未完成", StringComparison.Ordinal));
                FindVisualChildren<Button>(row).Single(button =>
                    System.Windows.Automation.AutomationProperties.GetName(button) == "取消完成" &&
                    GetTaggedRowId(button.Tag) == original.Id)
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                Assert.Equal(0, repository.SaveCount);
                Assert.Equal(TodoStatus.Planned, repository.Get(original.Id)!.Status);
                Assert.Equal(1, repository.Get(original.Id)!.Steps.Count(step => step.IsCompleted));
            }
            finally { CloseView(view); }
        });
    }

    [Fact]
    public void Confirming_parent_completion_keeps_step_progress_and_reopen_does_not_clear_it()
    {
        StaRun(() =>
        {
            var repository = new FakeTodoRepository();
            var original = ItemWithSteps("complete-confirmed", 2, 1);
            repository.Items.Add(original);
            var view = ShowView(repository);
            try
            {
                var rows = Assert.IsType<ItemsControl>(view.FindName("ActiveItems"));
                var row = GetRowExpander(rows, original.Id);
                GetParentCompletionControl(rows, original.Id).RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
                view.UpdateLayout();
                FindVisualChildren<Button>(row).Single(button =>
                    System.Windows.Automation.AutomationProperties.GetName(button) == "仍然完成" &&
                    GetTaggedRowId(button.Tag) == original.Id)
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                var completed = repository.Get(original.Id)!;
                Assert.Equal(TodoStatus.Completed, completed.Status);
                Assert.Equal(1, completed.Steps.Count(step => step.IsCompleted));

                view.Refresh();
                view.UpdateLayout();
                GetParentCompletionControl(rows, original.Id).RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));

                var reopened = repository.Get(original.Id)!;
                Assert.Equal(TodoStatus.Planned, reopened.Status);
                Assert.Equal(1, reopened.Steps.Count(step => step.IsCompleted));
            }
            finally { CloseView(view); }
        });
    }

    [Fact]
    public void Failed_save_keeps_the_entire_draft_and_editor_visible()
    {
        StaRun(() =>
        {
            var repository = new FakeTodoRepository { SaveFailure = new IOException("simulated") };
            var view = ShowView(repository);
            try
            {
                view.BeginAdd();
                var title = Assert.IsType<TextBox>(view.FindName("TitleInput"));
                var description = Assert.IsType<TextBox>(view.FindName("DescriptionInput"));
                title.Text = "Keep me";
                Click(view, "添加备注");
                description.Text = "1. Keep every step";

                Click(view, "保存");

                Assert.Equal("Keep me", title.Text);
                Assert.Equal("1. Keep every step", description.Text);
                Assert.True(title.IsVisible);
                Assert.Equal(Visibility.Visible, Assert.IsAssignableFrom<FrameworkElement>(view.FindName("OptionalDescriptionPanel")).Visibility);
                Assert.Contains("输入已保留", Assert.IsType<TextBlock>(view.FindName("StatusText")).Text);
            }
            finally { CloseView(view); }
        });
    }

    [Fact]
    public void Empty_title_save_keeps_the_validation_error()
    {
        StaRun(() =>
        {
            var repository = new FakeTodoRepository();
            var view = ShowView(repository);
            try
            {
                view.BeginAdd();
                Click(view, "保存");

                Assert.Empty(repository.Items);
                Assert.Contains("请输入标题", Assert.IsType<TextBlock>(view.FindName("StatusText")).Text);
            }
            finally { CloseView(view); }
        });
    }

    [Fact]
    public void Repeated_save_activation_creates_only_one_todo()
    {
        StaRun(() =>
        {
            var repository = new FakeTodoRepository();
            var view = ShowView(repository);
            try
            {
                view.BeginAdd();
                var title = Assert.IsType<TextBox>(view.FindName("TitleInput"));
                Assert.False(Assert.IsType<Button>(view.FindName("SaveButton")).IsEnabled);
                title.Text = "Only once";
                var save = FindVisualChildren<Button>(view).Single(button => Equals(button.Content, "保存"));
                Assert.True(save.IsEnabled);

                save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                save.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                Assert.Single(repository.Items);
                Assert.Equal(1, repository.SaveCount);
                Assert.Equal("已保存", Assert.IsType<TextBlock>(view.FindName("StatusText")).Text);
                Assert.Equal(Visibility.Collapsed, Assert.IsAssignableFrom<FrameworkElement>(view.FindName("QuickAddEditor")).Visibility);
                Assert.False(save.IsEnabled);

                view.BeginAdd();
                title.Text = "Save again";
                Assert.True(save.IsEnabled);
                save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                Assert.Equal(2, repository.Items.Count);
                Assert.Equal(2, repository.SaveCount);
                Assert.Equal("已保存", Assert.IsType<TextBlock>(view.FindName("StatusText")).Text);
            }
            finally { CloseView(view); }
        });
    }

    [Fact]
    public void New_task_focus_uses_created_id_when_titles_are_the_same()
    {
        StaRun(() =>
        {
            var repository = new FakeTodoRepository();
            repository.Save(Item("same-1", "Same title", "first"));
            repository.Save(Item("same-2", "Same title", "second"));
            var view = ShowView(repository);
            try
            {
                view.BeginAdd();
                Assert.IsType<TextBox>(view.FindName("TitleInput")).Text = "Same title";
                Click(view, "保存");
                PumpDispatcher();

                var created = repository.Items.Single(item => item.Id is not "same-1" and not "same-2");
                var focusedRow = GetFocusedRow(view);
                Assert.NotNull(focusedRow);
                Assert.Equal(created.Id, GetRowItem(focusedRow!).Id);
                Assert.True(GetRowIsHighlighted(focusedRow));
            }
            finally { CloseView(view); }
        });
    }

    [Fact]
    public void New_task_focus_brings_the_row_into_view_after_scrolling_to_end()
    {
        StaRun(() =>
        {
            var repository = new FakeTodoRepository();
            for (var index = 0; index < 28; index++)
                repository.Save(Item("long-" + index, "Existing task " + index, new string('x', 180)));
            var view = ShowView(repository);
            try
            {
                var scroll = FindVisualChildren<ScrollViewer>(view).First();
                scroll.ScrollToEnd();
                view.UpdateLayout();
                Assert.True(scroll.VerticalOffset > 0);

                view.BeginAdd();
                Assert.IsType<TextBox>(view.FindName("TitleInput")).Text = "Created at the end";
                Click(view, "保存");
                PumpDispatcher();

                var created = repository.Items.Single(item => item.Title == "Created at the end");
                var focusedRow = GetFocusedRow(view);
                Assert.NotNull(focusedRow);
                Assert.Equal(created.Id, GetRowItem(focusedRow!).Id);
                Assert.True(scroll.VerticalOffset > 0);
            }
            finally { CloseView(view); }
        });
    }

    [Fact]
    public void Queued_refresh_does_not_replace_the_created_id_focus()
    {
        StaRun(() =>
        {
            var repository = new FakeTodoRepository();
            var view = ShowView(repository);
            try
            {
                view.BeginAdd();
                Assert.IsType<TextBox>(view.FindName("TitleInput")).Text = "Queue target";
                Click(view, "保存");
                var created = repository.Items.Single(item => item.Title == "Queue target");

                view.Refresh();
                PumpDispatcher();

                var focusedRow = GetFocusedRow(view);
                Assert.NotNull(focusedRow);
                Assert.Equal(created.Id, GetRowItem(focusedRow!).Id);
            }
            finally { CloseView(view); }
        });
    }

    [Fact]
    public void FocusTodo_missing_or_deleted_target_reports_status_without_highlighting_it()
    {
        StaRun(() =>
        {
            var repository = new FakeTodoRepository();
            var view = ShowView(repository);
            try
            {
                view.FocusTodo("missing");
                PumpDispatcher();
                Assert.Contains("不存在", Assert.IsType<TextBlock>(view.FindName("StatusText")).Text);

                view.BeginAdd();
                Assert.IsType<TextBox>(view.FindName("TitleInput")).Text = "Delete before focus";
                Click(view, "保存");
                var created = repository.Items.Single(item => item.Title == "Delete before focus");
                repository.Items.RemoveAll(item => item.Id == created.Id);
                view.Refresh();
                PumpDispatcher();

                Assert.DoesNotContain(FindVisualChildren<Border>(view), border => GetRowIsHighlighted(border.DataContext));
                Assert.Contains("不存在", Assert.IsType<TextBlock>(view.FindName("StatusText")).Text);
            }
            finally { CloseView(view); }
        });
    }

    [Fact]
    public void Title_enter_saves_and_escape_cancels_new_draft()
    {
        StaRun(() =>
        {
            var repository = new FakeTodoRepository();
            var view = ShowView(repository);
            try
            {
                view.BeginAdd();
                var title = Assert.IsType<TextBox>(view.FindName("TitleInput"));
                title.Text = "Enter saves";
                view.RaiseEvent(CreateKeyEvent(view, Key.Enter));

                Assert.Single(repository.Items);
                Assert.Equal(Visibility.Collapsed, Assert.IsAssignableFrom<FrameworkElement>(view.FindName("QuickAddEditor")).Visibility);

                view.BeginAdd();
                title.Text = "Draft to cancel";
                var description = Assert.IsType<TextBox>(view.FindName("DescriptionInput"));
                Click(view, "添加备注");
                description.Text = "Do not write";
                view.RaiseEvent(CreateKeyEvent(view, Key.Escape));

                Assert.Single(repository.Items);
                Assert.Equal(string.Empty, title.Text);
                Assert.Equal(string.Empty, description.Text);
                Assert.Equal(Visibility.Collapsed, Assert.IsAssignableFrom<FrameworkElement>(view.FindName("QuickAddEditor")).Visibility);
                Assert.Same(view.FindName("AddTaskButton"), Keyboard.FocusedElement);
            }
            finally { CloseView(view); }
        });
    }

    [Fact]
    public void Description_accepts_return_while_title_enter_is_save_route()
    {
        StaRun(() =>
        {
            var repository = new FakeTodoRepository();
            var view = ShowView(repository);
            try
            {
                view.BeginAdd();
                var title = Assert.IsType<TextBox>(view.FindName("TitleInput"));
                var description = Assert.IsType<TextBox>(view.FindName("DescriptionInput"));
                Assert.False(title.AcceptsReturn);
                Assert.True(description.AcceptsReturn);
                Click(view, "添加备注");
                description.Focus();

                var enter = CreateKeyEvent(view, Key.Enter);
                view.RaiseEvent(enter);

                Assert.False(enter.Handled);
                Assert.Equal(0, repository.SaveCount);
                Assert.Equal(Visibility.Visible, Assert.IsAssignableFrom<FrameworkElement>(view.FindName("QuickAddEditor")).Visibility);
            }
            finally { CloseView(view); }
        });
    }

    [Fact]
    public void Long_chinese_title_and_multiline_remark_remain_editable_without_inner_scrolling()
    {
        StaRun(() =>
        {
            var repository = new FakeTodoRepository();
            var view = ShowView(repository);
            try
            {
                view.BeginAdd();
                var title = Assert.IsType<TextBox>(view.FindName("TitleInput"));
                var description = Assert.IsType<TextBox>(view.FindName("DescriptionInput"));
                title.Text = string.Concat(Enumerable.Repeat("中文任务", 80));
                Click(view, "添加备注");
                description.Text = string.Join("\n", Enumerable.Repeat("第一行备注，第二行仍可继续输入。", 20));

                Assert.True(title.Text.Length > 100);
                Assert.True(description.AcceptsReturn);
                Assert.Equal(ScrollBarVisibility.Disabled, description.VerticalScrollBarVisibility);
                Assert.Equal(Visibility.Visible, Assert.IsAssignableFrom<FrameworkElement>(view.FindName("QuickAddEditor")).Visibility);
            }
            finally { CloseView(view); }
        });
    }

    [Fact]
    public void Ime_processed_key_does_not_submit_or_cancel_the_todo_draft()
    {
        StaRun(() =>
        {
            var repository = new FakeTodoRepository();
            var view = ShowView(repository);
            try
            {
                view.BeginAdd();
                var title = Assert.IsType<TextBox>(view.FindName("TitleInput"));
                title.Text = "输入法草稿";
                var ime = CreateKeyEvent(view, Key.ImeProcessed);
                view.RaiseEvent(ime);

                Assert.Empty(repository.Items);
                Assert.Equal(Visibility.Visible, Assert.IsAssignableFrom<FrameworkElement>(view.FindName("QuickAddEditor")).Visibility);
                Assert.Equal("输入法草稿", title.Text);
            }
            finally { CloseView(view); }
        });
    }

    [Fact]
    public void Confirmation_actions_render_as_text_buttons_instead_of_icon_template_content()
    {
        StaRun(() =>
        {
            var repository = new FakeTodoRepository();
            var todo = ItemWithSteps("confirmation-text", 1, 0);
            repository.Items.Add(todo);
            var view = ShowView(repository);
            try
            {
                var rows = Assert.IsType<ItemsControl>(view.FindName("ActiveItems"));
                var row = GetRowExpander(rows, todo.Id);
                GetParentCompletionControl(rows, todo.Id).RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
                view.UpdateLayout();
                Assert.Equal("仍然完成", FindConfirmationButton(row, "仍然完成").Content);
                Assert.Equal("取消", FindConfirmationButton(row, "取消完成").Content);
                Assert.Null(FindConfirmationButton(row, "仍然完成").ContentTemplate);
                Assert.Null(FindConfirmationButton(row, "取消完成").ContentTemplate);
            }
            finally { CloseView(view); }
        });
    }

    [Fact]
    public void Completing_two_rows_keeps_their_positions_and_allows_in_place_reopen()
    {
        StaRun(() =>
        {
            var repository = new FakeTodoRepository();
            repository.Save(Item("todo-1", "First", null));
            repository.Save(Item("todo-2", "Second", null));
            var view = ShowView(repository);
            try
            {
                var rows = Assert.IsType<ItemsControl>(view.FindName("ActiveItems"));
                var first = GetRowItem(rows.Items[0]).Id;
                var second = GetRowItem(rows.Items[1]).Id;

                ClickCompletion(rows, first);
                Assert.Equal(2, rows.Items.Count);
                Assert.Equal(first, GetRowItem(rows.Items[0]).Id);
                Assert.True(GetRowIsCompleted(rows.Items[0]));
                Assert.True(GetCompletionControl(rows, first).IsEnabled);
                Assert.Empty(Assert.IsType<ItemsControl>(view.FindName("CompletedItems")).Items);
                Assert.Equal(Visibility.Collapsed, Assert.IsType<TextBlock>(view.FindName("EmptyText")).Visibility);

                ClickCompletion(rows, second);
                Assert.Equal(2, rows.Items.Count);
                Assert.True(GetRowIsCompleted(rows.Items[1]));

                ClickCompletion(rows, first);
                Assert.Equal(first, GetRowItem(rows.Items[0]).Id);
                Assert.False(GetRowIsCompleted(rows.Items[0]));
                Assert.Equal(TodoStatus.Planned, repository.Get(first)!.Status);
                Assert.Empty(Assert.IsType<ItemsControl>(view.FindName("CompletedItems")).Items);
            }
            finally { CloseView(view); }
        });
    }

    [Fact]
    public void Leaving_and_reentering_discards_session_retention_and_regroups_by_persisted_state()
    {
        StaRun(() =>
        {
            var repository = new FakeTodoRepository();
            repository.Save(Item("todo-1", "Done later", null));
            var view = ShowView(repository);
            try
            {
                var rows = Assert.IsType<ItemsControl>(view.FindName("ActiveItems"));
                ClickCompletion(rows, "todo-1");
                Assert.Single(rows.Items);
            }
            finally { CloseView(view); }

            var reopenedView = ShowView(repository);
            try
            {
                Assert.Empty(Assert.IsType<ItemsControl>(reopenedView.FindName("ActiveItems")).Items);
                Assert.Single(Assert.IsType<ItemsControl>(reopenedView.FindName("CompletedItems")).Items);
                Assert.Equal(Visibility.Visible, Assert.IsType<TextBlock>(reopenedView.FindName("EmptyText")).Visibility);
            }
            finally { CloseView(reopenedView); }
        });
    }

    [Fact]
    public void Completed_history_row_can_be_reopened_without_the_old_completion_snapshot()
    {
        StaRun(() =>
        {
            var repository = new FakeTodoRepository();
            var completed = Item("todo-history", "History item", null).Complete(DateTimeOffset.UtcNow);
            repository.Save(completed);
            var view = ShowView(repository);
            try
            {
                var history = Assert.IsType<ItemsControl>(view.FindName("CompletedItems"));
                Assert.IsType<Expander>(view.FindName("CompletedSection")).IsExpanded = true;
                view.UpdateLayout();
                Assert.True(GetCompletionControl(history, completed.Id).IsEnabled);
                ClickCompletion(history, completed.Id);

                Assert.Single(Assert.IsType<ItemsControl>(view.FindName("ActiveItems")).Items);
                Assert.Empty(history.Items);
                Assert.Equal(TodoStatus.Planned, repository.Get(completed.Id)!.Status);
            }
            finally { CloseView(view); }
        });
    }

    [Fact]
    public void Completed_row_hides_editing_controls_but_keeps_steps_progress_and_reopen()
    {
        StaRun(() =>
        {
            var repository = new FakeTodoRepository();
            var completed = ItemWithSteps("completed-controls", 2, 1).Complete(DateTimeOffset.UtcNow);
            repository.Save(completed);
            var view = ShowView(repository);
            try
            {
                var history = Assert.IsType<ItemsControl>(view.FindName("CompletedItems"));
                Assert.IsType<Expander>(view.FindName("CompletedSection")).IsExpanded = true;
                view.UpdateLayout();
                var row = GetRowExpander(history, completed.Id);
                row.IsExpanded = true;
                view.UpdateLayout();

                Assert.Contains(FindVisualChildren<TextBlock>(row), text => text.Text == "1 / 2 步");
                Assert.Equal(2, FindVisualChildren<CheckBox>(row).Count(IsStepCheckBox));
                Assert.All(FindVisualChildren<Button>(row).Where(button =>
                    System.Windows.Automation.AutomationProperties.GetName(button) is "编辑步骤" or "更多步骤操作" or "添加步骤" or "编辑"),
                    button => Assert.Equal(Visibility.Collapsed, button.Visibility));
                Assert.True(GetParentCompletionControl(history, completed.Id).IsEnabled);

                GetParentCompletionControl(history, completed.Id).RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
                view.Refresh();
                view.UpdateLayout();
                var active = Assert.IsType<ItemsControl>(view.FindName("ActiveItems"));
                var reopened = GetRowExpander(active, completed.Id);
                reopened.IsExpanded = true;
                view.UpdateLayout();
                Assert.Contains(FindVisualChildren<Button>(reopened), button =>
                    System.Windows.Automation.AutomationProperties.GetName(button) == "编辑" && button.Visibility == Visibility.Visible);
                Assert.Contains(FindVisualChildren<Button>(reopened), button =>
                    System.Windows.Automation.AutomationProperties.GetName(button) == "添加步骤" && button.Visibility == Visibility.Visible);
                Assert.Contains(FindVisualChildren<Button>(reopened), button =>
                    System.Windows.Automation.AutomationProperties.GetName(button) == "编辑步骤" && button.Visibility == Visibility.Visible);
            }
            finally { CloseView(view); }
        });
    }

    [Fact]
    public void Parent_actions_are_at_details_top_and_empty_description_is_collapsed()
    {
        StaRun(() =>
        {
            var repository = new FakeTodoRepository();
            var todo = ItemWithSteps("parent-actions", 1, 0);
            repository.Save(todo);
            var view = ShowView(repository);
            try
            {
                var rows = Assert.IsType<ItemsControl>(view.FindName("ActiveItems"));
                var row = GetRowExpander(rows, todo.Id);
                row.IsExpanded = true;
                view.UpdateLayout();

                var parentActions = FindVisualChildren<FrameworkElement>(row).Single(element => element.Name == "ParentActions");
                var stepItems = FindVisualChildren<ItemsControl>(row).Single(control => control.Name == "StepItems");
                Assert.True(parentActions.TranslatePoint(new Point(0, 0), row).Y < stepItems.TranslatePoint(new Point(0, 0), row).Y);
                Assert.Equal(Visibility.Collapsed, FindVisualChildren<TextBlock>(row).Single(text => text.Name == "DescriptionText").Visibility);
                Assert.Equal("添加步骤", System.Windows.Automation.AutomationProperties.GetName(GetAddStepButton(row)));
                Assert.Equal("添加任务", System.Windows.Automation.AutomationProperties.GetName(FindButton(view, "添加任务")));
            }
            finally { CloseView(view); }
        });
    }

    [Fact]
    public void Completion_write_failure_keeps_the_real_planned_row_and_does_not_retain_it()
    {
        StaRun(() =>
        {
            var repository = new FakeTodoRepository { SaveFailure = new IOException("simulated") };
            repository.Items.Add(Item("todo-failure", "Cannot finish", null));
            var view = ShowView(repository);
            try
            {
                var rows = Assert.IsType<ItemsControl>(view.FindName("ActiveItems"));
                ClickCompletion(rows, "todo-failure");

                Assert.Equal(TodoStatus.Planned, repository.Get("todo-failure")!.Status);
                Assert.Single(rows.Items);
                Assert.False(GetRowIsCompleted(rows.Items[0]));
                Assert.Empty(Assert.IsType<ItemsControl>(view.FindName("CompletedItems")).Items);
                Assert.Contains("保存失败", Assert.IsType<TextBlock>(view.FindName("StatusText")).Text);
            }
            finally { CloseView(view); }
        });
    }

    [Fact]
    public void Active_and_unknown_execution_rows_remain_disabled_for_completion_or_reopen()
    {
        StaRun(() =>
        {
            var repository = new FakeTodoRepository();
            var active = Item("todo-active", "Active work", null).Activate(DateTimeOffset.UtcNow);
            var unknown = Item("todo-unknown", "Unknown work", null).Complete(DateTimeOffset.UtcNow);
            repository.Items.Add(active);
            repository.Items.Add(unknown);
            var agents = new FakeAgentRepository(new AgentExecution(
                "execution-unknown", unknown.Id, "codex", "source-1", "task-1", "dispatch-1",
                DateTimeOffset.UtcNow, AgentExecutionStatus.DispatchOutcomeUnknown));
            var view = ShowView(repository, agents);
            try
            {
                var activeRows = Assert.IsType<ItemsControl>(view.FindName("ActiveItems"));
                var historyRows = Assert.IsType<ItemsControl>(view.FindName("CompletedItems"));
                Assert.IsType<Expander>(view.FindName("CompletedSection")).IsExpanded = true;
                view.UpdateLayout();
                Assert.False(GetCompletionControl(activeRows, active.Id).IsEnabled);
                Assert.False(GetCompletionControl(historyRows, unknown.Id).IsEnabled);
            }
            finally { CloseView(view); }
        });
    }

    [Fact]
    public void Todo_row_starts_compact_and_keeps_details_and_actions_in_its_expansion()
    {
        StaRun(() =>
        {
            var repository = new FakeTodoRepository();
            repository.Save(Item("todo-1", "A long title that must wrap instead of overflowing", "Readable details"));
            var view = ShowView(repository);
            try
            {
                var rows = Assert.IsType<ItemsControl>(view.FindName("ActiveItems"));
                var row = FindVisualChildren<Expander>(rows).Single();
                var title = FindVisualChildren<TextBlock>(row).Single(text => text.Text.StartsWith("A long title"));

                Assert.False(row.IsExpanded);
                Assert.Equal(TextWrapping.Wrap, title.TextWrapping);

                row.IsExpanded = true;
                view.UpdateLayout();

                Assert.Contains(FindVisualChildren<TextBlock>(row), text => text.Text == "Readable details" && text.TextWrapping == TextWrapping.Wrap);
                Assert.Contains(FindVisualChildren<Button>(row), button => System.Windows.Automation.AutomationProperties.GetName(button) == "复制任务说明");
                Assert.Contains(FindVisualChildren<Button>(row), button => System.Windows.Automation.AutomationProperties.GetName(button) == "编辑");
                var more = FindButton(row, "更多操作");
                Assert.NotNull(more.ContextMenu);
                more.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpDispatcher();
                Assert.True(more.ContextMenu.IsOpen);
                var delete = Assert.IsType<MenuItem>(Assert.Single(more.ContextMenu.Items));
                Assert.Equal("删除待办", System.Windows.Automation.AutomationProperties.GetName(delete));
                more.ContextMenu.IsOpen = false;
            }
            finally { CloseView(view); }
        });
    }

    [Fact]
    public void Unknown_execution_remains_visible_and_blocks_mutating_row_actions()
    {
        StaRun(() =>
        {
            var repository = new FakeTodoRepository();
            var todo = Item("todo-protected", "Protected work", "Check the original task");
            repository.Save(todo);
            var agents = new FakeAgentRepository(new AgentExecution(
                "execution-1", todo.Id, "codex", "source-1", "task-1", "dispatch-1",
                DateTimeOffset.UtcNow, AgentExecutionStatus.DispatchOutcomeUnknown));
            var view = ShowView(repository, agents);
            try
            {
                var rows = Assert.IsType<ItemsControl>(view.FindName("ActiveItems"));
                var row = FindVisualChildren<Expander>(rows).Single();
                row.IsExpanded = true;
                view.UpdateLayout();

                Assert.False(FindVisualChildren<CheckBox>(rows).Single().IsEnabled);
                Assert.False(FindButton(row, "编辑").IsEnabled);
                var more = FindButton(row, "更多操作");
                more.ContextMenu!.PlacementTarget = more;
                more.ContextMenu!.IsOpen = true;
                PumpDispatcher();
                Assert.False(Assert.IsType<MenuItem>(Assert.Single(more.ContextMenu.Items)).IsEnabled);
                more.ContextMenu.IsOpen = false;
                Assert.Contains(FindVisualChildren<TextBlock>(row), text => text.Text == "待核对");
                Assert.DoesNotContain(FindVisualChildren<Expander>(view), expander => Equals(expander.Header, "Agent 兼容记录"));
                Assert.DoesNotContain(FindVisualChildren<Expander>(view), expander => Equals(expander.Header, "执行记录与恢复"));
            }
            finally { CloseView(view); }
        });
    }

    [Fact]
    public void Editing_a_middle_row_stays_inline_and_preserves_scroll_context()
    {
        StaRun(() =>
        {
            var repository = new FakeTodoRepository();
            for (var index = 0; index < 30; index++)
                repository.Items.Add(Item("edit-" + index, "Task " + index, "Details " + index));
            var view = ShowView(repository);
            try
            {
                var scroll = FindVisualChildren<ScrollViewer>(view).First();
                scroll.ScrollToVerticalOffset(240);
                view.UpdateLayout();
                Assert.True(scroll.VerticalOffset > 0);

                var rows = Assert.IsType<ItemsControl>(view.FindName("ActiveItems"));
                var target = GetRowItem(rows.Items[15]);
                var row = GetRowExpander(rows, target.Id);
                row.IsExpanded = true;
                view.UpdateLayout();
                FindButton(row, "编辑")
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpDispatcher();

                Assert.Equal(Visibility.Collapsed, Assert.IsAssignableFrom<FrameworkElement>(view.FindName("QuickAddEditor")).Visibility);
                var editors = FindVisualChildren<TextBox>(row).Where(textBox => textBox.Tag is not null).ToArray();
                Assert.Equal(2, editors.Length);
                editors[0].Text = "Edited middle task";
                editors[1].Text = "Updated remarks";
                var offset = scroll.VerticalOffset;
                FindVisualChildren<Button>(row).Single(button =>
                    System.Windows.Automation.AutomationProperties.GetName(button) == "保存编辑")
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                Assert.Equal("Edited middle task", repository.Get(target.Id)!.Title);
                Assert.Equal("Updated remarks", repository.Get(target.Id)!.Description);
                Assert.True(offset > 0);
                Assert.True(scroll.VerticalOffset > 0);
            }
            finally { CloseView(view); }
        });
    }

    [Fact]
    public void Cancelling_row_edit_does_not_write_and_returns_to_the_same_row()
    {
        StaRun(() =>
        {
            var repository = new FakeTodoRepository();
            repository.Items.Add(Item("edit-cancel", "Original", "Original remarks"));
            var view = ShowView(repository);
            try
            {
                var row = GetRowExpander(Assert.IsType<ItemsControl>(view.FindName("ActiveItems")), "edit-cancel");
                row.IsExpanded = true;
                view.UpdateLayout();
                FindButton(row, "编辑")
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpDispatcher();
                var editors = FindVisualChildren<TextBox>(row).Where(textBox => textBox.Tag is not null).ToArray();
                editors[0].Text = "Discarded";
                editors[1].Text = "Discarded remarks";
                FindVisualChildren<Button>(row).Single(button =>
                    System.Windows.Automation.AutomationProperties.GetName(button) == "取消编辑")
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                Assert.Equal(0, repository.SaveCount);
                Assert.Equal("Original", repository.Get("edit-cancel")!.Title);
                Assert.Equal("已取消编辑", Assert.IsType<TextBlock>(view.FindName("StatusText")).Text);
                Assert.Equal(Visibility.Collapsed,
                    FindVisualChildren<StackPanel>(row).Single(panel => panel.Name == "RowEditor").Visibility);
            }
            finally { CloseView(view); }
        });
    }

    [Fact]
    public void Failed_row_edit_keeps_the_draft_visible()
    {
        StaRun(() =>
        {
            var repository = new FakeTodoRepository { SaveFailure = new IOException("simulated") };
            repository.Items.Add(Item("edit-failure", "Original", "Original remarks"));
            var view = ShowView(repository);
            try
            {
                var row = GetRowExpander(Assert.IsType<ItemsControl>(view.FindName("ActiveItems")), "edit-failure");
                row.IsExpanded = true;
                view.UpdateLayout();
                FindButton(row, "编辑")
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpDispatcher();
                var editors = FindVisualChildren<TextBox>(row).Where(textBox => textBox.Tag is not null).ToArray();
                editors[0].Text = "Keep this draft";
                editors[1].Text = "Keep these remarks";
                FindVisualChildren<Button>(row).Single(button =>
                    System.Windows.Automation.AutomationProperties.GetName(button) == "保存编辑")
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                Assert.Contains("保存失败", Assert.IsType<TextBlock>(view.FindName("StatusText")).Text);
                Assert.Equal("Keep this draft", editors[0].Text);
                Assert.Equal("Keep these remarks", editors[1].Text);
                Assert.Equal("Original", repository.Get("edit-failure")!.Title);
            }
            finally { CloseView(view); }
        });
    }

    [Fact]
    public void External_update_or_new_execution_is_not_overwritten_by_an_open_row_draft()
    {
        StaRun(() =>
        {
            var repository = new FakeTodoRepository();
            var original = Item("edit-external", "Original", "Original remarks");
            repository.Items.Add(original);
            var view = ShowView(repository);
            try
            {
                var row = GetRowExpander(Assert.IsType<ItemsControl>(view.FindName("ActiveItems")), original.Id);
                row.IsExpanded = true;
                view.UpdateLayout();
                FindButton(row, "编辑")
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpDispatcher();
                var editors = FindVisualChildren<TextBox>(row).Where(textBox => textBox.Tag is not null).ToArray();
                editors[0].Text = "Local draft";
                var external = new TodoItem(original.Id, "External update", original.Description, original.Priority,
                    original.DueAt, original.CreatedAt, original.UpdatedAt.AddMinutes(1));
                repository.Items[0] = external;
                view.Refresh();
                FindVisualChildren<Button>(row).Single(button =>
                    System.Windows.Automation.AutomationProperties.GetName(button) == "保存编辑")
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                Assert.Equal(external, repository.Get(original.Id));
                Assert.Contains("变化或受保护", Assert.IsType<TextBlock>(view.FindName("StatusText")).Text);
                Assert.Equal("Local draft", editors[0].Text);
            }
            finally { CloseView(view); }
        });
    }

    [Fact]
    public void Protected_row_rejects_an_open_edit_without_overwriting_the_protected_state()
    {
        StaRun(() =>
        {
            var repository = new FakeTodoRepository();
            var original = Item("edit-protected", "Original", "Original remarks");
            repository.Items.Add(original);
            var view = ShowView(repository);
            try
            {
                var row = GetRowExpander(Assert.IsType<ItemsControl>(view.FindName("ActiveItems")), original.Id);
                row.IsExpanded = true;
                view.UpdateLayout();
                FindButton(row, "编辑")
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpDispatcher();
                var editors = FindVisualChildren<TextBox>(row).Where(textBox => textBox.Tag is not null).ToArray();
                editors[0].Text = "Must not overwrite";
                var protectedTodo = original.Activate(DateTimeOffset.UtcNow);
                repository.Items[0] = protectedTodo;
                view.Refresh();
                FindVisualChildren<Button>(row).Single(button =>
                    System.Windows.Automation.AutomationProperties.GetName(button) == "保存编辑")
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                Assert.Equal(protectedTodo, repository.Get(original.Id));
                Assert.Contains("变化或受保护", Assert.IsType<TextBlock>(view.FindName("StatusText")).Text);
                Assert.Equal("Must not overwrite", editors[0].Text);
            }
            finally { CloseView(view); }
        });
    }

    [Fact]
    public void Switching_rows_or_beginning_add_keeps_the_existing_row_draft()
    {
        StaRun(() =>
        {
            var repository = new FakeTodoRepository();
            repository.Items.Add(Item("edit-one", "First", null));
            repository.Items.Add(Item("edit-two", "Second", null));
            var view = ShowView(repository);
            try
            {
                var rows = Assert.IsType<ItemsControl>(view.FindName("ActiveItems"));
                var first = GetRowExpander(rows, "edit-one");
                first.IsExpanded = true;
                view.UpdateLayout();
                FindButton(first, "编辑")
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpDispatcher();
                var editors = FindVisualChildren<TextBox>(first).Where(textBox => textBox.Tag is not null).ToArray();
                editors[0].Text = "Retained draft";

                var second = GetRowExpander(rows, "edit-two");
                second.IsExpanded = true;
                view.UpdateLayout();
                FindButton(second, "编辑")
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                view.BeginAdd();

                Assert.Equal("Retained draft", editors[0].Text);
                Assert.Equal(Visibility.Collapsed, Assert.IsAssignableFrom<FrameworkElement>(view.FindName("QuickAddEditor")).Visibility);
                Assert.Contains("先保存或取消", Assert.IsType<TextBlock>(view.FindName("StatusText")).Text);
            }
            finally { CloseView(view); }
        });
    }

    [Fact]
    public void Row_editor_long_remarks_disable_inner_scrolling_for_the_main_list()
    {
        StaRun(() =>
        {
            var repository = new FakeTodoRepository();
            repository.Items.Add(Item("edit-long", "Long remarks", string.Join("\n", Enumerable.Repeat("A long remark that wraps in the list.", 80))));
            var view = ShowView(repository);
            try
            {
                var row = GetRowExpander(Assert.IsType<ItemsControl>(view.FindName("ActiveItems")), "edit-long");
                row.IsExpanded = true;
                view.UpdateLayout();
                FindButton(row, "编辑")
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpDispatcher();
                var description = FindVisualChildren<TextBox>(row).Single(textBox =>
                    System.Windows.Automation.AutomationProperties.GetName(textBox) == "待办备注");

                Assert.Equal(ScrollBarVisibility.Disabled, description.VerticalScrollBarVisibility);
                Assert.Contains(FindVisualChildren<ScrollViewer>(view), scrollViewer => scrollViewer.VerticalScrollBarVisibility == ScrollBarVisibility.Auto);
            }
            finally { CloseView(view); }
        });
    }

    [Fact]
    public void Top_feedback_is_compact_and_undo_is_an_icon_action()
    {
        StaRun(() =>
        {
            var repository = new FakeTodoRepository();
            repository.Items.Add(Item("feedback", "Feedback", null));
            var view = ShowView(repository);
            try
            {
                var status = Assert.IsType<TextBlock>(view.FindName("StatusText"));
                var undo = Assert.IsType<Button>(view.FindName("UndoButton"));

                Assert.True(status.FontSize <= 14, $"feedback should be compact, actual font size: {status.FontSize}");
                Assert.NotNull(undo.ContentTemplate);
                Assert.Equal("撤销完成", ToolTipService.GetToolTip(undo));
                Assert.Equal("撤销完成", System.Windows.Automation.AutomationProperties.GetName(undo));
            }
            finally { CloseView(view); }
        });
    }

    [Fact]
    public void Feedback_visibility_does_not_move_the_list_start_and_error_remains_visible()
    {
        StaRun(() =>
        {
            var repository = new FakeTodoRepository();
            for (var index = 0; index < 24; index++)
                repository.Items.Add(Item($"feedback-{index}", $"Task {index}", new string('x', 240)));
            var view = ShowView(repository);
            try
            {
                var scroll = FindVisualChildren<ScrollViewer>(view).First();
                scroll.ScrollToVerticalOffset(220);
                view.UpdateLayout();
                var active = Assert.IsType<ItemsControl>(view.FindName("ActiveItems"));
                var before = active.TransformToAncestor(scroll).Transform(new Point(0, 0)).Y;
                var status = Assert.IsType<TextBlock>(view.FindName("StatusText"));
                status.Text = "已完成";
                view.UpdateLayout();
                var afterSuccess = active.TransformToAncestor(scroll).Transform(new Point(0, 0)).Y;
                Assert.Equal(before, afterSuccess, precision: 1);

                status.Text = "保存失败：任务仍有活动执行，请先核对后重试。";
                view.UpdateLayout();
                Assert.Equal(TextWrapping.Wrap, status.TextWrapping);
                Assert.True(status.ActualHeight > 16);
                Assert.True(status.IsArrangeValid);
            }
            finally { CloseView(view); }
        });
    }

    [Fact]
    public void Main_todo_scroll_view_is_a_clipped_viewport_and_refresh_keeps_mid_list_position()
    {
        StaRun(() =>
        {
            var repository = new FakeTodoRepository();
            for (var index = 0; index < 30; index++)
                repository.Items.Add(Item($"boundary-{index}", $"Task {index}", new string('y', 260)));
            var view = ShowView(repository);
            try
            {
                var scroll = FindVisualChildren<ScrollViewer>(view).First();
                Assert.True(scroll.ClipToBounds);
                scroll.ScrollToVerticalOffset(260);
                view.UpdateLayout();
                Assert.True(scroll.VerticalOffset > 0);
                view.Refresh();
                view.UpdateLayout();
                Assert.True(scroll.VerticalOffset > 0);
            }
            finally { CloseView(view); }
        });
    }

    private static TodoWorkspaceView ShowView(FakeTodoRepository repository, IAgentRepository? agents = null)
    {
        var view = new TodoWorkspaceView(new TodoApplicationService(repository, TimeProvider.System, agents), agents);
        var window = new Window { Width = 720, Height = 520, Content = view };
        window.Show();
        view.Refresh();
        view.UpdateLayout();
        return view;
    }

    private static void CloseView(TodoWorkspaceView view)
    {
        var window = Window.GetWindow(view);
        window?.Close();
    }

    private static TodoItem Item(string id, string title, string? description) =>
        new(id, title, description, TodoPriority.Normal, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private static TodoItem ItemWithSteps(string id, int count, int completed)
    {
        var steps = Enumerable.Range(0, count)
            .Select(index => new TodoStep($"{id}-step-{index}", $"步骤 {index + 1}", index, index < completed))
            .ToArray();
        return new TodoItem(id, "任务 " + id, null, TodoPriority.Normal, null,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, steps: steps);
    }

    private static void Click(DependencyObject root, string content) =>
        FindButton(root, content)
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static Button FindButton(DependencyObject root, string label) =>
        FindVisualChildren<Button>(root).Single(button => Equals(button.Content, label) ||
            System.Windows.Automation.AutomationProperties.GetName(button) == label);

    private static void ClickCompletion(ItemsControl rows, string id) =>
        GetCompletionControl(rows, id).RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));

    private static CheckBox GetCompletionControl(ItemsControl rows, string id) =>
        FindVisualChildren<CheckBox>(rows).Single(checkBox => GetRowItem(checkBox.Tag!).Id == id);

    private static CheckBox GetParentCompletionControl(ItemsControl rows, string id) =>
        FindVisualChildren<CheckBox>(rows).Single(checkBox =>
            checkBox.Tag?.GetType().GetProperty("Item")?.GetValue(checkBox.Tag) is TodoItem item && item.Id == id);

    private static CheckBox GetStepCompletionControl(Expander row, string id) =>
        FindVisualChildren<CheckBox>(row).Single(checkBox =>
            checkBox.Tag?.GetType().GetProperty("Item")?.GetValue(checkBox.Tag) is TodoStep step && step.Id == id);

    private static Button GetStepEditButton(Expander row, string id) =>
        FindVisualChildren<Button>(row).Single(button =>
            System.Windows.Automation.AutomationProperties.GetName(button) == "编辑步骤" &&
            button.Tag?.GetType().GetProperty("Item")?.GetValue(button.Tag) is TodoStep step && step.Id == id);

    private static Button GetStepMoreButton(Expander row, string id) =>
        FindVisualChildren<Button>(row).Single(button =>
            System.Windows.Automation.AutomationProperties.GetName(button) == "更多步骤操作" &&
            button.Tag?.GetType().GetProperty("Item")?.GetValue(button.Tag) is TodoStep step && step.Id == id);

    private static MenuItem GetStepMenuItem(Button more, string header)
    {
        Assert.NotNull(more.ContextMenu);
        more.ContextMenu!.PlacementTarget = more;
        more.ContextMenu.IsOpen = true;
        PumpDispatcher();
        return Assert.IsType<MenuItem>(more.ContextMenu.Items.Cast<object>().Single(item =>
            item is MenuItem menu && Equals(menu.Header, header)));
    }

    private static Button GetStepDeleteActionButton(Expander row, string id, string name) =>
        FindVisualChildren<Button>(row).Single(button =>
            System.Windows.Automation.AutomationProperties.GetName(button) == name &&
            button.Tag?.GetType().GetProperty("Item")?.GetValue(button.Tag) is TodoStep step && step.Id == id);

    private static FrameworkElement GetStepDeleteConfirmationPanel(Expander row, string id) =>
        FindVisualChildren<StackPanel>(row).Single(panel =>
            panel.Name == "StepDeleteConfirmation" &&
            panel.DataContext?.GetType().GetProperty("Item")?.GetValue(panel.DataContext) is TodoStep step &&
            step.Id == id);

    private static Button FindConfirmationButton(Expander row, string name) =>
        FindVisualChildren<Button>(row).Single(button =>
            System.Windows.Automation.AutomationProperties.GetName(button) == name);

    private static Button FindConfirmationButton(Expander row, string id, string name) =>
        FindVisualChildren<Button>(row).Single(button =>
            System.Windows.Automation.AutomationProperties.GetName(button) == name &&
            GetTaggedRowId(button.Tag) == id);

    private static Button GetStepActionButton(Expander row, string id, string name) =>
        FindVisualChildren<Button>(row).Single(button =>
            System.Windows.Automation.AutomationProperties.GetName(button) == name &&
            button.Tag?.GetType().GetProperty("Item")?.GetValue(button.Tag) is TodoStep step && step.Id == id);

    private static Button GetAddStepButton(Expander row) =>
        FindVisualChildren<Button>(row).Single(button =>
            System.Windows.Automation.AutomationProperties.GetName(button) == "添加步骤");

    private static Button GetAddStepActionButton(Expander row, string name) =>
        FindVisualChildren<Button>(row).Single(button =>
            System.Windows.Automation.AutomationProperties.GetName(button) == name &&
            button.Tag?.GetType().GetProperty("Item")?.GetValue(button.Tag) is TodoItem);

    private static TextBox GetNewStepTitleInput(Expander row) =>
        FindVisualChildren<TextBox>(row).Single(textBox =>
            System.Windows.Automation.AutomationProperties.GetName(textBox) == "新步骤标题");

    private static FrameworkElement GetNewStepEditor(Expander row) =>
        FindVisualChildren<StackPanel>(row).Single(panel =>
            panel.Name == "AddStepEditor");

    private static TextBox GetStepTitleInput(Expander row, string id) =>
        FindVisualChildren<TextBox>(row).Single(textBox =>
            System.Windows.Automation.AutomationProperties.GetName(textBox) == "步骤标题" &&
            textBox.Tag?.GetType().GetProperty("Item")?.GetValue(textBox.Tag) is TodoStep step && step.Id == id);

    private static bool IsStepCheckBox(CheckBox checkBox) =>
        checkBox.Tag?.GetType().GetProperty("Item")?.GetValue(checkBox.Tag) is TodoStep;

    private static Expander GetRowExpander(ItemsControl rows, string id) =>
        FindVisualChildren<Expander>(rows).Single(expander => GetRowItem(expander.DataContext!).Id == id);

    private static void PumpDispatcher() =>
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private static object? GetFocusedRow(DependencyObject root) =>
        FindVisualChildren<ContentPresenter>(root)
            .Where(presenter => presenter.IsKeyboardFocused
                && presenter.DataContext?.GetType().GetProperty("Item") is not null)
            .Select(presenter => presenter.DataContext)
            .SingleOrDefault();

    private static TodoItem GetRowItem(object row) =>
        (TodoItem)row.GetType().GetProperty("Item")!.GetValue(row)!;

    private static string? GetTaggedRowId(object? tag) =>
        tag?.GetType().GetProperty("Item")?.GetValue(tag) is TodoItem item ? item.Id : null;

    private static bool GetRowIsCompleted(object row) =>
        row.GetType().GetProperty("IsCompleted")?.GetValue(row) is true;

    private static bool GetRowIsHighlighted(object? row) =>
        row?.GetType().GetProperty("IsHighlighted")?.GetValue(row) is true;

    private static KeyEventArgs CreateKeyEvent(TodoWorkspaceView view, Key key) => new(
        Keyboard.PrimaryDevice, PresentationSource.FromVisual(Window.GetWindow(view)!), 0, key)
    { RoutedEvent = Keyboard.PreviewKeyDownEvent };

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            foreach (var child in FindVisualChildren<T>(VisualTreeHelper.GetChild(root, index))) yield return child;
        }
    }

    private static void StaRun(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception error) { failure = error; }
            finally
            {
                var dispatcher = System.Windows.Threading.Dispatcher.FromThread(Thread.CurrentThread);
                if (dispatcher is not null && !dispatcher.HasShutdownStarted) dispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class FakeTodoRepository : ITodoRepository
    {
        public List<TodoItem> Items { get; } = new();
        public int SaveCount { get; private set; }
        public Exception? SaveFailure { get; set; }
        public TodoItem? LastAttempt { get; private set; }

        public void Save(TodoItem todo)
        {
            LastAttempt = todo;
            if (SaveFailure is not null) throw SaveFailure;
            Items.RemoveAll(item => item.Id == todo.Id);
            Items.Add(todo);
            SaveCount++;
        }

        public TodoItem? Get(string id) => Items.SingleOrDefault(item => item.Id == id);
        public IReadOnlyList<TodoItem> List(TodoStatus? status = null) =>
            Items.Where(item => status is null || item.Status == status).ToArray();
        public IReadOnlyList<TodoItem> ListCompletedOn(DateOnly localDate) =>
            Items.Where(item => item.CompletedAt?.ToLocalTime().Date == localDate.ToDateTime(TimeOnly.MinValue).Date).ToArray();
        public void Delete(string id) => Items.RemoveAll(item => item.Id == id);
        public void ClearAgentTodoData() => Items.Clear();
    }

    private sealed class FakeAgentRepository(AgentExecution execution) : IAgentRepository
    {
        public void SaveExecution(AgentExecution value) => throw new NotSupportedException();
        public AgentExecution? GetExecution(string id) => execution.Id == id ? execution : null;
        public AgentExecution? GetExecution(string sourceType, string sourceInstance, string taskId) => execution;
        public AgentExecution? GetLatestExecutionForTodo(string todoId) => execution.TodoId == todoId ? execution : null;
        public IReadOnlyList<AgentExecution> ListNonTerminalExecutions() => [execution];
        public IReadOnlyList<AgentExecution> ListTerminalExecutions(DateTimeOffset endedBefore, int limit) => [];
        public bool HasEventReceipt(string sourceType, string sourceInstance, string taskId, long sequence) => false;
        public AgentEventApplyResult ApplyEvent(AgentEvent agentEvent) => throw new NotSupportedException();
        public void SaveArchiveBatch(AgentArchiveBatch batch) { }
        public AgentArchiveBatch? GetArchiveBatch(string batchId) => null;
        public IReadOnlyList<AgentArchiveBatch> ListIncompleteArchiveBatches() => [];
        public void CompleteArchiveBatch(string batchId, DateTimeOffset completedAt) { }
        public void SaveConnection(PersistedAgentConnection connection, IReadOnlyList<AgentProjectTarget> allowedTargets) { }
        public IReadOnlyList<PersistedAgentConnection> ListConnections() => [];
    }
}
