using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using FgoPet.App.Dialogue;
using FgoPet.App.Panels;
using Xunit;

namespace FgoPet.Windows.Tests.Panels;

[Trait("Category", "WindowsIntegration")]
public sealed class DialoguePanelIntegrationTests
{
    [Fact]
    public void Expanded_dialogue_contains_input_and_controls_without_changing_four_headers()
    {
        StaRun(() =>
        {
            var view = new AttachedPanelView
            {
                DataContext = new AttachedPanelViewModel(TimeProvider.System),
            };
            var dialogue = Assert.IsAssignableFrom<FrameworkElement>(view.FindName("DialogueContent"));

            Assert.NotNull(view.FindName("FocusButton"));
            Assert.NotNull(view.FindName("TodayButton"));
            Assert.NotNull(view.FindName("TodoButton"));
            Assert.NotNull(view.FindName("DialogueButton"));
            Assert.NotNull(view.FindName("DialogueInputBox"));
            Assert.NotNull(view.FindName("SendDialogueButton"));
            Assert.NotNull(view.FindName("StopDialogueButton"));
            Assert.Equal(Visibility.Collapsed, dialogue.Visibility);
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
