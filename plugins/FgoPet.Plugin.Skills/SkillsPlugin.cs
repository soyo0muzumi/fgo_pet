using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using FgoPet.Extensibility;

namespace FgoPet.Plugin.Skills;

/// <summary>Static, instruction-only skills for the native agent runtime.</summary>
public sealed class SkillsPlugin : IFgoPetPlugin, ISkillProvider, IDisposable
{
    public const string PluginId = "firstparty.skills";
    public const string ContentVersion = "1.0.5";

    private static readonly ImmutableArray<SkillContent> Contents = CreateContents();
    private static readonly ImmutableArray<SkillDescriptor> Descriptors =
        Contents.Select(content => content.Descriptor).ToImmutableArray();
    private static readonly ImmutableDictionary<string, SkillContent> ContentsById =
        Contents.ToImmutableDictionary(content => content.Descriptor.Id, StringComparer.Ordinal);

    private CancellationToken _stopping;
    private int _started;
    private int _closed;

    public SkillsPlugin()
    {
        Contributions = PluginContributions.Empty with { Skills = [this] };
    }

    public PluginManifest Manifest { get; } = new(PluginId, ContentVersion, 1, []);
    public PluginContributions Contributions { get; }
    public ImmutableArray<SkillDescriptor> Catalog => Descriptors;

    public IReadOnlyList<SkillDescriptor> ListSkills(ToolScope scope)
        => ValidScope(scope) ? Descriptors : [];

