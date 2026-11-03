using System.IO;
using System.Text.Json;
using FgoPet.App.Bootstrap;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Settings;
using FgoPet.Extensibility;
using FgoPet.Infrastructure.Persistence;
using FgoPet.Understanding;
using FgoPet.Understanding.Contracts;
using Xunit;

namespace FgoPet.App.Tests.Understanding;

public sealed class TopicMaterialHostTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "fgopet-material-host-" + Guid.NewGuid().ToString("N"));
    private readonly UnderstandingService _understanding;
    private MaterialResearchGrant _grant = new();
    private ToolScope? _scope = new("conversation", "mash", "project");
    private ModelConnectionSettings? _connection = new("fixture", "https://example.org/v1", "model", contextWindowOverride: 32768);
    private readonly ConfiguredMaterialResearchHost _host;
    private ConversationScope SemanticScope => new("mash", "project");
    private static UnderstandingPolicy AllPolicy => new(UnderstandingMode.Bounded, ReadSources: true,
        ExternalProcessing: true, SaveCandidates: true, UseInDialogue: true);

    public TopicMaterialHostTests()
    {
        var database = new RuntimeDatabase(Path.Combine(_directory, "runtime.db"), pooling: false);
        new RuntimeDatabaseMigrator(database).Migrate();
        _understanding = new(database, new Sources());
        _host = new(() => _grant, () => _scope, () => _connection, _understanding, new object());
    }

    [Fact]
    public void Research_requires_its_own_grant_in_addition_to_understanding_permissions()
    {
        Configure(AllPolicy);
        Assert.Null(_host.Capture());
        EnableGrant();
        var captured = Assert.IsType<MaterialResearchConfiguration>(_host.Capture());
        Assert.Equal(_scope, captured.Scope); Assert.Equal(_connection, captured.Connection);
        var commits = 0;
        Assert.True(_host.TryCommit(captured, () => commits++)); Assert.Equal(1, commits);
        _grant = _grant with { Enabled = false };
        Assert.False(_host.TryCommit(captured, () => commits++)); Assert.Equal(1, commits);
    }

    [Theory]
    [InlineData("off")]
    [InlineData("disabled")]
    [InlineData("sources")]
    [InlineData("external-processing")]
    [InlineData("saving")]
    [InlineData("dialogue")]
    public void Each_understanding_permission_remains_independent(string denied)
    {
        EnableGrant();
        Configure(denied switch
        {
            "off" => AllPolicy with { Mode = UnderstandingMode.Off },
            "disabled" => AllPolicy with { Enabled = false },
            "sources" => AllPolicy with { ReadSources = false },
            "external-processing" => AllPolicy with { ExternalProcessing = false },
            "saving" => AllPolicy with { SaveCandidates = false },
            _ => AllPolicy with { UseInDialogue = false }
        });
        Assert.Null(_host.Capture());
    }

    [Theory]
    [InlineData("role")]
    [InlineData("project")]
    [InlineData("route")]
    [InlineData("search-recipient")]
    [InlineData("daily-zero")]
    [InlineData("daily-large")]
    [InlineData("no-scope")]
    [InlineData("no-model")]
    public void Research_grant_is_bound_to_scope_route_search_recipient_and_bounded_budget(string mismatch)
    {
        Configure(AllPolicy); EnableGrant();
        switch (mismatch)
        {
            case "role": _grant = _grant with { RoleId = "other" }; break;
            case "project": _grant = _grant with { ProjectId = "other" }; break;
            case "route": _grant = _grant with { ApprovedRoute = null }; break;
            case "search-recipient": _grant = _grant with { SearchProvider = "other" }; break;
            case "daily-zero": _grant = _grant with { DailyLimit = 0 }; break;
            case "daily-large": _grant = _grant with { DailyLimit = 7 }; break;
            case "no-scope": _scope = null; break;
            case "no-model": _connection = null; break;
        }
        Assert.Null(_host.Capture());
    }

    [Theory]
    [InlineData("policy")]
    [InlineData("route")]
    [InlineData("conversation")]
    [InlineData("grant")]
    public void Captured_work_cannot_commit_after_authority_changes(string change)
    {
        Configure(AllPolicy); EnableGrant();
        var captured = Assert.IsType<MaterialResearchConfiguration>(_host.Capture());
        switch (change)
        {
            case "policy": Configure(AllPolicy with { ExternalProcessing = false }); break;
            case "route": _connection = new("fixture", "https://example.org/v1", "changed", contextWindowOverride: 32768); break;
            case "conversation": _scope = _scope! with { ConversationId = "new-conversation" }; break;
            case "grant": _grant = _grant with { DailyLimit = 3 }; break;
        }
        var committed = false;
        Assert.False(_host.TryCommit(captured, () => committed = true)); Assert.False(committed);
    }

    [Fact]
    public async Task Search_source_invokes_only_the_bound_tool_with_one_bounded_query()
    {
        var tool = new SearchTool();
        var source = new SingleToolMaterialResearchSource(tool);
        var material = Assert.Single(await source.SearchAsync(_scope!, "agent memory", default));
        Assert.Equal("Synthetic source", material.Title);
        Assert.Equal("https://example.org/memory", material.Url);
        Assert.Equal("Synthetic snippet.", material.Snippet);
        var invocation = Assert.IsType<ToolInvocation>(tool.Invocation);
        Assert.Equal(_scope, invocation.Scope);
        Assert.Equal("agent memory", invocation.Arguments.GetProperty("query").GetString());
        Assert.Equal(3, invocation.Arguments.GetProperty("maxResults").GetInt32());
        Assert.Equal(2, invocation.Arguments.EnumerateObject().Count());
        Assert.Equal(1, tool.Calls);
    }

    [Theory]
    [InlineData("other.read", ToolEffect.ReadOnly)]
    [InlineData("web.search", ToolEffect.Proposal)]
    [InlineData("web.search", ToolEffect.Command)]
    public async Task Search_source_rejects_any_tool_outside_the_readonly_search_capability(string name, ToolEffect effect)
    {
        var tool = new SearchTool(name, effect);
        var source = new SingleToolMaterialResearchSource(tool);
        await Assert.ThrowsAsync<InvalidOperationException>(() => source.SearchAsync(_scope!, "agent memory", default));
        Assert.Equal(0, tool.Calls);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"results\":null}")]
    [InlineData("{\"results\":[{}]}")]
    [InlineData("{\"results\":[{\"title\":1,\"url\":\"https://example.org/\",\"snippet\":\"x\"}]}")]
    [InlineData("{\"results\":[{\"title\":\"\",\"url\":\"https://example.org/\",\"snippet\":\"x\"}]}")]
    [InlineData("{\"results\":[{},{},{},{}]}")]
    public async Task Search_source_rejects_malformed_payloads(string json)
    {
        var tool = new SearchTool { Result = new(true, JsonSerializer.Deserialize<JsonElement>(json)) };
        await Assert.ThrowsAsync<InvalidOperationException>(() => new SingleToolMaterialResearchSource(tool)
            .SearchAsync(_scope!, "agent memory", default));
        Assert.Equal(1, tool.Calls);
    }

    [Fact]
    public async Task Search_failure_or_cancellation_is_not_retried()
    {
        var tool = new SearchTool { Result = new(false, JsonSerializer.SerializeToElement(new { }), "SYNTHETIC_FAILURE") };
        var source = new SingleToolMaterialResearchSource(tool);
        await Assert.ThrowsAsync<InvalidOperationException>(() => source.SearchAsync(_scope!, "agent memory", default));
        Assert.Equal(1, tool.Calls);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.SearchAsync(_scope!, "agent memory", cancellation.Token));
        Assert.Equal(1, tool.Calls);
        using var midCall = new CancellationTokenSource(); tool.OnInvoke = midCall.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.SearchAsync(_scope!, "agent memory", midCall.Token));
        Assert.Equal(2, tool.Calls);
    }

    private void EnableGrant() => _grant = new(true, "mash", "project", ModelRouteKey.From(_connection!), "duckduckgo", 6);
    private void Configure(UnderstandingPolicy policy) => _understanding.Configure(SemanticScope, _understanding.State(SemanticScope).Revision, policy);
    private sealed class Sources : IConversationSourceReader
    {
        public ConversationSourceSnapshot Read(ToolScope scope, string messageId, ConversationSourceKind kind) => throw new NotSupportedException();
        public bool IsCurrent(ConversationSourceSnapshot source) => true;
    }
    private sealed class SearchTool(string name = "web.search", ToolEffect effect = ToolEffect.ReadOnly) : IToolProvider
    {
        public ToolDescriptor Descriptor { get; } = new(name, "Synthetic source", "{}", effect);
        public ToolResult Result = new(true, JsonSerializer.SerializeToElement(new
        { results = new[] { new { title = "Synthetic source", url = "https://example.org/memory", snippet = "Synthetic snippet." } } }));
        public ToolInvocation? Invocation;
        public int Calls;
        public Action? OnInvoke;
        public ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken token)
        { Invocation = invocation; Calls++; OnInvoke?.Invoke(); return ValueTask.FromResult(Result); }
    }
    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
