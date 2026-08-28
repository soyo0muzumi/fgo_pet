using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using FgoPet.App.Panels;
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
    public void Collapse_button_steps_compact_back_to_collapsed()
    {
        StaRun(() =>
        {
            var model = new AttachedPanelViewModel(TimeProvider.System);
            var view = new AttachedPanelView { DataContext = model };
            var collapse = Assert.IsType<Button>(view.FindName("CollapseButton"));
            model.PortraitClick();

            collapse.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

            Assert.Equal(Core.Panels.AttachedPanelState.Collapsed, model.State);
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
