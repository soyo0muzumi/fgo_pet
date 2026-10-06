using System.Collections.Immutable;
using FgoPet.Extensibility;
using Xunit;

namespace FgoPet.Foundation.Tests;

public sealed class NativeContributionCatalogTests
{
    [Fact]
    public void Native_metadata_and_context_identity_trust_are_captured_once_under_registered_owner()
    {
        var descriptor = Descriptor();
        var skills = new Skills([descriptor]);
        var context = new Context("fixture.context");
        var observer = new Observer("fixture.observer");
        var plugin = new Plugin("fixture.native", PluginContributions.Empty with
        { Skills = [skills], AgentContexts = [context], AgentObservers = [observer] });
        var catalog = PluginCatalog.Create([plugin]);
        Assert.Equal(1, skills.CatalogReads);
        skills.Items = [];
        context.Id = "changed.context";
        context.ProvidesTrustedInstructions = false;
        observer.Id = "changed.observer";
        plugin.Contributions = PluginContributions.Empty;
        var capturedSkill = Assert.Single(catalog.Skills);
        Assert.Equal("fixture.native", capturedSkill.PluginId);
        Assert.Same(descriptor, capturedSkill.Descriptor);
        var capturedContext = Assert.Single(catalog.AgentContexts);
        Assert.Equal("fixture.native", capturedContext.PluginId);
        Assert.Equal("fixture.context", capturedContext.ProviderId);
        Assert.True(capturedContext.ProvidesTrustedInstructions);
        Assert.Equal("fixture.observer", Assert.Single(catalog.AgentObservers).ProviderId);
        Assert.Equal(1, skills.CatalogReads);
    }

    [Theory]
    [InlineData("skillsDefault")]
    [InlineData("skillsNull")]
    [InlineData("contextsDefault")]
    [InlineData("contextsNull")]
    [InlineData("observersDefault")]
    [InlineData("observersNull")]
    public void Malformed_native_contribution_arrays_fail_closed(string error)
    {
        var contributions = error switch
        {
            "skillsDefault" => PluginContributions.Empty with { Skills = default },
            "skillsNull" => PluginContributions.Empty with { Skills = [null!] },
            "contextsDefault" => PluginContributions.Empty with { AgentContexts = default },
            "contextsNull" => PluginContributions.Empty with { AgentContexts = [null!] },
            "observersDefault" => PluginContributions.Empty with { AgentObservers = default },
            _ => PluginContributions.Empty with { AgentObservers = [null!] }
        };
        Assert.Equal("PLUGIN_INVALID_CONTRIBUTIONS", Assert.Throws<PluginValidationException>(
            () => PluginCatalog.Create([new Plugin("fixture.native", contributions)])).Code);
    }

    [Theory]
    [InlineData("id")]
    [InlineData("longId")]
    [InlineData("description")]
    [InlineData("longDescription")]
    [InlineData("version")]
    [InlineData("digest")]
    [InlineData("digestLower")]
    [InlineData("requiredDefault")]
    [InlineData("optionalDefault")]
    [InlineData("requiredDuplicate")]
    [InlineData("optionalDuplicate")]
    [InlineData("overlap")]
    [InlineData("malformedTool")]
    [InlineData("tooManyTools")]
    [InlineData("nullDescriptor")]
    [InlineData("defaultCatalog")]
    public void Invalid_skill_metadata_is_rejected_at_registration(string error)
    {
        var descriptor = Descriptor();
        descriptor = error switch
        {
            "id" => descriptor with { Id = "../unsafe" },
            "longId" => descriptor with { Id = new string('a', 129) },
            "description" => descriptor with { Description = " " },
            "longDescription" => descriptor with { Description = new string('a', 2049) },
            "version" => descriptor with { Version = "invalid" },
            "digest" => descriptor with { ContentDigest = "G" + new string('A', 63) },
            "digestLower" => descriptor with { ContentDigest = new string('a', 64) },
            "requiredDefault" => descriptor with { RequiredTools = default },
            "optionalDefault" => descriptor with { OptionalTools = default },
            "requiredDuplicate" => descriptor with { RequiredTools = ["todo.list", "todo.list"] },
            "optionalDuplicate" => descriptor with { OptionalTools = ["todo.list", "todo.list"] },
            "overlap" => descriptor with { RequiredTools = ["todo.list"], OptionalTools = ["todo.list"] },
            "malformedTool" => descriptor with { RequiredTools = ["../read"] },
            "tooManyTools" => descriptor with { RequiredTools = [.. Enumerable.Range(0, 33).Select(index => "tool" + index)] },
            _ => descriptor
        };
        var provider = new Skills(error == "defaultCatalog" ? default : error == "nullDescriptor" ? [null!] : [descriptor]);
        Assert.Equal("PLUGIN_INVALID_SKILL", Assert.Throws<PluginValidationException>(
            () => PluginCatalog.Create([new Plugin("fixture.native", PluginContributions.Empty with { Skills = [provider] })])).Code);
    }

