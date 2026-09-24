using System.IO;
using System.Reflection;
using System.Resources;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using FgoPet.App.Archives;
using FgoPet.App.Dialogue;
using FgoPet.App.ViewModels;
using FgoPet.App.Views;
using FgoPet.Core.Todo;
using Xunit;

namespace FgoPet.Windows.Tests.Dialogue;

[Trait("Category", "WindowsIntegration")]
public sealed class WorkCardCompositionTests
{
    [Fact]
    public void Compiled_xaml_resources_have_exactly_one_owner()
    {
        var assemblies = new[]
        {
            typeof(DialogueWindow).Assembly,
            typeof(ConversationViewModel).Assembly,
            typeof(TodoProposalService).Assembly,
            typeof(ArchiveDraftService).Assembly,
        }.Distinct().ToArray();
        var keys = assemblies.ToDictionary(assembly => assembly.GetName().Name!, ReadResourceKeys);

        Assert.Equal("FgoPet.DesktopShell", typeof(DialogueWindow).Assembly.GetName().Name);
        Assert.Equal("FgoPet.Dialogue", typeof(TodoProposalCard).Assembly.GetName().Name);
        Assert.Equal("FgoPet.Dialogue", typeof(ArchiveDraftCard).Assembly.GetName().Name);
        Assert.Equal("FgoPet.DesktopShell", Assert.Single(keys.Where(pair => pair.Value.Contains("dialogue/dialoguewindow.baml"))).Key);
        Assert.Equal("FgoPet.Dialogue", Assert.Single(keys.Where(pair => pair.Value.Contains("views/todoproposalcard.baml"))).Key);
        Assert.Equal("FgoPet.Dialogue", Assert.Single(keys.Where(pair => pair.Value.Contains("views/archivedraftcard.baml"))).Key);
    }

    [Theory]
    [InlineData("FgoLight")]
    [InlineData("ModernGray")]
    public void Relocated_todo_card_keeps_edit_confirmation_and_bubbling_without_implicit_writes(string theme)
    {
        StaRunner.Run(() =>
        {
            var confirmation = new TodoConfirmation();
            var model = new TodoProposalViewModel(new TodoProposal("Original", "Keep this note"), confirmation);
            var card = Assert.IsType<TodoProposalCard>(Application.LoadComponent(
                new Uri("/FgoPet.Dialogue;component/Views/TodoProposalCard.xaml", UriKind.Relative)));
            card.DataContext = model;
            var window = new Window { Content = card, Width = 500, Height = 480 };
            window.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"/FgoPet.App;component/Themes/{theme}.xaml", UriKind.Relative),
            });
            var routedCount = 0;
            window.AddHandler(TodoProposalCard.ViewTodoRequestedEvent, new RoutedEventHandler((_, args) =>
            {
                Assert.Same(card, args.OriginalSource);
                Assert.Equal("confirmed-todo", model.CreatedTodoId);
                routedCount++;
            }));
            try
            {
                window.Show();
                window.UpdateLayout();
                Assert.Empty(confirmation.Confirmed);
                Assert.Single(Children<Expander>(card)).IsExpanded = true;
                window.UpdateLayout();
                var title = Assert.Single(Children<TextBox>(card).Where(box => AutomationProperties.GetName(box) == "建议标题"));
                title.SetCurrentValue(TextBox.TextProperty, "Edited in the card");
                title.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
                var confirm = FindButton(card, "加入待办");
                confirm.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                window.UpdateLayout();

                Assert.Equal("Edited in the card", Assert.Single(confirmation.Confirmed).Title);
                Assert.True(model.IsAdded);
                Assert.False(model.IsExpanded);
                // A retained button event cannot create another Todo after successful confirmation.
                confirm.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Single(confirmation.Confirmed);
                FindButton(card, "查看待办").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(1, routedCount);
                window.Hide();
                window.Show();
                window.UpdateLayout();
                Assert.Same(model, card.DataContext);
                Assert.Single(confirmation.Confirmed);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Relocated_archive_card_loads_its_baml_and_confirms_only_the_edited_copy()
    {
        StaRunner.Run(() =>
        {
            var confirmation = new ArchiveConfirmation();
            var date = new DateOnly(2026, 1, 2);
            var original = new ArchiveDraft("archive", "test", ["todo"], date, "Original", date, date,
                "Original summary", ["Outcome"], "Bounded input");
            var model = new ArchiveDraftViewModel(original, confirmation);
            var card = Assert.IsType<ArchiveDraftCard>(Application.LoadComponent(
                new Uri("/FgoPet.Dialogue;component/Views/ArchiveDraftCard.xaml", UriKind.Relative)));
            card.DataContext = model;
            var window = new Window { Content = card, Width = 500, Height = 400 };
            try
            {
                window.Show();
                window.UpdateLayout();
                Assert.Empty(confirmation.Confirmed);
                var editors = Children<TextBox>(card).ToArray();
                Assert.Equal(2, editors.Length);
                editors[0].SetCurrentValue(TextBox.TextProperty, "Edited title");
                editors[0].GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
                editors[1].SetCurrentValue(TextBox.TextProperty, "Edited summary");
                editors[1].GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
                Assert.Single(Children<Button>(card)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                Assert.Equal(original with { Title = "Edited title", Summary = "Edited summary" }, Assert.Single(confirmation.Confirmed));
                Assert.Equal("Original", original.Title);
                Assert.Equal("Original summary", original.Summary);
            }
            finally { window.Close(); }
        });
    }

    private static Button FindButton(DependencyObject root, string name) =>
        Assert.Single(Children<Button>(root).Where(button => AutomationProperties.GetName(button) == name));

    private static IEnumerable<T> Children<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T value) yield return value;
            foreach (var descendant in Children<T>(child)) yield return descendant;
        }
    }

    private static HashSet<string> ReadResourceKeys(Assembly assembly)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in assembly.GetManifestResourceNames().Where(name => name.EndsWith(".g.resources", StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new ResourceReader(stream);
            var enumerator = reader.GetEnumerator();
            while (enumerator.MoveNext()) keys.Add((string)enumerator.Key);
        }
        return keys;
    }

    private sealed class TodoConfirmation : ILegacyTodoProposalConfirmation
    {
        public List<TodoProposal> Confirmed { get; } = [];
        public TodoItem Confirm(TodoProposal proposal)
        {
            Confirmed.Add(proposal);
            var now = DateTimeOffset.UtcNow;
            return new TodoItem("confirmed-todo", proposal.Title, proposal.Description, proposal.Priority, proposal.DueAt, now, now);
        }
    }

    private sealed class ArchiveConfirmation : IArchiveDraftConfirmation
    {
        public List<ArchiveDraft> Confirmed { get; } = [];
        public void Confirm(ArchiveDraft draft) => Confirmed.Add(draft);
    }
}
