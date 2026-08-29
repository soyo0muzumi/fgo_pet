using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using FgoPet.App.Panels;
using FgoPet.Core.Panels;
using Xunit;

namespace FgoPet.Windows.Tests.Panels;

[Trait("Category", "WindowsIntegration")]
public sealed class AttachedPanelViewIntegrationTests
{
    [Fact]
    public void Compact_and_expanded_states_show_only_their_intended_content()
    {
        StaRun(() =>
        {
            var model = new AttachedPanelViewModel(TimeProvider.System);
            var view = new AttachedPanelView { DataContext = model };
            var compact = Assert.IsType<StackPanel>(view.FindName("CompactActions"));
            var title = Assert.IsType<TextBlock>(view.FindName("PanelTitle"));
            var message = Assert.IsType<TextBlock>(view.FindName("CompactMessage"));
            var dialogue = Assert.IsAssignableFrom<FrameworkElement>(view.FindName("DialogueContent"));
            var todo = Assert.IsAssignableFrom<FrameworkElement>(view.FindName("TodoContent"));

            model.PortraitClick();
            Assert.Equal("CHALDEA LINK", title.Text);
            Assert.False(string.IsNullOrWhiteSpace(message.Text));
            Assert.Equal(Visibility.Visible, message.Visibility);
            Assert.Equal(Visibility.Visible, compact.Visibility);
            Assert.Equal(Visibility.Collapsed, dialogue.Visibility);
            Assert.Equal(Visibility.Collapsed, todo.Visibility);

            model.DialogueClick();
            Assert.Equal(Visibility.Visible, dialogue.Visibility);
            Assert.Equal(Visibility.Collapsed, todo.Visibility);

            model.TodoClick();
            Assert.Equal(Visibility.Collapsed, dialogue.Visibility);
            Assert.Equal(Visibility.Visible, todo.Visibility);
        });
    }

    [Fact]
    public void Header_has_four_phase2_columns_and_no_collapse_button()
    {
        StaRun(() =>
        {
            var view = new AttachedPanelView { DataContext = new AttachedPanelViewModel(TimeProvider.System) };
            Assert.NotNull(view.FindName("FocusButton"));
            Assert.NotNull(view.FindName("TodayButton"));
            Assert.NotNull(view.FindName("TodoButton"));
            Assert.NotNull(view.FindName("DialogueButton"));
            Assert.Null(view.FindName("CollapseButton"));
            Assert.IsAssignableFrom<FrameworkElement>(view.FindName("FocusContent"));
            Assert.IsAssignableFrom<FrameworkElement>(view.FindName("TodayContent"));
            Assert.IsAssignableFrom<FrameworkElement>(view.FindName("CompactTimer"));
        });
    }

    [Fact]
    public void Focus_and_today_states_toggle_only_their_own_content()
    {
        StaRun(() =>
        {
            var model = new AttachedPanelViewModel(TimeProvider.System);
            var view = new AttachedPanelView { DataContext = model };
            var focus = Assert.IsAssignableFrom<FrameworkElement>(view.FindName("FocusContent"));
            var today = Assert.IsAssignableFrom<FrameworkElement>(view.FindName("TodayContent"));
            var todo = Assert.IsAssignableFrom<FrameworkElement>(view.FindName("TodoContent"));
            var dialogue = Assert.IsAssignableFrom<FrameworkElement>(view.FindName("DialogueContent"));
            var message = Assert.IsAssignableFrom<FrameworkElement>(view.FindName("CompactMessage"));
            var timer = Assert.IsAssignableFrom<FrameworkElement>(view.FindName("CompactTimer"));

            model.PortraitClick();
            model.FocusClick();
            Assert.Equal(Visibility.Visible, focus.Visibility);
            Assert.Equal(Visibility.Collapsed, today.Visibility);
            Assert.Equal(Visibility.Collapsed, todo.Visibility);
            Assert.Equal(Visibility.Collapsed, dialogue.Visibility);
            Assert.Equal(Visibility.Collapsed, message.Visibility);
            Assert.Equal(Visibility.Collapsed, timer.Visibility);

            model.TodayClick();
            Assert.Equal(Visibility.Collapsed, focus.Visibility);
            Assert.Equal(Visibility.Visible, today.Visibility);

            model.Escape();
            Assert.Equal(Visibility.Collapsed, focus.Visibility);
            Assert.Equal(Visibility.Collapsed, today.Visibility);
            Assert.Equal(Visibility.Visible, message.Visibility);
        });
    }

    [Fact]
    public void Focus_header_switches_from_expanded_dialogue_without_collapsing()
    {
        StaRun(() =>
        {
            var model = new AttachedPanelViewModel(TimeProvider.System);
            var view = new AttachedPanelView { DataContext = model };
            var focus = Assert.IsAssignableFrom<FrameworkElement>(view.FindName("FocusContent"));
            var dialogue = Assert.IsAssignableFrom<FrameworkElement>(view.FindName("DialogueContent"));

            model.PortraitClick();
            model.DialogueClick();
            model.FocusClick();

            Assert.Equal(AttachedPanelState.ExpandedFocus, model.State);
            Assert.Equal(Visibility.Visible, focus.Visibility);
            Assert.Equal(Visibility.Collapsed, dialogue.Visibility);
        });
    }

    [Fact]
    public void Timer_state_visibility_follows_the_view_model_not_the_panel_state()
    {
        StaRun(() =>
        {
            var model = new AttachedPanelViewModel(TimeProvider.System);
            var view = new AttachedPanelView { DataContext = model };
            var timer = Assert.IsAssignableFrom<FrameworkElement>(view.FindName("CompactTimer"));
            var message = Assert.IsAssignableFrom<FrameworkElement>(view.FindName("CompactMessage"));

            model.PortraitClick();
            Assert.Equal(Visibility.Visible, message.Visibility);
            Assert.Equal(Visibility.Collapsed, timer.Visibility);
        });
    }

    private static void StaRun(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception error)
            {
                failure = error;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
