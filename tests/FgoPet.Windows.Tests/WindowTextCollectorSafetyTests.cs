using FgoPet.Platform.WindowText;
using FgoPet.Platform.Windows.WindowText;
using Xunit;

namespace FgoPet.Windows.Tests;

public sealed class WindowTextCollectorSafetyTests
{
    [Theory]
    [InlineData("first-child")]
    [InlineData("process")]
    [InlineData("password")]
    [InlineData("offscreen")]
    public void Failed_structure_or_safety_property_blocks_ancestor_body_and_keeps_safe_sibling(string failure)
    {
        var failed = new Node("unavailable private body", failure: failure);
        var document = new Node("aggregate must not be read", textContainer: true,
            children: [failed, new Node("public sibling")]);

        var result = WindowTextCollector.Collect(document, 7, default);

        Assert.Equal("public sibling", result.Text);
        Assert.Equal(WindowTextReadStatus.Partial, result.Status);
        Assert.Equal(0, document.TextReads);
        Assert.Equal(0, failed.TextReads);
    }

    [Fact]
    public void Failed_next_sibling_blocks_ancestors_and_preserves_already_scanned_leaf_and_outer_sibling()
    {
        var first = new Node("first safe leaf", failure: "next-sibling");
        var incomplete = new Node("incomplete aggregate must not be read", textContainer: true,
            children: [first, new Node("unvisited leaf")]);
        var document = new Node("outer aggregate must not be read", textContainer: true,
            children: [incomplete, new Node("outer safe sibling")]);

        var result = WindowTextCollector.Collect(document, 7, default);

        Assert.Equal("first safe leaf\nouter safe sibling", result.Text);
        Assert.Equal(WindowTextReadStatus.Partial, result.Status);
        Assert.Equal(0, document.TextReads);
        Assert.Equal(0, incomplete.TextReads);
        Assert.Equal(1, first.TextReads);
    }

    [Fact]
    public void Depth_limit_blocks_all_ancestor_aggregates_and_keeps_shallow_safe_sibling()
    {
        var branch = new Node("unvisited deep text");
        var containers = new List<Node>();
        for (var index = 0; index <= WindowTextLimits.MaxDepth; index++)
        {
            branch = new Node("deep aggregate must not be read", textContainer: true, children: [branch]);
            containers.Add(branch);
        }
        var document = new Node("outer aggregate must not be read", textContainer: true,
            children: [branch, new Node("shallow safe sibling")]);

        var result = WindowTextCollector.Collect(document, 7, default);

        Assert.Equal("shallow safe sibling", result.Text);
        Assert.Equal(WindowTextReadStatus.Partial, result.Status);
        Assert.Equal(0, document.TextReads);
        Assert.All(containers, container => Assert.Equal(0, container.TextReads));
    }

    [Fact]
    public void Node_becoming_password_after_scan_is_never_read()
    {
        var changing = new Node("private after scan", passwordAfterFirstCheck: true);
        var root = new Node("", children: [changing, new Node("safe sibling")]);

        var result = WindowTextCollector.Collect(root, 7, default);

        Assert.Equal("safe sibling", result.Text);
        Assert.Equal(WindowTextReadStatus.Partial, result.Status);
        Assert.Equal(0, changing.TextReads);
    }

    [Fact]
    public void Descendant_becoming_password_after_scan_blocks_parent_aggregate()
    {
        var changing = new Node("private after scan", passwordAfterFirstCheck: true);
        var document = new Node("aggregate containing private after scan", textContainer: true,
            children: [changing, new Node("safe sibling")]);

        var result = WindowTextCollector.Collect(document, 7, default);

        Assert.Equal("safe sibling", result.Text);
        Assert.Equal(WindowTextReadStatus.Partial, result.Status);
        Assert.Equal(0, document.TextReads);
        Assert.Equal(0, changing.TextReads);
    }

    [Fact]
    public void Nonleaf_document_body_truncates_without_splitting_unicode_surrogate_pair()
    {
        var decoration = new Node("");
        var document = new Node(new string('文', WindowTextLimits.MaxTextChars - 1) + "😀尾",
            textContainer: true, children: [decoration]);

        var result = WindowTextCollector.Collect(document, 7, default);

        Assert.Equal(WindowTextReadStatus.Partial, result.Status);
        Assert.Equal(WindowTextLimits.MaxTextChars - 1, result.Text.Length);
        Assert.False(char.IsHighSurrogate(result.Text[^1]));
        Assert.Equal(1, document.TextReads);
        Assert.Equal(0, decoration.TextReads);
    }

