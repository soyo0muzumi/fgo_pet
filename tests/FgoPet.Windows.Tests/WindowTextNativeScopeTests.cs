using FgoPet.Platform.Windows.WindowText;
using Xunit;

namespace FgoPet.Windows.Tests;

public sealed class WindowTextNativeScopeTests
{
    private static readonly IntPtr Root = new(10);
    private static readonly IntPtr Child = new(20);
    private static readonly IntPtr Other = new(30);

    [Fact]
    public void Foreign_process_requires_a_native_child_of_the_exact_selected_window()
    {
        var native = new Native();
        var scope = new WindowTextNativeScope(Root, 7, native);
        Assert.True(scope.Allows(new Node(8, Child)));
        Assert.False(scope.Allows(new Node(8, Other)));
        Assert.False(scope.Allows(new Node(9, Child)));
        Assert.False(scope.Allows(new Node(8, Root)));
    }

    [Fact]
    public void Handleless_text_can_resolve_same_process_native_child_through_raw_parents()
    {
        var scope = new WindowTextNativeScope(Root, 7, new Native());
        Assert.True(scope.Allows(new Node(8, IntPtr.Zero,
            new Node(8, IntPtr.Zero, new Node(8, Child)))));
        Assert.False(scope.Allows(new Node(8, IntPtr.Zero)));
        Assert.False(scope.Allows(new Node(8, IntPtr.Zero, new Node(7, Child))));
    }

    [Fact]
    public void Unrelated_nearest_handle_cannot_be_skipped_for_an_owned_ancestor()
    {
        var scope = new WindowTextNativeScope(Root, 7, new Native());
        var ownedAncestor = new Node(8, Child);
        var unrelated = new Node(8, Other, ownedAncestor);
        Assert.False(scope.Allows(new Node(8, IntPtr.Zero, unrelated)));
        Assert.Equal(0, unrelated.ParentReads);
    }

    [Fact]
    public void Native_child_ownership_is_rechecked_after_an_earlier_success()
    {
        var native = new Native();
        var scope = new WindowTextNativeScope(Root, 7, native);
        Assert.True(scope.Allows(new Node(8, Child)));
        native.Children.Clear();
        Assert.False(scope.Allows(new Node(8, Child)));
    }

    [Fact]
    public void Root_destruction_pid_change_and_pid_reuse_revoke_same_process_access()
    {
        foreach (var change in new Action<Native>[]
        {
            native => native.Windows.Remove(Root),
            native => native.Windows[Root] = 9,
            native => native.Started[7]++,
        })
        {
            var native = new Native();
            var scope = new WindowTextNativeScope(Root, 7, native);
            Assert.True(scope.Allows(new Node(7, Root)));
            change(native);
            Assert.False(scope.Allows(new Node(7, Root)));
            Assert.False(scope.Allows(new Node(8, Child)));
        }
    }

    [Fact]
    public void Foreign_process_id_reuse_never_inherits_previous_access()
    {
        var native = new Native();
        var scope = new WindowTextNativeScope(Root, 7, native);
        Assert.True(scope.Allows(new Node(8, Child)));
        native.Started[8]++;
        Assert.False(scope.Allows(new Node(8, Child)));
    }

    [Fact]
    public void Invalid_root_at_construction_cannot_later_gain_authority()
    {
        var native = new Native();
        native.Windows.Remove(Root);
        var scope = new WindowTextNativeScope(Root, 7, native);
        native.Windows[Root] = 7;
        Assert.False(scope.Allows(new Node(7, Root)));
        Assert.False(scope.Allows(new Node(8, Child)));
    }

    [Fact]
    public void Parent_traversal_is_bounded_and_never_reaches_a_more_distant_handle()
    {
        IWindowTextScopeNode branch = new Node(8, Child);
        var nodes = new List<Node>();
        for (var index = 0; index < 32; index++)
        {
            var node = new Node(8, IntPtr.Zero, branch);
            nodes.Add(node);
            branch = node;
        }
        var scope = new WindowTextNativeScope(Root, 7, new Native());
        Assert.False(scope.Allows(branch));
        Assert.Equal(31, nodes.Sum(node => node.ParentReads));
    }

    [Fact]
    public void Cyclic_parent_provider_stops_at_the_fixed_limit()
    {
        var cyclic = new Node(8, IntPtr.Zero);
        cyclic.ParentNode = cyclic;
        Assert.False(new WindowTextNativeScope(Root, 7, new Native()).Allows(cyclic));
        Assert.Equal(31, cyclic.ParentReads);
    }

    [Fact]
    public void Property_and_native_permission_failures_deny_access()
    {
        var native = new Native();
        var scope = new WindowTextNativeScope(Root, 7, native);
        Assert.False(scope.Allows(new Node(8, IntPtr.Zero) { FailParent = true }));
        native.Fail = true;
        Assert.False(scope.Allows(new Node(7, Root)));
        Assert.False(scope.Allows(new Node(8, Child)));
    }

