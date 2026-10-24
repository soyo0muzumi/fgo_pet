using FgoPet.Platform.Windows.WindowText;
using Xunit;

namespace FgoPet.Windows.Tests;

public sealed class WindowTextReadRootTests
{
    private static readonly IntPtr Root = new(10);
    private static readonly IntPtr Renderer = new(20);
    private const string RendererClass = "Chrome_RenderWidgetHostHWND";

    [Fact]
    public void Unique_visible_renderer_child_is_selected_after_complete_enumeration()
    {
        var native = new Native();
        native.Add(Renderer);
        native.Add(new(30), className: "OrdinaryControl");
        Assert.Equal(Renderer, WindowTextReadRoot.Select(Root, native));
        Assert.Equal(2, native.Visited);
    }

    [Theory]
    [InlineData(false, true, RendererClass)]
    [InlineData(true, false, RendererClass)]
    [InlineData(true, true, "Chrome_RenderWidgetHostHWND_fake")]
    [InlineData(true, true, "chrome_renderwidgethosthwnd")]
    public void Hidden_foreign_or_wrong_class_windows_are_not_selected(bool visible, bool child, string className)
    {
        var native = new Native();
        native.Add(Renderer, visible, child, className);
        Assert.Equal(Root, WindowTextReadRoot.Select(Root, native));
    }

    [Fact]
    public void Empty_children_fall_back_to_selected_root()
    {
        Assert.Equal(Root, WindowTextReadRoot.Select(Root, new Native()));
    }

    [Fact]
    public void Zero_root_never_invokes_native_enumeration()
    {
        var native = new Native();
        Assert.Equal(IntPtr.Zero, WindowTextReadRoot.Select(IntPtr.Zero, native));
        Assert.Equal(0, native.EnumerationCalls);
    }

    [Fact]
    public void Selected_root_cannot_be_mistaken_for_its_own_renderer_child()
    {
        var native = new Native();
        native.Add(Root);
        Assert.Equal(Root, WindowTextReadRoot.Select(Root, native));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Multiple_or_duplicate_candidates_are_not_guessed(bool sameHandle)
    {
        var native = new Native();
        native.Add(Renderer);
        native.Add(sameHandle ? Renderer : new IntPtr(30));
        Assert.Equal(Root, WindowTextReadRoot.Select(Root, native));
    }

    [Fact]
    public void Incomplete_enumeration_cannot_authorize_an_observed_unique_candidate()
    {
        var native = new Native { Complete = false };
        native.Add(Renderer);
        Assert.Equal(Root, WindowTextReadRoot.Select(Root, native));
    }

    [Theory]
    [InlineData(400, true)]
    [InlineData(401, false)]
    public void Enumeration_limit_requires_a_complete_tree_with_at_most_400_children(int count, bool selectRenderer)
    {
        var native = new Native();
        native.Add(Renderer);
        for (var index = 1; index < count; index++)
            native.Add(new IntPtr(100 + index), className: "OrdinaryControl");
        Assert.Equal(selectRenderer ? Renderer : Root, WindowTextReadRoot.Select(Root, native));
        Assert.True(native.Visited <= 401);
    }

    [Fact]
    public void Cancellation_before_enumeration_does_not_visit_children()
    {
        var native = new Native();
        native.Add(Renderer);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => WindowTextReadRoot.Select(Root, native, cancellation.Token));
        Assert.Equal(0, native.Visited);
        Assert.Equal(0, native.EnumerationCalls);
    }

    [Fact]
    public void Cancellation_during_enumeration_never_returns_the_candidate()
    {
        using var cancellation = new CancellationTokenSource();
        var native = new Native { BeforeVisit = cancellation.Cancel };
        native.Add(Renderer);
        Assert.Throws<OperationCanceledException>(() => WindowTextReadRoot.Select(Root, native, cancellation.Token));
        Assert.True(native.CallbackReturned);
    }

    [Theory]
    [InlineData("child")]
    [InlineData("visible")]
    [InlineData("class-name")]
    public void Recoverable_metadata_failure_stops_callback_normally_and_falls_back(string failure)
    {
        var native = new Native { Failure = failure };
        native.Add(Renderer);
        native.Add(new IntPtr(30));

        Assert.Equal(Root, WindowTextReadRoot.Select(Root, native));
        Assert.True(native.CallbackReturned);
        Assert.True(native.EnumerationStoppedByCallback);
        Assert.Equal(1, native.Visited);
    }

    private sealed class Native : IWindowTextReadRootNative
    {
        private readonly List<IntPtr> handles = [];
        private readonly Dictionary<IntPtr, (bool Visible, bool Child, string ClassName)> properties = [];
        internal bool Complete { get; init; } = true;
        internal Action? BeforeVisit { get; init; }
        internal string? Failure { get; init; }
        internal int Visited { get; private set; }
        internal int EnumerationCalls { get; private set; }
        internal bool CallbackReturned { get; private set; }
        internal bool EnumerationStoppedByCallback { get; private set; }

        internal void Add(IntPtr handle, bool visible = true, bool child = true, string className = RendererClass)
        {
            handles.Add(handle);
            properties[handle] = (visible, child, className);
        }

        public bool VisitChildren(IntPtr root, Func<IntPtr, bool> visitor)
        {
            EnumerationCalls++;
            Assert.Equal(Root, root);
            foreach (var handle in handles)
            {
                BeforeVisit?.Invoke();
                Visited++;
                var keepGoing = visitor(handle);
                CallbackReturned = true;
                if (!keepGoing)
                {
                    EnumerationStoppedByCallback = true;
                    return false;
                }
            }
            return Complete;
        }

        public bool IsVisible(IntPtr handle)
        {
            if (Failure == "visible") throw new InvalidOperationException("Synthetic visibility query unavailable.");
            return properties[handle].Visible;
        }
        public bool IsChild(IntPtr root, IntPtr child)
        {
            if (Failure == "child") throw new System.ComponentModel.Win32Exception(5, "Synthetic child query denied.");
            return root == Root && properties[child].Child;
        }
        public string GetClassName(IntPtr handle)
        {
            if (Failure == "class-name") throw new UnauthorizedAccessException("Synthetic class query denied.");
            return properties[handle].ClassName;
        }
    }
}