    [Fact]
    public void Password_sibling_inserted_after_scan_blocks_document_aggregate()
    {
        var original = new Node("safe existing leaf");
        var insertedPassword = new Node("private inserted password", password: true);
        var document = new Node("aggregate containing private inserted password", textContainer: true,
            children: [original]);
        // The first password check belongs to scanning. The second starts content validation.
        document.OnSecondPasswordCheck = () => document.SetChildren([original, insertedPassword]);

        var result = WindowTextCollector.Collect(document, 7, default);

        Assert.Equal(WindowTextReadStatus.Partial, result.Status);
        Assert.True(result.Text is "" or "safe existing leaf");
        Assert.Equal(0, document.TextReads);
        Assert.Equal(0, insertedPassword.TextReads);
    }

    [Fact]
    public void First_child_replaced_after_scan_blocks_aggregate_and_does_not_read_detached_old_child()
    {
        var detached = new Node("detached old body must not be read");
        var replacement = new Node("private replacement password", password: true);
        var document = new Node("aggregate containing private replacement password", textContainer: true,
            children: [detached]);
        document.OnSecondPasswordCheck = () => document.SetChildren([replacement]);

        var result = WindowTextCollector.Collect(document, 7, default);

        Assert.Equal(WindowTextReadStatus.Partial, result.Status);
        Assert.Empty(result.Text);
        Assert.Equal(0, document.TextReads);
        Assert.Equal(0, detached.TextReads);
        Assert.Equal(0, replacement.TextReads);
    }

    [Fact]
    public void Nontext_root_replacing_child_after_scan_does_not_read_detached_old_child()
    {
        var detached = new Node("detached old body must not be read");
        var replacement = new Node("private replacement password", password: true);
        var root = new Node("", children: [detached]);
        root.OnSecondPasswordCheck = () => root.SetChildren([replacement]);

        var result = WindowTextCollector.Collect(root, 7, default);

        Assert.Equal(WindowTextReadStatus.Partial, result.Status);
        Assert.Empty(result.Text);
        Assert.Equal(0, root.TextReads);
        Assert.Equal(0, detached.TextReads);
        Assert.Equal(0, replacement.TextReads);
    }

    [Fact]
    public void Unsafe_document_replacing_children_after_scan_does_not_read_detached_safe_child()
    {
        var detached = new Node("detached formerly safe body must not be read");
        var originalPassword = new Node("original private password", password: true);
        var replacement = new Node("replacement private password", password: true);
        var document = new Node("aggregate containing private body", textContainer: true,
            children: [detached, originalPassword]);
        document.OnSecondPasswordCheck = () => document.SetChildren([replacement]);

        var result = WindowTextCollector.Collect(document, 7, default);

        Assert.Equal(WindowTextReadStatus.Partial, result.Status);
        Assert.Empty(result.Text);
        Assert.Equal(0, document.TextReads);
        Assert.Equal(0, detached.TextReads);
        Assert.Equal(0, originalPassword.TextReads);
        Assert.Equal(0, replacement.TextReads);
    }

    private sealed class Node : IWindowTextNode
    {
        private readonly string text;
        private readonly string? failure;
        private Node[] children = [];
        private readonly bool passwordAfterFirstCheck;
        private readonly bool password;
        private Node? nextSibling;
        private int passwordChecks;

        internal Node(string text, bool textContainer = false, Node[]? children = null,
            string? failure = null, bool passwordAfterFirstCheck = false, bool password = false)
        {
            this.text = text;
            this.failure = failure;
            this.passwordAfterFirstCheck = passwordAfterFirstCheck;
            this.password = password;
            IsTextContainer = textContainer;
            SetChildren(children ?? []);
        }

        internal Action? OnSecondPasswordCheck { get; set; }

        internal void SetChildren(Node[] value)
        {
            children = value;
            for (var index = 0; index < children.Length; index++)
                children[index].nextSibling = index + 1 < children.Length ? children[index + 1] : null;
        }

        public bool IsPassword
        {
            get
            {
                Fail("password");
                passwordChecks++;
                if (passwordChecks == 2) OnSecondPasswordCheck?.Invoke();
                return password || (passwordAfterFirstCheck && passwordChecks > 1);
            }
        }
        public bool IsOffscreen { get { Fail("offscreen"); return false; } }
        public int ProcessId { get { Fail("process"); return 7; } }
        public bool IsTextContainer { get; }
        public bool IsScrollBar => false;
        public bool IsScrollBarPart => false;
        public IWindowTextNode? FirstChild { get { Fail("first-child"); return children.FirstOrDefault(); } }
        public IWindowTextNode? NextSibling { get { Fail("next-sibling"); return nextSibling; } }
        public int TextReads { get; private set; }

        public string ReadText(int maxLength)
        {
            TextReads++;
            return text.Length > maxLength ? text[..maxLength] : text;
        }

        private void Fail(string operation)
        {
            if (failure == operation) throw new InvalidOperationException("Synthetic provider property unavailable.");
        }
    }
}
