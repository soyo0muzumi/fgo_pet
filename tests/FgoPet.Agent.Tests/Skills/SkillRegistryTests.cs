using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using FgoPet.Extensibility;
using FgoPet.Kernel.Agent;
using Xunit;

namespace FgoPet.Agent.Tests.Skills;

public sealed class SkillRegistryTests
{
    [Fact]
    public async Task Equivalent_descriptor_array_contents_load_under_captured_plugin_owner()
    {
        using var host = new Host(required: ["fixture.read"], optional: ["optional.read"]);
        await host.StartAsync();
        var original = host.Provider.Catalog[0];
        host.Provider.Catalog = [original with { RequiredTools = [.. original.RequiredTools], OptionalTools = [.. original.OptionalTools] }];
        Assert.Equal("fixture.skill", Assert.Single(host.Registry.GetSkills(Scope)).Id);
        var loaded = await host.Registry.LoadAsync(Scope, "fixture.skill", default);
        Assert.Null(loaded.ErrorCode);
        Assert.Equal("fixture.skills", loaded.PluginId);
        Assert.Equal("synthetic instructions", loaded.Content!.Instructions);
        Assert.Same(original, loaded.Content.Descriptor);
        Assert.Equal(1, host.Provider.Loads);
    }

    [Theory]
    [InlineData("missing", "SKILL_NOT_FOUND")]
    [InlineData("path", "SKILL_NOT_FOUND")]
    [InlineData("inactive", "SKILL_UNAVAILABLE")]
    [InlineData("closed", "SKILL_UNAVAILABLE")]
    [InlineData("scope", "SKILL_NOT_FOUND")]
    [InlineData("changedDescription", "SKILL_CONTENT_CHANGED")]
    [InlineData("changedVersion", "SKILL_CONTENT_CHANGED")]
    [InlineData("changedRequired", "SKILL_CONTENT_CHANGED")]
    [InlineData("changedOptional", "SKILL_CONTENT_CHANGED")]
    [InlineData("changedDigest", "SKILL_CONTENT_CHANGED")]
    [InlineData("duplicateListing", "SKILL_CONTENT_CHANGED")]
    [InlineData("unknownListing", "SKILL_CONTENT_CHANGED")]
    [InlineData("defaultListing", "SKILL_CONTENT_CHANGED")]
    [InlineData("missingTool", "SKILL_REQUIRED_TOOL_UNAVAILABLE")]
    public async Task Guard_rejections_do_not_call_loader(string failure, string code)
    {
        using var host = new Host(required: failure == "missingTool" ? ["unavailable.tool"] : []);
        if (failure != "inactive") await host.StartAsync();
        if (failure == "closed") host.Runtime.CloseAdmission();
        var descriptor = host.Provider.Catalog[0];
        host.Provider.Catalog = failure switch
        {
            "changedDescription" => [descriptor with { Description = "different" }],
            "changedVersion" => [descriptor with { Version = "2.0.0" }],
            "changedRequired" => [descriptor with { RequiredTools = ["other.read"] }],
            "changedOptional" => [descriptor with { OptionalTools = ["other.read"] }],
            "changedDigest" => [descriptor with { ContentDigest = new string('A', 64) }],
            "duplicateListing" => [descriptor, descriptor],
            "unknownListing" => [descriptor, descriptor with { Id = "other.skill" }],
            "defaultListing" => default,
            _ => [descriptor]
        };
        var id = failure == "missing" ? "missing.skill" : failure == "path" ? "../fixture.skill" : "fixture.skill";
        var scope = failure == "scope" ? Scope with { RoleId = "another-role" } : Scope;
        var result = await host.Registry.LoadAsync(scope, id, default);
        Assert.Equal(code, result.ErrorCode);
        Assert.Null(result.Content);
        Assert.Equal(0, host.Provider.Loads);
    }

    [Fact]
    public async Task Optional_missing_tools_do_not_block_loading_or_listing()
    {
        using var host = new Host(optional: ["unavailable.tool"]);
        await host.StartAsync();
        Assert.Single(host.Registry.GetSkills(Scope));
        Assert.Null((await host.Registry.LoadAsync(Scope, "fixture.skill", default)).ErrorCode);
        Assert.Equal(1, host.Provider.Loads);
    }

    [Theory]
    [InlineData("body", "SKILL_CONTENT_CHANGED")]
    [InlineData("descriptor", "SKILL_CONTENT_CHANGED")]
    [InlineData("null", "SKILL_UNAVAILABLE")]
    [InlineData("exception", "SKILL_UNAVAILABLE")]
    public async Task Returned_content_is_checked_and_provider_failures_are_safe(string failure, string code)
    {
        using var host = new Host();
        await host.StartAsync();
        host.Provider.OnLoad = (_, _) => failure switch
        {
            "body" => ValueTask.FromResult<SkillContent?>(new(host.Provider.Catalog[0], "changed body")),
            "descriptor" => ValueTask.FromResult<SkillContent?>(new(host.Provider.Catalog[0] with { Version = "2.0.0" }, "synthetic instructions")),
            "null" => ValueTask.FromResult<SkillContent?>(null),
            _ => throw new IOException("private backend details")
        };
        var result = await host.Registry.LoadAsync(Scope, "fixture.skill", default);
        Assert.Equal(code, result.ErrorCode);
        Assert.Null(result.Content);
        Assert.Equal(1, host.Provider.Loads);
        Assert.DoesNotContain("private", result.ToString());
    }

