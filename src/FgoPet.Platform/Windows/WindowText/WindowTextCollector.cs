using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using FgoPet.Platform.WindowText;

namespace FgoPet.Platform.Windows.WindowText;

internal interface IWindowTextNode
{
    bool IsPassword { get; }
    bool IsOffscreen { get; }
    int ProcessId { get; }
    bool IsInScope(int processId) => ProcessId == processId;
    bool IsTextContainer { get; }
    bool IsScrollBar { get; }
    bool IsScrollBarPart { get; }
    IWindowTextNode? FirstChild { get; }
    IWindowTextNode? NextSibling { get; }
    bool IsSameNode(IWindowTextNode other) => ReferenceEquals(this, other);
    string ReadText(int maxLength);
}

internal sealed record WindowTextCollection(string Text, WindowTextReadStatus Status, int VisitedNodes);

internal static class WindowTextCollector
{
    // No FindAll: even a malformed/provider-owned tree is walked incrementally under fixed limits.
    internal static WindowTextCollection Collect(IWindowTextNode root, int processId, CancellationToken token)
    {
        var text = new StringBuilder();
        var visited = 0;
        var partial = false;
        var watch = Stopwatch.StartNew();
        var scanned = Scan(root, 0);
        if (scanned is not null) Read(scanned);
        return new(text.ToString(), partial ? WindowTextReadStatus.Partial : WindowTextReadStatus.Complete, visited);

        bool WithinDeadline()
        {
            token.ThrowIfCancellationRequested();
            if (watch.Elapsed >= WindowTextLimits.ReadDeadline)
            { partial = true; return false; }
            return true;
        }

        bool IsAllowed(IWindowTextNode node) =>
            node.IsInScope(processId) && !node.IsPassword && !node.IsOffscreen;

        ScannedNode? Scan(IWindowTextNode node, int depth, bool inScrollBar = false)
        {
            if (!WithinDeadline()) return null;
            if (visited >= WindowTextLimits.MaxNodes || depth >= WindowTextLimits.MaxDepth)
            { partial = true; return null; }
            visited++;
            var scannedNode = new ScannedNode(node);
            try
            {
                // Do not query name/text, patterns, or descendants before these privacy checks.
                if (!node.IsInScope(processId) || node.IsPassword) return null;
                // Scrollbars contain hidden thumb/page buttons even when the document is visible.
                // Only fully scanned, non-text ScrollBar/Button/Thumb branches are decorations.
                // A hidden Pane, text-bearing part, or unknown child still blocks parent text.
                var decoration = node.IsScrollBar || (inScrollBar && node.IsScrollBarPart);
                if (inScrollBar && !decoration) return null;
                if (node.IsOffscreen && !decoration) return null;
                scannedNode.Allowed = !decoration;
                var child = node.FirstChild;
                scannedNode.IsLeaf = child is null;
                while (child is not null)
                {
                    scannedNode.ObservedChildren.Add(child);
                    var descendant = Scan(child, depth + 1, decoration);
                    if (descendant is not null) scannedNode.Children.Add(descendant);
                    if (descendant is null || !descendant.SubtreeSafe) scannedNode.SubtreeSafe = false;
                    child = child.NextSibling;
                    if (child is not null && (visited >= WindowTextLimits.MaxNodes || !WithinDeadline()))
                    { scannedNode.SubtreeSafe = false; partial = true; break; }
                }
                scannedNode.ChildrenComplete = child is null;
            }
            catch (Exception exception) when (IsProviderFailure(exception))
            { scannedNode.SubtreeSafe = false; scannedNode.IsLeaf = false; partial = true; }
            return scannedNode;
        }

        void Read(ScannedNode scannedNode)
        {
            if (!WithinDeadline() || !scannedNode.Allowed || scannedNode.StructureChanged) return;
            var node = scannedNode.Node;
            try
            {
                // UIA is live. Recheck before content access; never trust a stale password flag.
                if (!IsAllowed(node)) { partial = true; return; }
                if (!StructureMatches(scannedNode)) return;
            }
            catch (Exception exception) when (IsProviderFailure(exception))
            { partial = true; return; }
            try
            {
                if ((scannedNode.IsLeaf || (scannedNode.SubtreeSafe && node.IsTextContainer))
                    && StillSafe(scannedNode) && Append(node)) return;
            }
            catch (Exception exception) when (IsProviderFailure(exception))
            { partial = true; }
            if (scannedNode.StructureChanged) return;
            foreach (var child in scannedNode.Children)
            {
                if (text.Length >= WindowTextLimits.MaxTextChars) { partial = true; break; }
                Read(child);
            }
        }

        bool StillSafe(ScannedNode scannedNode, bool inScrollBar = false)
        {
            if (!WithinDeadline() || !scannedNode.SubtreeSafe) return false;
            try
            {
                var node = scannedNode.Node;
                if (!node.IsInScope(processId) || node.IsPassword) { partial = true; return false; }
                var decoration = node.IsScrollBar || (inScrollBar && node.IsScrollBarPart);
                if ((inScrollBar && !decoration) || (node.IsOffscreen && !decoration))
                { partial = true; return false; }
                if (!StructureMatches(scannedNode)) return false;
                foreach (var child in scannedNode.Children)
                    if (!StillSafe(child, decoration)) return false;
                return true;
            }
            catch (Exception exception) when (IsProviderFailure(exception))
            { scannedNode.StructureChanged = true; partial = true; return false; }
        }

        bool StructureMatches(ScannedNode scannedNode)
        {
            try
            {
                // For a partial scan only the observed prefix can contribute text; do not
                // re-query an already-failed next-sibling link beyond that prefix.
                if (!scannedNode.ChildrenComplete && scannedNode.ObservedChildren.Count == 0) return true;
                var current = scannedNode.Node.FirstChild;
                for (var index = 0; index < scannedNode.ObservedChildren.Count; index++)
                {
                    if (!WithinDeadline() || current is null || !scannedNode.ObservedChildren[index].IsSameNode(current))
                    { scannedNode.StructureChanged = true; partial = true; return false; }
                    if (index + 1 < scannedNode.ObservedChildren.Count || scannedNode.ChildrenComplete)
                        current = current.NextSibling;
                }
                if (scannedNode.ChildrenComplete && current is not null)
                { scannedNode.StructureChanged = true; partial = true; return false; }
                return true;
            }
            catch (Exception exception) when (IsProviderFailure(exception))
            { scannedNode.StructureChanged = true; partial = true; return false; }
        }

        bool Append(IWindowTextNode node)
        {
            var room = WindowTextLimits.MaxTextChars - text.Length - (text.Length == 0 ? 0 : 1);
            if (room <= 0) { partial = true; return true; }
            var value = node.ReadText(room + 1).Trim();
            token.ThrowIfCancellationRequested();
            if (value.Length == 0) return false;
            if (value.Length > room)
            {
                partial = true;
                value = value[..(char.IsHighSurrogate(value[room - 1]) ? room - 1 : room)];
            }
            if (text.Length > 0) text.Append('\n');
            text.Append(value);
            return true;
        }
    }