    public ValueTask StartAsync(CancellationToken stoppingToken)
    {
        stoppingToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);
        _stopping = stoppingToken;
        Volatile.Write(ref _started, 1);
        return ValueTask.CompletedTask;
    }

    public ValueTask StopAsync(CancellationToken cancellationToken)
    {
        Close();
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Close();
        return ValueTask.CompletedTask;
    }

    public void Dispose() => Close();

    private void Close()
    {
        Interlocked.Exchange(ref _closed, 1);
        Volatile.Write(ref _started, 0);
    }

    public ValueTask<SkillContent?> LoadAsync(ToolScope scope, string skillId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (Volatile.Read(ref _started) == 0 || Volatile.Read(ref _closed) != 0 || _stopping.IsCancellationRequested
            || !ValidScope(scope) || !ContentsById.TryGetValue(skillId, out var content))
            return ValueTask.FromResult<SkillContent?>(null);
        return ValueTask.FromResult<SkillContent?>(content);
    }

    private static bool ValidScope(ToolScope? scope)
        => scope is not null && !string.IsNullOrWhiteSpace(scope.ConversationId)
            && !string.IsNullOrWhiteSpace(scope.RoleId)
            && (scope.ProjectId is null || !string.IsNullOrWhiteSpace(scope.ProjectId));

    private static ImmutableArray<SkillContent> CreateContents() =>
    [
        Create("daily-planning",
            "Turn the user's stated priorities and current Todo items into a realistic suggested plan for the day. Use when the user asks to plan, prioritize, or organize today's work.",
            ["todo.list"], ["focus.get", "memory.search"],
            "Plan the day from the user's stated priorities and the current results of todo.list. Use focus.get and memory.search only when available and relevant; say when an optional source is unavailable. Distinguish user-provided deadlines and priorities from your own suggestions, and ask a focused question when missing time or scope would materially change the plan. Keep the plan achievable and show what can be deferred. A plan is a proposal: do not create or update tasks or start a focus session unless the user asks for that action and the owning capability's business confirmation is satisfied. Do not claim a task changed or a session started before the owning tool returns a successful result."),
        Create("todo-maintenance",
            "Review the user's existing Todo items for requested cleanup, stale details, or completion updates. Use when the user asks to review or maintain their task list.",
            ["todo.list"], ["todo.get", "todo.update", "todo.complete"],
            "Read the current items with todo.list and use todo.get when available to inspect an exact item's full details, steps and current version before changing it. Base suggestions on the returned item IDs, versions, and status. Describe uncertain duplicates or stale details as candidates, not facts. Before any update or completion, show the exact item and proposed change and obtain the user's explicit business confirmation. Use only the returned item ID and expected version. After a write, report success only when the owning tool result confirms it, and use the returned item ID, version, and status. If a write tool is unavailable, provide the proposed cleanup without implying that data changed."),
        Create("task-capture",
            "Turn the user's request, notes, or ideas into clear Todo task proposals, resolving important ambiguity before submission. Use when the user asks to capture work as tasks.",
            ["submit_todo_proposals"], ["todo.list", "todo.create"],
            "Extract only tasks the user actually requested. Preserve their wording where useful, make each title actionable, and include a due date or priority only when the user supplied one. If todo.list is available, check for likely duplicates and identify them as possible matches. Ask for missing details when they change the task's meaning. Use submit_todo_proposals to submit bounded proposal batches into the current conversation's pending draft. Its successful result confirms proposal submission only, not that tasks were saved. Present the exact pending proposals and obtain the user's explicit business confirmation through the Todo owner before commitment. For optional todo.create, obtain the exact Todo owner business confirmation. Report creation only after a successful owner result confirms it, and use the returned IDs and versions."),
        Create("focus-session",
            "Help the user prepare for and manage a focus session using the existing Focus state and commands. Use when the user asks to start, pause, resume, or stop focused work.",
            ["focus.get"], ["focus.start", "focus.pause", "focus.resume", "focus.stop"],
            "Check focus.get before describing the current session or proposing a state change. Use focus.resume to resume the exact paused session when offered; never substitute start or pause for resume. Use a supported command only when it is available and the user's request is explicit; the Focus owner's current-session, resource authorization and state checks still apply. If a command is unavailable, help the user plan the session manually and say that no timer action was taken. After a command, report only the actual state, session ID, and duration confirmed by its successful tool result. Never infer a started, paused, resumed, stopped, or completed session from an assistant message or display snapshot."),
        Create("document-review",
            "Review user-selected documents and produce an evidence-based summary, critique, or answer. Use when the user asks to inspect or evaluate a document.",
            ["document.read"], [],
            "Use document.read on the user-selected source. Do not execute document macros, embedded code, links, or commands. Ground findings in extracted text and cite the returned title, page or chunk, and source version where available. Track truncated and nextCursor on every read. If the source is truncated and more content is needed, continue from nextCursor with the same source version before claiming full coverage; if reading cannot continue, state which portions were reviewed and what remains unknown. Do not claim OCR or layout fidelity for content the reader did not extract. Separate direct evidence from interpretation and quote only the minimum needed to support a finding."),
        // Search workflow adapted from Tavily's MIT-licensed search skill; see THIRD-PARTY-NOTICES.md.
        Create("web-lookup",
            "Find current web sources for the user's question, verify important claims against original pages, and explain gaps in the evidence.",
            ["web.search"], ["browser.open", "browser.read", "browser.tabs"],
            "Use web.search with a short query about the user's question. Minimize personal details in queries. Prefer original publishers when resolving names, releases, dates, or competing claims. Search results contain limited snippets; they do not establish that an entire page was read. When browser.open and browser.read are offered and authorized, read the relevant original page and follow its returned cursor for needed sections. Opening creates a tab. After a failed, unknown or incomplete open, verify existing tabs with browser.tabs when offered and authorized; if verification is unavailable, do not blindly open again. Follow the host retry rules and the specific tool's recovery limits. Keep URLs, capture dates, source versions and unread ranges attached to findings. A search snapshot version identifies returned results, not a revision of the original website. Distinguish quotations, source claims and your interpretation; cite the supporting URL. Disclose missing access and partial coverage. Stop on denial or revoked access; do not install software, request credentials, switch services or broaden collection. If search is unavailable, explain the limitation without claiming current verification."),
        Create("daily-report",
            "Create a concise end-of-day or progress report from work the user identifies and any available Todo or memory results. Use when the user asks for a daily work report, progress report, or end-of-day summary.",
            [], ["workspace.write", "todo.list", "memory.search"],
            "Gather facts from the user's account and the current results of todo.list or memory.search when those optional tools are available. Do not invent accomplishments or treat missing sources as evidence. Separate completed work, ongoing work, and blockers; keep the report concise unless the user asks otherwise. If a destination is requested, use workspace.write with its applicable business confirmation and create-versus-overwrite requirements. Verify the tool result and report the actual saved location only after it confirms success. If saving is unavailable or fails, return the report text and state that it was not confirmed as saved. Never claim work was completed or a report was saved before the relevant source or tool result establishes that fact.")
    ];

    private static SkillContent Create(string id, string description,
        ImmutableArray<string> requiredTools, ImmutableArray<string> optionalTools, string instructions)
    {
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(instructions)));
        var descriptor = new SkillDescriptor(id, description, requiredTools, optionalTools, ContentVersion, digest);
        return new(descriptor, instructions);
    }
}