    [Theory]
    [InlineData("asciiBoundary", null)]
    [InlineData("asciiOver", "SKILL_CONTENT_TOO_LARGE")]
    [InlineData("unicodeOver", "SKILL_CONTENT_TOO_LARGE")]
    [InlineData("invalidUtf16", "SKILL_CONTENT_CHANGED")]
    public async Task Body_limit_uses_exact_utf8_bytes_and_allows_boundary(string size, string? error)
    {
        var body = size == "asciiBoundary" ? new string('a', 32768)
            : size == "asciiOver" ? new string('a', 32769)
            : size == "invalidUtf16" ? new string((char)0xD800, 1) : new string('界', 10923);
        using var host = new Host(body);
        await host.StartAsync();
        var result = await host.Registry.LoadAsync(Scope, "fixture.skill", default);
        Assert.Equal(error, result.ErrorCode);
        if (error is null) Assert.Equal(body, result.Content!.Instructions);
        else Assert.Null(result.Content);
        Assert.Equal(1, host.Provider.Loads);
    }

    [Theory]
    [InlineData("admission", "SKILL_UNAVAILABLE")]
    [InlineData("scope", "SKILL_NOT_FOUND")]
    [InlineData("metadata", "SKILL_CONTENT_CHANGED")]
    public async Task Admission_and_scope_catalog_are_rechecked_after_await(string change, string code)
    {
        using var host = new Host();
        await host.StartAsync();
        var initial = host.Provider.Catalog[0];
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Provider.OnLoad = async (_, token) =>
        {
            reached.SetResult();
            await release.Task.WaitAsync(token);
            return new(initial, "synthetic instructions");
        };
        var loading = host.Registry.LoadAsync(Scope, "fixture.skill", default).AsTask();
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if (change == "admission") host.Runtime.CloseAdmission();
        else if (change == "scope") host.Provider.ScopeEnabled = false;
        else host.Provider.Catalog = [initial with { Version = "2.0.0" }];
        release.SetResult();
        var result = await loading;
        Assert.Equal(code, result.ErrorCode);
        Assert.Null(result.Content);
        Assert.Equal(1, host.Provider.Loads);
    }

    [Fact]
    public async Task Cancellation_before_or_during_load_propagates()
    {
        using var host = new Host();
        await host.StartAsync();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.Registry.LoadAsync(Scope, "fixture.skill", cancelled.Token).AsTask());
        Assert.Equal(0, host.Provider.Loads);
        host.Provider.OnLoad = (_, _) => throw new OperationCanceledException();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.Registry.LoadAsync(Scope, "fixture.skill", default).AsTask());
        Assert.Equal(1, host.Provider.Loads);
    }

    private static readonly ToolScope Scope = new("conversation", "role", null);
    private sealed class Host : IDisposable
    {
        public Provider Provider { get; }
        public PluginRuntime Runtime { get; }
        public SkillRegistry Registry { get; }
        public Host(string body = "synthetic instructions", ImmutableArray<string> required = default, ImmutableArray<string> optional = default)
        {
            var descriptor = new SkillDescriptor("fixture.skill", "Synthetic skill", required.IsDefault ? [] : required,
                optional.IsDefault ? [] : optional, "1.0.0", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body))));
            Provider = new([descriptor], body);
            var catalog = PluginCatalog.Create([new Plugin(Provider)]);
            Runtime = new(catalog);
            Registry = new(catalog, Runtime, new ToolRegistry(catalog, Runtime));
        }
        public async Task StartAsync() => Assert.True((await Runtime.StartAsync(default)).Succeeded);
        public void Dispose() => Runtime.Dispose();
    }
    private sealed class Plugin(ISkillProvider provider) : IFgoPetPlugin
    {
        public PluginManifest Manifest { get; } = new("fixture.skills", "1.0.0", 1, []);
        public PluginContributions Contributions { get; } = new([new Tool()], [], []) { Skills = [provider] };
        public ValueTask StartAsync(CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask StopAsync(CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Tool : IToolProvider
    {
        public ToolDescriptor Descriptor { get; } = new("fixture.read", "Synthetic read", "{\"type\":\"object\"}", ToolEffect.ReadOnly);
        public ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken token)
            => throw new InvalidOperationException("Skills must not execute their dependency tools.");
    }
    private sealed class Provider(ImmutableArray<SkillDescriptor> catalog, string body) : ISkillProvider
    {
        public ImmutableArray<SkillDescriptor> Catalog { get; set; } = catalog;
        public bool ScopeEnabled { get; set; } = true;
        public int Loads { get; private set; }
        public Func<string, CancellationToken, ValueTask<SkillContent?>>? OnLoad { get; set; }
        public IReadOnlyList<SkillDescriptor> ListSkills(ToolScope scope) => ScopeEnabled && scope == Scope ? Catalog : [];
        public ValueTask<SkillContent?> LoadAsync(ToolScope scope, string id, CancellationToken token)
        {
            Loads++;
            return OnLoad is not null ? OnLoad(id, token) : ValueTask.FromResult<SkillContent?>(new(Catalog[0], body));
        }
    }
}