    private static bool IsProviderFailure(Exception exception) => exception is ElementNotAvailableException
        or InvalidOperationException or COMException or UnauthorizedAccessException or NotSupportedException;

    private sealed class ScannedNode(IWindowTextNode node)
    {
        internal IWindowTextNode Node { get; } = node;
        internal List<ScannedNode> Children { get; } = [];
        internal List<IWindowTextNode> ObservedChildren { get; } = [];
        internal bool ChildrenComplete { get; set; }
        internal bool Allowed { get; set; }
        internal bool IsLeaf { get; set; }
        internal bool SubtreeSafe { get; set; } = true;
        internal bool StructureChanged { get; set; }
    }
}

internal sealed class AutomationWindowTextNode : IWindowTextNode
{
    private readonly AutomationElement element;
    private readonly WindowTextNativeScope? scope;

    internal AutomationWindowTextNode(AutomationElement element, WindowTextNativeScope? scope = null)
    {
        this.element = element;
        this.scope = scope;
    }

    public bool IsSameNode(IWindowTextNode other) => other is AutomationWindowTextNode native
        && Automation.Compare(element, native.element);
    public bool IsPassword => element.Current.IsPassword;
    public bool IsOffscreen => element.Current.IsOffscreen;
    public int ProcessId => element.Current.ProcessId;
    public bool IsInScope(int processId) => scope?.Allows(element) ?? ProcessId == processId;
    public bool IsTextContainer => element.Current.ControlType == ControlType.Document
        || element.Current.ControlType == ControlType.Edit;
    public bool IsScrollBar => element.Current.ControlType == ControlType.ScrollBar && HasNoTextPatterns;
    public bool IsScrollBarPart => (element.Current.ControlType == ControlType.Button
        || element.Current.ControlType == ControlType.Thumb) && HasNoTextPatterns;
    private bool HasNoTextPatterns =>
        element.GetCurrentPropertyValue(AutomationElement.IsTextPatternAvailableProperty) is false
        && element.GetCurrentPropertyValue(AutomationElement.IsValuePatternAvailableProperty) is false;
    public IWindowTextNode? FirstChild => Wrap(TreeWalker.RawViewWalker.GetFirstChild(element));
    public IWindowTextNode? NextSibling => Wrap(TreeWalker.RawViewWalker.GetNextSibling(element));

    public string ReadText(int maxLength)
    {
        var control = element.Current.ControlType;
        if (control != ControlType.Text && control != ControlType.Document && control != ControlType.Edit) return string.Empty;
        if (element.TryGetCurrentPattern(TextPattern.Pattern, out var pattern) && pattern is TextPattern text)
            return text.DocumentRange.GetText(maxLength);
        // Never use ValuePattern: its getter has no length bound and may expose editable secrets.
        // A Text control's accessible name is its display label; Edit/Document get no fallback.
        if (control != ControlType.Text) return string.Empty;
        var name = element.Current.Name ?? string.Empty;
        return name.Length <= maxLength ? name : name[..maxLength];
    }

    private IWindowTextNode? Wrap(AutomationElement? value) => value is null ? null : new AutomationWindowTextNode(value, scope);
}
