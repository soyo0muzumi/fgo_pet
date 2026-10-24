using System.Text.Json;
using System.IO;
using FgoPet.Core.Dialogue;
using FgoPet.Extensibility;
using FgoPet.Infrastructure.Persistence;
using FgoPet.Understanding;
using FgoPet.Understanding.Contracts;
using FgoPet.Understanding.Desktop;
using FgoPet.UiSdk;
using Xunit;

namespace FgoPet.App.Tests.Understanding;

public sealed class UnderstandingAwarenessWebPageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fgo-awareness-page-" + Guid.NewGuid().ToString("N"));
    private readonly UnderstandingService _owner;
    private readonly Lifetime _lifetime = new();
    private readonly Timeline _timeline = new();
    private ConversationScope? _scope = new("mash", "project");
    private readonly UnderstandingWebPage _page;
    public UnderstandingAwarenessWebPageTests()
    {
        var db = new RuntimeDatabase(Path.Combine(_root, "runtime.db"), pooling: false); new RuntimeDatabaseMigrator(db).Migrate();
        _owner = new(db, new Sources()); _page = new(_owner, () => _scope, _lifetime, _timeline);
    }
    private ValueTask<WebSurfaceCommandResult> Get() => AwarenessSend(_page, "understanding.get", new { pageId = "Understanding" });

    [Fact] public async Task Awareness_is_optional_and_opening_a_session_never_lists_windows_or_grants_access()
    {
        var withoutHost = JsonSerializer.SerializeToElement((await Get()).Payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(JsonValueKind.Null, withoutHost.GetProperty("awareness").ValueKind);
        var unavailable = await _page.HandleCommandAsync(new("understanding.awareness.windows", "fixture",
            JsonSerializer.SerializeToElement(new { pageId = "Understanding", contextToken = "awareness-context" })), default);
        Assert.Equal("AWARENESS_UNAVAILABLE", unavailable.ErrorCode);
        var controls = new AwarenessControls();
        var session = AwarenessPage(controls).CreateSession();
        var result = await AwarenessSend(session, "understanding.get", new { pageId = "Understanding" });
        var status = JsonSerializer.SerializeToElement(result.Payload, new JsonSerializerOptions(JsonSerializerDefaults.Web)).GetProperty("awareness");
        Assert.True(result.Success); Assert.Equal("awareness-context", status.GetProperty("contextToken").GetString());
        Assert.False(status.GetProperty("options").GetProperty("backgroundEnabled").GetBoolean());
        Assert.Empty(controls.Calls); Assert.Equal(new UnderstandingPolicy(), _owner.State(_scope!).Policy);
        Assert.Equal(0, _lifetime.Suspensions);
    }

    [Fact] public async Task Awareness_commands_use_the_controls_binding_and_leave_saved_understanding_policy_unchanged()
    {
        var controls = new AwarenessControls(); var page = AwarenessPage(controls);
        var before = _owner.State(_scope!);
        Assert.True((await AwarenessSend(page, "understanding.get", new { pageId = "Understanding" })).Success);
        Assert.True((await AwarenessSend(page, "understanding.awareness.configure", new { pageId = "Understanding", contextToken = controls.Status.ContextToken,
            revision = controls.Status.Revision, options = new { activityContextEnabled = true, applicationsEnabled = true, backgroundEnabled = false, dailyRequestLimit = 8 } })).Success);
        Assert.Equal(new CompanionAwarenessOptions(true, true, false, 8), controls.Status.Options);
        var listed = await AwarenessSend(page, "understanding.awareness.windows", new { pageId = "Understanding", contextToken = controls.Status.ContextToken });
        Assert.True(listed.Success);
        var listing = JsonSerializer.SerializeToElement(listed.Payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal("window-candidate", Assert.Single(listing.GetProperty("windows").EnumerateArray()).GetProperty("token").GetString());
        Assert.Empty(controls.Status.Windows);
        Assert.True((await AwarenessSend(page, "understanding.awareness.allowWindow", new { pageId = "Understanding", contextToken = controls.Status.ContextToken,
            revision = controls.Status.Revision, candidateToken = "window-candidate" })).Success);
        Assert.Single(controls.Status.Windows);
        Assert.True((await AwarenessSend(page, "understanding.awareness.clearWindows", new { pageId = "Understanding", contextToken = controls.Status.ContextToken,
            revision = controls.Status.Revision })).Success);
        Assert.Empty(controls.Status.Windows);
        Assert.Equal(new[] { "configure", "windows", "allowWindow", "clearWindows" }, controls.Calls);
        Assert.Equal(before, _owner.State(_scope!)); Assert.Equal(0, _lifetime.Suspensions);
    }

    [Theory]
    [InlineData("configure", """{"pageId":"Understanding","contextToken":"awareness-context","revision":0,"scope":"other","options":{"activityContextEnabled":true,"applicationsEnabled":false,"backgroundEnabled":false,"dailyRequestLimit":6}}""")]
    [InlineData("configure", """{"pageId":"Understanding","contextToken":"awareness-context","revision":0,"options":{"activityContextEnabled":true,"applicationsEnabled":false,"backgroundEnabled":false,"dailyRequestLimit":6,"model":"other"}}""")]
    [InlineData("configure", """{"pageId":"Understanding","contextToken":"awareness-context","revision":0,"options":{"activityContextEnabled":true,"applicationsEnabled":false,"dailyRequestLimit":6}}""")]
    [InlineData("configure", """{"pageId":"Understanding","contextToken":"awareness-context","revision":0,"options":{"activityContextEnabled":true,"applicationsEnabled":false,"backgroundEnabled":"true","dailyRequestLimit":6}}""")]
    [InlineData("configure", """{"pageId":"Understanding","contextToken":"awareness-context","revision":0,"options":{"activityContextEnabled":true,"applicationsEnabled":false,"backgroundEnabled":false,"dailyRequestLimit":0}}""")]
    [InlineData("configure", """{"pageId":"Understanding","contextToken":"awareness-context","revision":0,"options":{"activityContextEnabled":true,"applicationsEnabled":false,"backgroundEnabled":false,"dailyRequestLimit":25}}""")]
    [InlineData("configure", """{"pageId":"Understanding","contextToken":"awareness-context","revision":0,"options":{"activityContextEnabled":true,"applicationsEnabled":false,"backgroundEnabled":false,"dailyRequestLimit":6.5}}""")]
    [InlineData("configure", """{"pageId":"Understanding","contextToken":"awareness-context","revision":"0","options":{"activityContextEnabled":true,"applicationsEnabled":false,"backgroundEnabled":false,"dailyRequestLimit":6}}""")]
    [InlineData("configure", """{"pageId":"Understanding","contextToken":"awareness-context","revision":0,"options":{"activityContextEnabled":true,"applicationsEnabled":false,"backgroundEnabled":false,"backgroundEnabled":true,"dailyRequestLimit":6}}""")]
    [InlineData("windows", """{"pageId":"Understanding","contextToken":"awareness-context","modelRecipient":"other"}""")]
    [InlineData("windows", """{"pageId":"Understanding","contextToken":"awareness-context","contextToken":"other"}""")]
    [InlineData("allowWindow", """{"pageId":"Understanding","contextToken":"awareness-context","revision":0,"handle":1234}""")]
    [InlineData("allowWindow", """{"pageId":"Understanding","contextToken":"awareness-context","revision":-1,"candidateToken":"window-candidate"}""")]
    [InlineData("allowWindow", """{"pageId":"Understanding","contextToken":"awareness-context","revision":0,"candidateToken":"window candidate"}""")]
    [InlineData("clearWindows", """{"pageId":"Understanding","contextToken":"awareness-context","revision":0,"backgroundEnabled":true}""")]
    [InlineData("readWindow", """{"pageId":"Understanding","contextToken":"awareness-context","revision":0,"candidateToken":"window-candidate"}""")]
    public async Task Awareness_payloads_are_closed_and_invalid_requests_never_reach_the_controls(string command, string json)
    {
        var controls = new AwarenessControls(); var page = AwarenessPage(controls);
        using var document = JsonDocument.Parse(json);
        var result = await page.HandleCommandAsync(new("understanding.awareness." + command, "fixture", document.RootElement), default);
        Assert.False(result.Success); Assert.Equal("SETTINGS_INVALID_INPUT", result.ErrorCode);
        Assert.Empty(controls.Calls); Assert.Equal(0, _lifetime.Suspensions);
        Assert.Equal(new UnderstandingPolicy(), _owner.State(_scope!).Policy);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Awareness_forwards_frozen_model_binding_and_revision_so_the_owner_can_reject_stale_choices(bool modelChanged)
    {
        var controls = new AwarenessControls(); var page = AwarenessPage(controls);
        await AwarenessSend(page, "understanding.get", new { pageId = "Understanding" });
        var frozen = controls.Status;
        controls.Status = modelChanged ? controls.Status with { ContextToken = "new-model-context", ModelRecipient = "Another synthetic model" }
            : controls.Status with { Revision = controls.Status.Revision + 1 };
        var result = await AwarenessSend(page, "understanding.awareness.configure", new { pageId = "Understanding", contextToken = frozen.ContextToken,
            revision = frozen.Revision, options = new { activityContextEnabled = true, applicationsEnabled = true, backgroundEnabled = true, dailyRequestLimit = 6 } });
        Assert.Equal("AWARENESS_STALE_OR_UNAVAILABLE", result.ErrorCode); Assert.Null(result.Payload);
        Assert.Equal(0, controls.Mutations); Assert.False(controls.Status.Options.BackgroundEnabled);
        Assert.Equal(new UnderstandingPolicy(), _owner.State(_scope!).Policy);
    }

    [Fact] public async Task A_scope_change_during_window_enumeration_does_not_return_titles_to_the_new_context()
    {
        var controls = new AwarenessControls { OnList = () => _scope = new("other", "project") };
        var result = await AwarenessSend(AwarenessPage(controls), "understanding.awareness.windows", new { pageId = "Understanding", contextToken = controls.Status.ContextToken });
        Assert.Equal("AWARENESS_STALE_OR_UNAVAILABLE", result.ErrorCode); Assert.Null(result.Payload);
    }

    [Fact] public async Task Observing_another_scope_via_awareness_does_not_restore_an_old_understanding_editor_binding()
    {
        var controls = new AwarenessControls(); var page = AwarenessPage(controls);
        var original = await AwarenessSend(page, "understanding.get", new { pageId = "Understanding" });
        var oldToken = JsonSerializer.SerializeToElement(original.Payload, new JsonSerializerOptions(JsonSerializerDefaults.Web)).GetProperty("contextToken").GetString();
        _scope = new("other", "project"); controls.Status = controls.Status with { ContextToken = "other-scope-context" };
        Assert.True((await AwarenessSend(page, "understanding.awareness.windows", new { pageId = "Understanding", contextToken = controls.Status.ContextToken })).Success);
        _scope = new("mash", "project");
        var stale = await AwarenessSend(page, "understanding.configure", new { pageId = "Understanding", contextToken = oldToken, revision = 0,
            policy = new { mode = "proposals", enabled = true, readSources = true, externalProcessing = true, saveCandidates = true, useInDialogue = true } });
        Assert.Equal("UNDERSTANDING_SCOPE_CHANGED", stale.ErrorCode); Assert.Equal(0, _lifetime.Suspensions);
        Assert.Equal(new UnderstandingPolicy(), _owner.State(_scope!).Policy);
    }

    private UnderstandingWebPage AwarenessPage(AwarenessControls controls) => new(_owner, () => _scope, _lifetime, _timeline, awareness: controls);
    private static ValueTask<WebSurfaceCommandResult> AwarenessSend(ISettingsWebPage page, string type, object payload) =>
        page.HandleCommandAsync(new(type, "fixture", JsonSerializer.SerializeToElement(payload)), default);
    private sealed class AwarenessControls : ICompanionAwarenessControls
    {
        private readonly CompanionAwarenessWindow _window = new("window-candidate", "Synthetic window", "Synthetic app");
        public CompanionAwarenessStatus Status = new("awareness-context", 0, true, "Synthetic model", false, new(), 0, [], "Idle");
        public readonly List<string> Calls = [];
        public int Mutations;
        public Action? OnList;
        public CompanionAwarenessStatus GetStatus() => Status;
        private void Validate(string contextToken, long? revision, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (contextToken != Status.ContextToken || revision.HasValue && revision.Value != Status.Revision)
                throw new InvalidOperationException("Synthetic private host state must not be returned to WebView.");
        }
        public ValueTask<CompanionAwarenessStatus> ConfigureAsync(string contextToken, long expectedRevision, CompanionAwarenessOptions options, CancellationToken token)
        {
            Calls.Add("configure"); Validate(contextToken, expectedRevision, token); Mutations++;
            Status = Status with { Revision = Status.Revision + 1, Options = options, ModelApprovalCurrent = true };
            return ValueTask.FromResult(Status);
        }
        public ValueTask<IReadOnlyList<CompanionAwarenessWindow>> ListWindowsAsync(string contextToken, CancellationToken token)
        {
            Calls.Add("windows"); Validate(contextToken, null, token); OnList?.Invoke();
            return ValueTask.FromResult<IReadOnlyList<CompanionAwarenessWindow>>([_window]);
        }
        public ValueTask<CompanionAwarenessStatus> AllowWindowAsync(string contextToken, long expectedRevision, string candidateToken, CancellationToken token)
        {
            Calls.Add("allowWindow"); Validate(contextToken, expectedRevision, token);
            if (candidateToken != _window.Token) throw new InvalidOperationException("stale candidate");
            Mutations++; Status = Status with { Revision = Status.Revision + 1, Windows = [_window] };
            return ValueTask.FromResult(Status);
        }
        public ValueTask<CompanionAwarenessStatus> ClearWindowsAsync(string contextToken, long expectedRevision, CancellationToken token)
        {
            Calls.Add("clearWindows"); Validate(contextToken, expectedRevision, token);
            Mutations++; Status = Status with { Revision = Status.Revision + 1, Windows = [] };
            return ValueTask.FromResult(Status);
        }
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private sealed class Sources : IConversationSourceReader
    {
        public ConversationSourceSnapshot Read(ToolScope scope, string messageId, ConversationSourceKind kind) => throw new NotSupportedException();
        public bool IsCurrent(ConversationSourceSnapshot source) => true;
    }
    private sealed class Timeline : IExperienceTimeline
    {
        public ConversationSourceSnapshot Read(ToolScope scope, string messageId, ConversationSourceKind kind) => throw new NotSupportedException();
        public bool IsCurrent(ConversationSourceSnapshot source) => true;
        public long Boundary(ConversationScope scope) => 71;
        public ExperienceWindowPage ReadPage(ConversationScope scope, string conversationId, int startSequence = 1, ExperienceWindowCursor? cursor = null,
            int maxItems = 8, int maxScalars = 6000, CancellationToken token = default) => throw new NotSupportedException();
        public ExperienceWindowPage ReadAdjacent(ConversationScope scope, HistoryAnchor anchor, CancellationToken token = default) => throw new NotSupportedException();
    }
    private sealed class Lifetime : IDialogueContextLifetime
    {
        public long Generation { get; set; }
        public int Suspensions;
        public Task InvalidateAsync(CancellationToken token) => Task.CompletedTask;
        public Task<IDisposable> SuspendAsync(CancellationToken token)
        { Suspensions++; return Task.FromResult<IDisposable>(new Lease()); }
        private sealed class Lease : IDisposable { public void Dispose() { } }
    }
}
