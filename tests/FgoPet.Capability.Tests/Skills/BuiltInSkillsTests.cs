using System.Collections.Immutable;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using FgoPet.Extensibility;
using FgoPet.Kernel.Agent;
using Xunit;

namespace FgoPet.Capability.Tests.Skills;

public sealed class BuiltInSkillsTests
{
    private static readonly ToolScope Scope = new("synthetic-conversation", "synthetic-role", null);
    private static readonly string[] ExpectedIds =
    [
        "daily-planning",
        "todo-maintenance",
        "task-capture",
        "focus-session",
        "document-review",
        "web-lookup",
        "daily-report"
    ];

    [Fact]
    public async Task Built_in_skills_have_stable_ids_and_load_verified_utf8_content()
    {
        await using var host = new Host();
        await host.StartAsync();

        var skills = host.Catalog.Skills;
        Assert.Equal("firstparty.skills", host.SkillPlugin.Manifest.Id);
        Assert.Equal(ExpectedIds, skills.Select(skill => skill.Descriptor.Id));
        Assert.Equal(ExpectedIds, host.Registry.GetSkills(Scope).Select(skill => skill.Id));

        foreach (var registered in skills)
        {
            var descriptor = registered.Descriptor;
            Assert.Equal("1.0.4", descriptor.Version);
            Assert.Matches("\\A[0-9A-F]{64}\\z", descriptor.ContentDigest);

            var loaded = await host.Registry.LoadAsync(Scope, descriptor.Id, default);

            Assert.Null(loaded.ErrorCode);
            Assert.Equal(host.SkillPlugin.Manifest.Id, loaded.PluginId);
            Assert.Equal(descriptor.Id, loaded.Content!.Descriptor.Id);
            var bytes = Encoding.UTF8.GetBytes(loaded.Content.Instructions);
            Assert.InRange(bytes.Length, 1, SkillRegistry.MaxBodyBytes);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), descriptor.ContentDigest);
        }

        Assert.Empty(Assert.Single(skills.Where(skill => skill.Descriptor.Id == "daily-report"))
            .Descriptor.RequiredTools);
        Assert.Equal(new[] { "workspace.write", "todo.list", "memory.search" },
            Assert.Single(skills.Where(skill => skill.Descriptor.Id == "daily-report"))
                .Descriptor.OptionalTools);
    }

    [Fact]
    public async Task Missing_required_todo_tool_hides_and_rejects_daily_planning()
    {
        await using var host = new Host(missingTools: ["todo.list"]);
        await host.StartAsync();

        Assert.DoesNotContain(host.Registry.GetSkills(Scope), skill => skill.Id == "daily-planning");
        Assert.Contains(host.Registry.GetSkills(Scope), skill => skill.Id == "daily-report");

        var loaded = await host.Registry.LoadAsync(Scope, "daily-planning", default);

        Assert.Equal("firstparty.skills", loaded.PluginId);
        Assert.Equal("SKILL_REQUIRED_TOOL_UNAVAILABLE", loaded.ErrorCode);
        Assert.Null(loaded.Content);
    }

    [Fact]
    public async Task Missing_optional_tools_do_not_block_daily_report()
    {
        await using var host = new Host(includeOptionalTools: false);
        await host.StartAsync();

        Assert.Contains(host.Registry.GetSkills(Scope), skill => skill.Id == "daily-report");
        Assert.DoesNotContain(host.Catalog.Tools, tool => tool.Descriptor.Name == "workspace.write");

        var loaded = await host.Registry.LoadAsync(Scope, "daily-report", default);

        Assert.Null(loaded.ErrorCode);
        Assert.Equal("firstparty.skills", loaded.PluginId);
        Assert.Contains("result", loaded.Content!.Instructions, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("saved", loaded.Content.Instructions, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Task_capture_loads_with_pending_proposals_when_direct_creation_is_unavailable()
    {
        await using var host = new Host(availableTools: ["submit_todo_proposals", "todo.list"]);
        await host.StartAsync();

        Assert.Contains(host.Registry.GetSkills(Scope), skill => skill.Id == "task-capture");
        var loaded = await host.Registry.LoadAsync(Scope, "task-capture", default);

        Assert.Null(loaded.ErrorCode);
        Assert.NotNull(loaded.Content);
    }

    [Fact]
    public async Task Task_capture_rejects_a_missing_proposal_tool_even_when_direct_creation_is_registered()
    {
        await using var host = new Host(availableTools: ["todo.create", "todo.list"]);
        await host.StartAsync();

        Assert.DoesNotContain(host.Registry.GetSkills(Scope), skill => skill.Id == "task-capture");
        var loaded = await host.Registry.LoadAsync(Scope, "task-capture", default);

        Assert.Equal("SKILL_REQUIRED_TOOL_UNAVAILABLE", loaded.ErrorCode);
        Assert.Null(loaded.Content);
    }

    [Fact]
    public async Task Web_lookup_loads_with_search_even_when_browser_tools_are_unavailable()
    {
        await using var host = new Host(availableTools: ["web.search"]);
        await host.StartAsync();

        Assert.Contains(host.Registry.GetSkills(Scope), skill => skill.Id == "web-lookup");
        var loaded = await host.Registry.LoadAsync(Scope, "web-lookup", default);

        Assert.Null(loaded.ErrorCode);
        Assert.NotNull(loaded.Content);
    }

    [Fact]
    public async Task Web_lookup_requires_search_even_when_browser_reading_is_registered()
    {
        await using var host = new Host(availableTools: ["browser.open", "browser.read"]);
        await host.StartAsync();

        Assert.DoesNotContain(host.Registry.GetSkills(Scope), skill => skill.Id == "web-lookup");
        var loaded = await host.Registry.LoadAsync(Scope, "web-lookup", default);

        Assert.Equal("SKILL_REQUIRED_TOOL_UNAVAILABLE", loaded.ErrorCode);
        Assert.Null(loaded.Content);
    }

    [Fact]
    public async Task Web_lookup_keeps_tab_verification_optional_and_does_not_blindly_reopen_after_failure()
    {
        await using var host = new Host(availableTools: ["web.search", "browser.open", "browser.read"]);
        await host.StartAsync();
        var loaded = await host.Registry.LoadAsync(Scope, "web-lookup", default);
        Assert.Null(loaded.ErrorCode);
        Assert.Contains("browser.tabs", loaded.Content!.Descriptor.OptionalTools);
        Assert.Contains("browser.tabs", loaded.Content.Instructions, StringComparison.Ordinal);
        Assert.Contains("do not blindly open again", loaded.Content.Instructions, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Skill_guidance_preserves_tool_authority_and_document_source_evidence()
    {
        await using var host = new Host();
        await host.StartAsync();

        var todo = await host.Registry.LoadAsync(Scope, "todo-maintenance", default);
        var capture = await host.Registry.LoadAsync(Scope, "task-capture", default);
        var focus = await host.Registry.LoadAsync(Scope, "focus-session", default);
        var documents = await host.Registry.LoadAsync(Scope, "document-review", default);

        Assert.Contains("confirmation", todo.Content!.Instructions, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("exact item", todo.Content.Instructions, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("confirmation", capture.Content!.Instructions, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("result", capture.Content.Instructions, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("state checks", focus.Content!.Instructions, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("source", documents.Content!.Instructions, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("truncated", documents.Content.Instructions, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("nextCursor", documents.Content.Instructions, StringComparison.OrdinalIgnoreCase);
    }

    private static IFgoPetPlugin CreateSkillsPlugin()
    {
        var assembly = Assembly.Load(new AssemblyName("FgoPet.Plugin.Skills"));
        var type = assembly.GetType("FgoPet.Plugin.Skills.SkillsPlugin");
        Assert.NotNull(type);
        Assert.True(typeof(IFgoPetPlugin).IsAssignableFrom(type));
        return Assert.IsAssignableFrom<IFgoPetPlugin>(Activator.CreateInstance(type));
    }

    private sealed class Host : IAsyncDisposable
    {
        public IFgoPetPlugin SkillPlugin { get; }
        public PluginCatalog Catalog { get; }
        public PluginRuntime Runtime { get; }
        public SkillRegistry Registry { get; }

        public Host(bool includeOptionalTools = true, ImmutableArray<string> missingTools = default,
            ImmutableArray<string> availableTools = default)
        {
            SkillPlugin = CreateSkillsPlugin();
            var provider = Assert.Single(SkillPlugin.Contributions.Skills);
            var missing = missingTools.IsDefault ? new HashSet<string>(StringComparer.Ordinal)
                : missingTools.ToHashSet(StringComparer.Ordinal);
            var names = (availableTools.IsDefault
                    ? provider.Catalog.SelectMany(descriptor => descriptor.RequiredTools
                        .Concat(includeOptionalTools ? descriptor.OptionalTools : [])).ToImmutableArray()
                    : availableTools)
                .Where(name => !missing.Contains(name))
                .Distinct(StringComparer.Ordinal)
                .ToImmutableArray();
            Catalog = PluginCatalog.Create([SkillPlugin, new DependencyToolPlugin(names)]);
            Runtime = new PluginRuntime(Catalog);
            Registry = new SkillRegistry(Catalog, Runtime, new ToolRegistry(Catalog, Runtime));
        }

        public async Task StartAsync()
        {
            var result = await Runtime.StartAsync(default);
            Assert.True(result.Succeeded, result.ErrorCode);
        }

        public async ValueTask DisposeAsync() => await Runtime.StopAsync();
    }

    private sealed class DependencyToolPlugin(ImmutableArray<string> names) : IFgoPetPlugin
    {
        public PluginManifest Manifest { get; } = new("fixture.skill-tools", "1.0.0", 1, []);
        public PluginContributions Contributions { get; } = PluginContributions.Empty with
        {
            Tools = names.Select(name => (IToolProvider)new DependencyTool(name)).ToImmutableArray()
        };

        public ValueTask StartAsync(CancellationToken stoppingToken) => ValueTask.CompletedTask;
        public ValueTask StopAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class DependencyTool : IToolProvider
    {
        public DependencyTool(string name)
        {
            var effect = name == "submit_todo_proposals" ? ToolEffect.Proposal
                : name is "workspace.write" or "workspace.edit" or "todo.create" or "todo.update"
                    or "todo.complete" or "focus.start" or "focus.pause" or "focus.stop"
                    ? ToolEffect.Command : ToolEffect.ReadOnly;
            Descriptor = new(name, "Synthetic dependency fixture", "{}", effect);
        }

        public ToolDescriptor Descriptor { get; }

        public ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Dependency fixture tools are not invoked by skill tests.");
    }
}