    [Theory]
    [InlineData("hidden")]
    [InlineData("reparented")]
    [InlineData("pid-reused")]
    [InlineData("pid-changed")]
    [InlineData("destroyed")]
    public void Content_anchor_revocation_denies_renderer_and_same_process_nodes(string change)
    {
        var native = new Native();
        var scope = new WindowTextNativeScope(Root, 7, native, Child);
        Assert.True(scope.Allows(new Node(8, Child)));
        Assert.True(scope.Allows(new Node(7, Root)));

        ChangeAnchor(native, change);

        Assert.False(scope.Allows(new Node(8, Child)));
        Assert.False(scope.Allows(new Node(7, Root)));
    }

    [Theory]
    [InlineData("hidden")]
    [InlineData("reparented")]
    [InlineData("destroyed")]
    [InlineData("invalid-pid")]
    [InlineData("invalid-start-time")]
    public void Initially_invalid_content_anchor_never_gains_authority_after_repair(string change)
    {
        var native = new Native();
        ChangeAnchor(native, change);
        var scope = new WindowTextNativeScope(Root, 7, native, Child);
        native.Windows[Child] = 8;
        native.Started[8] = 200;
        native.Children.Add(Child);
        native.Visible.Add(Child);

        Assert.False(scope.Allows(new Node(8, Child)));
        Assert.False(scope.Allows(new Node(7, Root)));
    }

    [Fact]
    public void Anchor_in_selected_process_is_still_checked_for_visibility_and_membership()
    {
        foreach (var change in new[] { "hidden", "reparented" })
        {
            var native = new Native();
            native.Windows[Child] = 7;
            var scope = new WindowTextNativeScope(Root, 7, native, Child);
            Assert.True(scope.Allows(new Node(7, Child)));
            ChangeAnchor(native, change);
            Assert.False(scope.Allows(new Node(7, Child)));
        }
    }

    [Fact]
    public void Selected_root_as_content_anchor_keeps_the_standard_root_scope()
    {
        var native = new Native();
        var scope = new WindowTextNativeScope(Root, 7, native, Root);
        Assert.True(scope.Allows(new Node(7, Root)));
        Assert.True(scope.Allows(new Node(8, Child)));
    }

    private static void ChangeAnchor(Native native, string change)
    {
        switch (change)
        {
            case "hidden": native.Visible.Remove(Child); break;
            case "reparented": native.Children.Remove(Child); break;
            case "pid-reused": native.Started[8]++; break;
            case "pid-changed": native.Windows[Child] = 9; break;
            case "destroyed": native.Windows.Remove(Child); break;
            case "invalid-pid": native.Windows[Child] = 0; break;
            case "invalid-start-time": native.Started[8] = 0; break;
            default: throw new ArgumentException("Unknown synthetic anchor change.", nameof(change));
        }
    }

    private sealed class Node(int processId, IntPtr handle, IWindowTextScopeNode? parent = null) : IWindowTextScopeNode
    {
        public int ProcessId => processId;
        public IntPtr NativeWindowHandle => handle;
        internal IWindowTextScopeNode? ParentNode { get; set; } = parent;
        internal bool FailParent { get; init; }
        internal int ParentReads { get; private set; }
        public IWindowTextScopeNode? Parent
        {
            get
            {
                ParentReads++;
                if (FailParent) throw new InvalidOperationException("Synthetic unavailable provider.");
                return ParentNode;
            }
        }
    }

    private sealed class Native : IWindowTextScopeNative
    {
        internal Dictionary<IntPtr, int> Windows { get; } = new() { [Root] = 7, [Child] = 8, [Other] = 8 };
        internal Dictionary<int, long> Started { get; } = new() { [7] = 100, [8] = 200, [9] = 300 };
        internal HashSet<IntPtr> Children { get; } = [Child];
        internal HashSet<IntPtr> Visible { get; } = [Root, Child, Other];
        internal bool Fail { get; set; }
        public bool IsWindow(IntPtr handle) { Check(); return Windows.ContainsKey(handle); }
        public bool IsWindowVisible(IntPtr handle) { Check(); return Visible.Contains(handle); }
        public int GetProcessId(IntPtr handle) { Check(); return Windows.GetValueOrDefault(handle); }
        public bool IsChild(IntPtr parent, IntPtr child) { Check(); return parent == Root && Children.Contains(child); }
        public long GetProcessStartedUtc(int processId) { Check(); return Started.GetValueOrDefault(processId); }
        private void Check()
        {
            if (Fail) throw new UnauthorizedAccessException("Synthetic native access denied.");
        }
    }
}
