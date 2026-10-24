using FgoPet.Platform.WindowText;
using FgoPet.Platform.Windows.WindowText;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using Xunit;
using Xunit.Abstractions;

namespace FgoPet.Windows.Tests;

public sealed class SelectedWindowTextReaderTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Unknown_native_token_fails_closed_without_enumerating_or_reading_a_window()
    {
        var diagnostics = new Diagnostics(output);
        using var reader = new WindowsWindowTextReader(diagnostics);
        var result = await reader.ReadAsync("123456", default);
        Assert.Equal(WindowTextReadStatus.Unavailable, result.Status);
        Assert.Empty(result.Text);
        Assert.Equal(new[] { WindowTextReadStage.Requested, WindowTextReadStage.Completed }, diagnostics.Events.Select(value => value.Stage));
        Assert.Single(diagnostics.Events.Select(value => value.RequestHash).Distinct());
        Assert.All(diagnostics.Events, value => Assert.Matches("^[A-F0-9]{16}$", value.RequestHash));
        Assert.DoesNotContain("123456", System.Text.Json.JsonSerializer.Serialize(diagnostics.Events));
    }

    [Fact]
    public Task Owned_synthetic_Wpf_window_exposes_text_through_production_automation_node() => StaRunner.RunAsync(async () =>
    {
        const string body = "Synthetic accessible text 42";
        const string secret = "Synthetic password must stay private";
        var correlation = Guid.NewGuid().ToString("N")[..16];
        var content = new StackPanel();
        content.Children.Add(new TextBlock { Text = body });
        content.Children.Add(new PasswordBox { Password = secret });
        var window = new Window
        {
            Title = "Synthetic window-text test", Content = content,
            Width = 360, Height = 140, Left = 24, Top = 24,
            ShowInTaskbar = false, ShowActivated = false,
        };
        try
        {
            window.Show();
            window.UpdateLayout();
            var handle = new WindowInteropHelper(window).Handle;
            // Only this test-owned handle is queried. Never enumerate desktop windows.
            Record("synthetic-window-created");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var result = await Task.Run(() =>
            {
                var root = AutomationElement.FromHandle(handle);
                return WindowTextCollector.Collect(new AutomationWindowTextNode(root), Environment.ProcessId, deadline.Token);
            }).WaitAsync(TimeSpan.FromSeconds(10));
            Record("synthetic-window-collected", result.Status, result.Text.Length, result.VisitedNodes);
            Assert.Contains(body, result.Text);
            Assert.DoesNotContain(secret, result.Text);
            Assert.Equal(WindowTextReadStatus.Complete, result.Status);
        }
        finally
        {
            window.Close();
            Record("synthetic-window-closed");
        }

        void Record(string stage, WindowTextReadStatus? status = null, int characterCount = 0, int visitedNodes = 0) =>
            output.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
                { timestampUtc = DateTimeOffset.UtcNow, requestHash = correlation, stage, status, characterCount, visitedNodes }));
    });

    [Fact]
    public void Synthetic_tree_excludes_password_offscreen_and_other_process_subtrees()
    {
        var password = new Node("secret", password: true);
        var hidden = new Node("hidden", offscreen: true);
        var foreign = new Node("foreign", processId: 99);
        var normal = new Node("visible");
        var root = new Node("aggregate must not be read", children: [password, hidden, foreign, normal]);
        var result = WindowTextCollector.Collect(root, 7, default);
        Assert.Equal("visible", result.Text);
        Assert.Equal(0, password.TextReads);
        Assert.Equal(0, hidden.TextReads);
        Assert.Equal(0, foreign.TextReads);
        Assert.Equal(0, root.TextReads);
    }

    [Fact]
    public void Synthetic_leaf_document_text_is_collected()
    {
        var document = new Node("Synthetic document body\nSecond line");
        var result = WindowTextCollector.Collect(document, 7, default);
        Assert.Equal("Synthetic document body\nSecond line", result.Text);
        Assert.Equal(WindowTextReadStatus.Complete, result.Status);
        Assert.Equal(1, document.TextReads);
    }

    [Fact]
    public void Synthetic_document_with_nontext_children_reads_body_once()
    {
        var document = new Node("Synthetic document body", children: [new Node("")], textContainer: true);
        var result = WindowTextCollector.Collect(document, 7, default);
        Assert.Equal("Synthetic document body", result.Text);
        Assert.Equal(1, document.TextReads);
    }

    [Fact]
    public void Safe_document_aggregate_replaces_descendant_text_without_duplicates()
    {
        var leaf = new Node("second line");
        var document = new Node("first line\nsecond line", children: [leaf], textContainer: true);
        var result = WindowTextCollector.Collect(document, 7, default);
        Assert.Equal("first line\nsecond line", result.Text);
        Assert.Equal(0, leaf.TextReads);
    }

    [Theory]
    [InlineData(true, false, 7)]
    [InlineData(false, true, 7)]
    [InlineData(false, false, 99)]
    public void Document_with_excluded_descendant_never_reads_parent_aggregate(bool password, bool offscreen, int pid)
    {
        var excluded = new Node("secret", password: password, offscreen: offscreen, processId: pid);
        var document = new Node("public and secret", children: [new Node("public"), excluded], textContainer: true);
        var result = WindowTextCollector.Collect(document, 7, default);
        Assert.Equal("public", result.Text);
        Assert.Equal(0, document.TextReads);
        Assert.Equal(0, excluded.TextReads);
    }

    [Fact]
    public void Incomplete_document_scan_never_reads_aggregate_but_keeps_safe_leaves()
    {
        var document = new Node("must not be read", textContainer: true,
            children: Enumerable.Range(0, 600).Select(_ => new Node("safe")).ToArray());
        var result = WindowTextCollector.Collect(document, 7, default);
        Assert.Equal(WindowTextReadStatus.Partial, result.Status);
        Assert.Contains("safe", result.Text);
        Assert.Equal(0, document.TextReads);
        Assert.True(result.VisitedNodes <= WindowTextLimits.MaxNodes);
    }

    [Fact]
    public void Empty_or_failed_document_pattern_falls_back_to_safe_child_text()
    {
        foreach (var fail in new[] { false, true })
        {
            var document = new Node("", textContainer: true, failTextRead: fail, children: [new Node("body")]);
            var result = WindowTextCollector.Collect(document, 7, default);
            Assert.Equal("body", result.Text);
            Assert.Equal(fail ? WindowTextReadStatus.Partial : WindowTextReadStatus.Complete, result.Status);
        }
    }

    [Fact]
    public void Hidden_nontext_scroll_parts_do_not_hide_document_body()
    {
        var thumb = new Node("never read decoration", offscreen: true, scrollPart: true);
        var scrollbar = new Node("", scrollBar: true, children: [thumb]);
        var document = new Node("body", textContainer: true, children: [scrollbar]);
        var result = WindowTextCollector.Collect(document, 7, default);
        Assert.Equal("body", result.Text);
        Assert.Equal(0, thumb.TextReads);
    }

    [Theory]
    [InlineData(true, false, 7)]
    [InlineData(false, false, 99)]
    [InlineData(false, true, 7)]
    public void Scrollbar_with_password_foreign_or_unknown_part_blocks_aggregate(bool password, bool unknown, int pid)
    {
        var suspect = new Node("secret", password: password, offscreen: true, processId: pid, scrollPart: !unknown);
        var document = new Node("body and secret", textContainer: true,
            children: [new Node("", scrollBar: true, children: [suspect]), new Node("public")]);
        var result = WindowTextCollector.Collect(document, 7, default);
        Assert.Equal("public", result.Text);
        Assert.Equal(0, document.TextReads);
        Assert.Equal(0, suspect.TextReads);
    }

    [Fact]
    public void Synthetic_nested_password_subtree_is_not_traversed_or_read()
    {
        var secret = new Node("secret descendant");
        var password = new Node("secret aggregate", password: true, children: [secret]);
        var document = new Node("aggregate including secret", children: [password, new Node("public text")]);
        var result = WindowTextCollector.Collect(document, 7, default);
        Assert.Equal("public text", result.Text);
        Assert.Equal(0, document.TextReads);
        Assert.Equal(0, password.ChildReads);
        Assert.Equal(0, password.TextReads);
        Assert.Equal(0, secret.TextReads);
    }

    [Fact]
    public void Synthetic_child_read_failure_marks_partial_without_reading_parent_aggregate()
    {
        var document = new Node("aggregate must not be read", children:
            [new Node("unavailable", failTextRead: true), new Node("public text")]);
        var result = WindowTextCollector.Collect(document, 7, default);
        Assert.Equal("public text", result.Text);
        Assert.Equal(WindowTextReadStatus.Partial, result.Status);
        Assert.Equal(0, document.TextReads);
    }

    [Fact]
    public void Synthetic_tree_caps_nodes_and_marks_partial()
    {
        var root = new Node("", children: Enumerable.Range(0, 600).Select(i => new Node($"line {i}")).ToArray());
        var result = WindowTextCollector.Collect(root, 7, default);
        Assert.Equal(WindowTextReadStatus.Partial, result.Status);
        Assert.DoesNotContain("line 599", result.Text);
        Assert.True(result.VisitedNodes <= WindowTextLimits.MaxNodes);
    }

    [Fact]
    public void Synthetic_text_is_bounded_without_splitting_surrogates()
    {
        var result = WindowTextCollector.Collect(new Node(new string('x', WindowTextLimits.MaxTextChars - 1) + "😀z"), 7, default);
        Assert.True(result.Text.Length <= WindowTextLimits.MaxTextChars);
        Assert.False(char.IsHighSurrogate(result.Text[^1]));
        Assert.Equal(WindowTextReadStatus.Partial, result.Status);
    }

    [Fact]
    public void Cancellation_before_traversal_reads_no_content()
    {
        var root = new Node("must not read");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => WindowTextCollector.Collect(root, 7, cancellation.Token));
        Assert.Equal(0, root.TextReads);
    }

    private sealed class Node(string text, bool password = false, bool offscreen = false, int processId = 7,
        Node[]? children = null, bool failTextRead = false, bool textContainer = false,
        bool scrollBar = false, bool scrollPart = false) : IWindowTextNode
    {
        private readonly Node[] children = children ?? [];
        public int TextReads { get; private set; }
        public int ChildReads { get; private set; }
        public bool IsPassword => password;
        public bool IsOffscreen => offscreen;
        public int ProcessId => processId;
        public bool IsTextContainer => textContainer;
        public bool IsScrollBar => scrollBar;
        public bool IsScrollBarPart => scrollPart;
        public IWindowTextNode? FirstChild { get { ChildReads++; LinkSiblings(); return children.FirstOrDefault(); } }
        public IWindowTextNode? NextSibling { get; private set; }
        public string ReadText(int maxLength)
        {
            TextReads++;
            if (failTextRead) throw new InvalidOperationException("Synthetic provider unavailable.");
            return text.Length > maxLength ? text[..maxLength] : text;
        }
        public void LinkSiblings()
        {
            for (var i = 0; i + 1 < children.Length; i++) children[i].NextSibling = children[i + 1];
            foreach (var child in children) child.LinkSiblings();
        }
    }

    private sealed class Diagnostics(ITestOutputHelper output) : IWindowTextDiagnostics
    {
        public List<WindowTextReadDiagnostic> Events { get; } = [];
        public void Record(WindowTextReadDiagnostic diagnostic)
        {
            Events.Add(diagnostic);
            output.WriteLine(System.Text.Json.JsonSerializer.Serialize(diagnostic));
        }
    }
}