    [Fact]
    public void Duplicate_skills_are_rejected_globally_before_any_plugin_activation()
    {
        var first = new Plugin("fixture.one", PluginContributions.Empty with { Skills = [new Skills([Descriptor()])] });
        var second = new Plugin("fixture.two", PluginContributions.Empty with { Skills = [new Skills([Descriptor()])] });
        Assert.Equal("PLUGIN_DUPLICATE_SKILL", Assert.Throws<PluginValidationException>(() => PluginCatalog.Create([first, second])).Code);
    }

    [Theory]
    [InlineData("../context")]
    [InlineData("UPPER")]
    [InlineData("context..native")]
    public void Invalid_native_provider_ids_are_rejected(string id)
    {
        var contributions = PluginContributions.Empty with { AgentContexts = [new Context(id)] };
        Assert.Equal("PLUGIN_INVALID_AGENT_CONTEXT", Assert.Throws<PluginValidationException>(
            () => PluginCatalog.Create([new Plugin("fixture.native", contributions)])).Code);
        contributions = PluginContributions.Empty with { AgentObservers = [new Observer(id)] };
        Assert.Equal("PLUGIN_INVALID_AGENT_OBSERVER", Assert.Throws<PluginValidationException>(
            () => PluginCatalog.Create([new Plugin("fixture.native", contributions)])).Code);
    }

    [Fact]
    public void Native_provider_ids_are_globally_unique_across_contexts_and_observers()
    {
        var first = new Plugin("fixture.one", PluginContributions.Empty with { AgentContexts = [new Context("fixture.context")] });
        var second = new Plugin("fixture.two", PluginContributions.Empty with { AgentObservers = [new Observer("fixture.context")] });
        Assert.Equal("PLUGIN_DUPLICATE_AGENT_PROVIDER", Assert.Throws<PluginValidationException>(() => PluginCatalog.Create([first, second])).Code);
    }

    private static SkillDescriptor Descriptor() => new("daily-report", "Synthetic description", [], [], "1.0.0", new string('A', 64));
    private sealed class Plugin(string id, PluginContributions contributions) : IFgoPetPlugin
    {
        public PluginManifest Manifest { get; } = new(id, "1.0.0", 1, []);
        public PluginContributions Contributions { get; set; } = contributions;
        public ValueTask StartAsync(CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask StopAsync(CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Skills(ImmutableArray<SkillDescriptor> catalog) : ISkillProvider
    {
        public int CatalogReads { get; private set; }
        public ImmutableArray<SkillDescriptor> Items { get; set; } = catalog;
        public ImmutableArray<SkillDescriptor> Catalog { get { CatalogReads++; return Items; } }
        public ValueTask<SkillContent?> LoadAsync(ToolScope scope, string skillId, CancellationToken token) => ValueTask.FromResult<SkillContent?>(null);
    }
    private sealed class Context(string id) : IAgentContextProvider
    {
        public string Id { get; set; } = id;
        public bool ProvidesTrustedInstructions { get; set; } = true;
        public ValueTask<IReadOnlyList<AgentContextBlock>> BuildAsync(AgentContextRequest request, CancellationToken token)
            => ValueTask.FromResult<IReadOnlyList<AgentContextBlock>>([]);
    }
    private sealed class Observer(string id) : IAgentRunObserver
    {
        public string Id { get; set; } = id;
        public ValueTask ObserveAsync(AgentRunNotification notification, CancellationToken token) => ValueTask.CompletedTask;
    }
}
