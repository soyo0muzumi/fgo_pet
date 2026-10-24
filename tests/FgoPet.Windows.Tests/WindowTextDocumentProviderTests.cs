using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using FgoPet.Platform.WindowText;
using FgoPet.Platform.Windows.WindowText;
using Xunit;
using Xunit.Abstractions;

namespace FgoPet.Windows.Tests;

/// <summary>Real UIA providers for test-owned documents, without querying unrelated windows.</summary>
public sealed class WindowTextDocumentProviderTests(ITestOutputHelper output)
{
    private const string FirstLine = "合成正文第一行：苹果三个。";
    private const string SecondLine = "合成正文第二行：橙子五个。";
    private const string Password = "synthetic-password-never-collect";
    private const string HiddenText = "synthetic-hidden-never-collect";
    private const string EditorId = "synthetic-document-editor";

    [Fact]
    public Task TextBox_with_visible_scrollbars_reads_multiline_body_and_excludes_private_siblings() =>
        AssertDocumentAsync(() => new TextBox
        {
            Text = FirstLine + "\r\n" + SecondLine,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Visible,
            VerticalScrollBarVisibility = ScrollBarVisibility.Visible,
            Height = 130,
        }, requireRawChildren: true);

    [Fact]
    public Task RichTextBox_with_nested_runs_reads_paragraphs_and_excludes_private_siblings() =>
        AssertDocumentAsync(() =>
        {
            var document = new FlowDocument();
            document.Blocks.Add(new Paragraph(new Span(new Run(FirstLine))));
            document.Blocks.Add(new Paragraph(new Bold(new Span(new Run(SecondLine)))));
            return new RichTextBox(document)
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Visible,
                VerticalScrollBarVisibility = ScrollBarVisibility.Visible,
                Height = 170,
            };
        });

    private Task AssertDocumentAsync(Func<Control> createEditor, bool requireRawChildren = false) => StaRunner.RunAsync(async () =>
    {
        var correlation = Guid.NewGuid().ToString("N")[..16];
        var editor = createEditor();
        AutomationProperties.SetAutomationId(editor, EditorId);
        var content = new StackPanel();
        content.Children.Add(editor);
        content.Children.Add(new PasswordBox { Password = Password, Height = 28 });
        content.Children.Add(new TextBlock { Text = HiddenText, Visibility = Visibility.Collapsed });
        var window = new Window
        {
            Title = "Synthetic document-provider test",
            Content = content,
            Width = 520,
            Height = 280,
            Left = 32,
            Top = 32,
            ShowInTaskbar = false,
            ShowActivated = false,
        };
        try
        {
            Record("fixture-created");
            window.Show();
            window.UpdateLayout();
            var handle = new WindowInteropHelper(window).Handle;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var result = await Task.Run(() =>
            {
                // UIA starts strictly at the handle created above. Never enumerate the desktop.
                var root = AutomationElement.FromHandle(handle);
                RecordProviderShape(root, deadline.Token);
                var document = root.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.AutomationIdProperty, EditorId));
                Assert.NotNull(document);
                var nativeDocument = new AutomationWindowTextNode(document);
                // RichTextBox exposes a leaf document on this provider. TextBox must exercise
                // the non-leaf scrollbar case that originally prevented body collection.
                if (requireRawChildren) Assert.NotNull(nativeDocument.FirstChild);
                Assert.True(document.TryGetCurrentPattern(TextPattern.Pattern, out _));
                return WindowTextCollector.Collect(new AutomationWindowTextNode(root),
                    Environment.ProcessId, deadline.Token);
            }).WaitAsync(TimeSpan.FromSeconds(10));
            Record("collection-completed", result.Status, result.Text.Length, result.VisitedNodes);
            Assert.Equal(WindowTextReadStatus.Complete, result.Status);
            Assert.Contains(FirstLine, result.Text);
            Assert.Contains(SecondLine, result.Text);
            Assert.Contains('\n', result.Text);
            Assert.DoesNotContain(Password, result.Text);
            Assert.DoesNotContain(HiddenText, result.Text);
            // Parent TextPattern and child text must not duplicate the same document body.
            Assert.Equal(1, result.Text.Split(FirstLine, StringSplitOptions.None).Length - 1);
            Assert.Equal(1, result.Text.Split(SecondLine, StringSplitOptions.None).Length - 1);
        }
        finally
        {
            window.Close();
            Record("fixture-closed");
        }

        void RecordProviderShape(AutomationElement root, CancellationToken token)
        {
            var count = 0;
            Visit(root, 0, -1);

            void Visit(AutomationElement element, int depth, int parent)
            {
                token.ThrowIfCancellationRequested();
                if (count >= 100 || depth >= 16) return;
                var index = count++;
                var current = element.Current;
                var sameProcess = current.ProcessId == Environment.ProcessId;
                var isPassword = current.IsPassword;
                var isOffscreen = current.IsOffscreen;
                // Deliberately omit accessible names, values, patterns and body text.
                output.WriteLine(JsonSerializer.Serialize(new
                {
                    timestampUtc = DateTimeOffset.UtcNow,
                    requestHash = correlation,
                    stage = "provider-shape",
                    index,
                    parent,
                    depth,
                    controlType = current.ControlType.ProgrammaticName,
                    sameProcess,
                    isPassword,
                    isOffscreen,
                }));
                if (!sameProcess || isPassword) return;
                var child = TreeWalker.RawViewWalker.GetFirstChild(element);
                while (child is not null && count < 100)
                {
                    token.ThrowIfCancellationRequested();
                    Visit(child, depth + 1, index);
                    child = TreeWalker.RawViewWalker.GetNextSibling(child);
                }
            }
        }

        void Record(string stage, WindowTextReadStatus? status = null, int characterCount = 0, int visitedNodes = 0) =>
            output.WriteLine(JsonSerializer.Serialize(new
            {
                timestampUtc = DateTimeOffset.UtcNow,
                requestHash = correlation,
                stage,
                status,
                characterCount,
                visitedNodes,
            }));
    });
}
