using System.Collections.Immutable;
using System.Text.RegularExpressions;

namespace FgoPet.Extensibility;

public sealed class PluginValidationException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

public sealed record RegisteredPlugin(PluginManifest Manifest, IFgoPetPlugin Instance);
public sealed record RegisteredTool(string PluginId, ToolDescriptor Descriptor, IToolProvider Provider);
public sealed record RegisteredWorkspace(string PluginId, WorkspaceDescriptor Descriptor);
public sealed record RegisteredTransientSurface(string PluginId, TransientSurfaceDescriptor Descriptor);
public sealed record RegisteredSettingsPage(string PluginId, SettingsPageDescriptor Descriptor);

/// <summary>Captures typed contributions once; there is no mutable registration API after construction.</summary>
public sealed class PluginCatalog
{
    private static readonly Regex IdPattern = new("\\A[a-z][a-z0-9]*(?:[.-][a-z0-9]+)*\\z", RegexOptions.CultureInvariant);
    private static readonly Regex ToolPattern = new("\\A[a-z][a-z0-9_]{0,63}\\z", RegexOptions.CultureInvariant);
    public ImmutableArray<RegisteredPlugin> Plugins { get; }
    public ImmutableArray<RegisteredTool> Tools { get; }
    public ImmutableArray<RegisteredWorkspace> Workspaces { get; }
    public ImmutableArray<RegisteredTransientSurface> TransientSurfaces { get; }
    public ImmutableArray<RegisteredSettingsPage> SettingsPages { get; }
    public ImmutableArray<RegisteredContribution<IConversationContextProvider>> Contexts { get; }
    public ImmutableArray<RegisteredContribution<IConversationContinuationProvider>> Continuations { get; }
    public ImmutableArray<RegisteredContribution<ITextReplyInterpreter>> TextInterpreters { get; }
    public ImmutableArray<RegisteredContribution<ICompanionSignalSource>> Signals { get; }
    public ImmutableArray<RegisteredContribution<IConversationPromptProvider>> Prompts { get; }
    public ImmutableArray<RegisteredContribution<IPostTurnObserver>> PostTurnObservers { get; }
    public ImmutableArray<RegisteredContribution<ICompanionFeedbackProvider>> FeedbackProviders { get; }

    private PluginCatalog(ImmutableArray<RegisteredPlugin> plugins, ImmutableArray<RegisteredTool> tools,
        ImmutableArray<RegisteredWorkspace> workspaces, ImmutableArray<RegisteredTransientSurface> transients,
        ImmutableArray<RegisteredSettingsPage> settings,
        ImmutableArray<RegisteredContribution<IConversationContextProvider>> contexts,
        ImmutableArray<RegisteredContribution<IConversationContinuationProvider>> continuations,
        ImmutableArray<RegisteredContribution<ITextReplyInterpreter>> interpreters,
        ImmutableArray<RegisteredContribution<ICompanionSignalSource>> signals,
        ImmutableArray<RegisteredContribution<IConversationPromptProvider>> prompts,
        ImmutableArray<RegisteredContribution<IPostTurnObserver>> observers,
        ImmutableArray<RegisteredContribution<ICompanionFeedbackProvider>> feedbackProviders)
    {
        Plugins = plugins; Tools = tools; Workspaces = workspaces; TransientSurfaces = transients; SettingsPages = settings;
        Contexts = contexts; Continuations = continuations; TextInterpreters = interpreters;
        Signals = signals;
        Prompts = prompts; PostTurnObservers = observers; FeedbackProviders = feedbackProviders;
    }

    public static PluginCatalog Create(IEnumerable<IFgoPetPlugin> plugins)
    {
        ArgumentNullException.ThrowIfNull(plugins);
        var entries = new Dictionary<string, (RegisteredPlugin Plugin, PluginContributions Contributions)>(StringComparer.Ordinal);
        foreach (var instance in plugins)
        {
            if (instance is null) throw new PluginValidationException("PLUGIN_MISSING_INSTANCE");
            var manifest = instance.Manifest;
            if (manifest is null || !IsId(manifest.Id)) throw new PluginValidationException("PLUGIN_INVALID_ID");
            if (!Version.TryParse(manifest.Version, out _) || manifest.ApiVersion != 1) throw new PluginValidationException("PLUGIN_UNSUPPORTED_VERSION");
            if (manifest.Dependencies.IsDefault || manifest.Dependencies.Any(id => !IsId(id))
                || manifest.Dependencies.Distinct(StringComparer.Ordinal).Count() != manifest.Dependencies.Length)
                throw new PluginValidationException("PLUGIN_INVALID_DEPENDENCIES");
            var contributions = instance.Contributions;
            if (contributions is null || contributions.Tools.IsDefault || contributions.Workspaces.IsDefault || contributions.TransientSurfaces.IsDefault || contributions.SettingsPages.IsDefault
                || contributions.Contexts.IsDefault || contributions.Continuations.IsDefault || contributions.TextInterpreters.IsDefault || contributions.Signals.IsDefault
                || contributions.Contexts.Any(provider => provider is null) || contributions.Continuations.Any(provider => provider is null)
                || contributions.TextInterpreters.Any(provider => provider is null) || contributions.Signals.Any(provider => provider is null)
                || contributions.FeedbackProviders.IsDefault || contributions.FeedbackProviders.Any(provider => provider is null)
                || contributions.Prompts.IsDefault || contributions.PostTurnObservers.IsDefault
                || contributions.Prompts.Any(provider => provider is null) || contributions.PostTurnObservers.Any(provider => provider is null))
                throw new PluginValidationException("PLUGIN_INVALID_CONTRIBUTIONS");
            if (!entries.TryAdd(manifest.Id, (new(manifest, instance), contributions))) throw new PluginValidationException("PLUGIN_DUPLICATE_ID");
        }

        var sorted = ImmutableArray.CreateBuilder<RegisteredPlugin>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var active = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in entries.Keys.Order(StringComparer.Ordinal)) Visit(id);

        var tools = ImmutableArray.CreateBuilder<RegisteredTool>();
        var workspaces = ImmutableArray.CreateBuilder<RegisteredWorkspace>();
        var transients = ImmutableArray.CreateBuilder<RegisteredTransientSurface>();
        var settings = ImmutableArray.CreateBuilder<RegisteredSettingsPage>();
        var contexts = ImmutableArray.CreateBuilder<RegisteredContribution<IConversationContextProvider>>();
        var continuations = ImmutableArray.CreateBuilder<RegisteredContribution<IConversationContinuationProvider>>();
        var interpreters = ImmutableArray.CreateBuilder<RegisteredContribution<ITextReplyInterpreter>>();
        var signals = ImmutableArray.CreateBuilder<RegisteredContribution<ICompanionSignalSource>>();
        var prompts = ImmutableArray.CreateBuilder<RegisteredContribution<IConversationPromptProvider>>();
        var observers = ImmutableArray.CreateBuilder<RegisteredContribution<IPostTurnObserver>>();
        var feedbackProviders = ImmutableArray.CreateBuilder<RegisteredContribution<ICompanionFeedbackProvider>>();
        var toolIds = new HashSet<string>(StringComparer.Ordinal);
        var surfaceIds = new HashSet<string>(StringComparer.Ordinal);
        var settingsIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var plugin in sorted)
        {
            var contribution = entries[plugin.Manifest.Id].Contributions;
            feedbackProviders.AddRange(contribution.FeedbackProviders.Select(provider => new RegisteredContribution<ICompanionFeedbackProvider>(plugin.Manifest.Id, provider)));
            contexts.AddRange(contribution.Contexts.Select(provider => new RegisteredContribution<IConversationContextProvider>(plugin.Manifest.Id, provider)));
            continuations.AddRange(contribution.Continuations.Select(provider => new RegisteredContribution<IConversationContinuationProvider>(plugin.Manifest.Id, provider)));
            interpreters.AddRange(contribution.TextInterpreters.Select(provider => new RegisteredContribution<ITextReplyInterpreter>(plugin.Manifest.Id, provider)));
            signals.AddRange(contribution.Signals.Select(provider => new RegisteredContribution<ICompanionSignalSource>(plugin.Manifest.Id, provider)));
            prompts.AddRange(contribution.Prompts.Select(provider => new RegisteredContribution<IConversationPromptProvider>(plugin.Manifest.Id, provider)));
            observers.AddRange(contribution.PostTurnObservers.Select(provider => new RegisteredContribution<IPostTurnObserver>(plugin.Manifest.Id, provider)));
            foreach (var provider in contribution.Tools)
            {
                var descriptor = provider?.Descriptor;
                if (descriptor is null || !ToolPattern.IsMatch(descriptor.Name)) throw new PluginValidationException("PLUGIN_INVALID_TOOL");
                if (!toolIds.Add(descriptor.Name)) throw new PluginValidationException("PLUGIN_DUPLICATE_TOOL");
                tools.Add(new(plugin.Manifest.Id, descriptor, provider!));
            }
            foreach (var descriptor in contribution.Workspaces)
            {
                if (descriptor is null || !IsId(descriptor.Id) || !IsTitle(descriptor.Title)) throw new PluginValidationException("PLUGIN_INVALID_WORKSPACE");
                if (!surfaceIds.Add(descriptor.Id)) throw new PluginValidationException("PLUGIN_DUPLICATE_WORKSPACE");
                workspaces.Add(new(plugin.Manifest.Id, descriptor));
            }
            foreach (var descriptor in contribution.TransientSurfaces)
            {
                if (descriptor is null || !IsId(descriptor.Id) || !IsTitle(descriptor.Title)
                    || !double.IsFinite(descriptor.PreferredWidthDip) || descriptor.PreferredWidthDip is < 100 or > 1200
                    || !double.IsFinite(descriptor.PreferredHeightDip) || descriptor.PreferredHeightDip is < 100 or > 1200
                    || !Enum.IsDefined(descriptor.Anchor))
                    throw new PluginValidationException("PLUGIN_INVALID_TRANSIENT_SURFACE");
                if (!surfaceIds.Add(descriptor.Id)) throw new PluginValidationException("PLUGIN_DUPLICATE_SURFACE");
                transients.Add(new(plugin.Manifest.Id, descriptor));
            }
            foreach (var descriptor in contribution.SettingsPages)
            {
                if (descriptor is null || !IsId(descriptor.Id) || !IsTitle(descriptor.Title)) throw new PluginValidationException("PLUGIN_INVALID_SETTINGS");
                if (!settingsIds.Add(descriptor.Id)) throw new PluginValidationException("PLUGIN_DUPLICATE_SETTINGS");
                settings.Add(new(plugin.Manifest.Id, descriptor));
            }
        }
        return new(sorted.ToImmutable(), tools.ToImmutable(), workspaces.ToImmutable(), transients.ToImmutable(), settings.ToImmutable(),
            contexts.ToImmutable(), continuations.ToImmutable(), interpreters.ToImmutable(), signals.ToImmutable(),
            prompts.ToImmutable(), observers.ToImmutable(), feedbackProviders.ToImmutable());

        void Visit(string id)
        {
            if (visited.Contains(id)) return;
            if (!entries.TryGetValue(id, out var entry)) throw new PluginValidationException("PLUGIN_MISSING_DEPENDENCY");
            if (!active.Add(id)) throw new PluginValidationException("PLUGIN_DEPENDENCY_CYCLE");
            foreach (var dependency in entry.Plugin.Manifest.Dependencies) Visit(dependency);
            active.Remove(id); visited.Add(id); sorted.Add(entry.Plugin);
        }
    }

    private static bool IsId(string? id) => id is { Length: <= 128 } && IdPattern.IsMatch(id);
    private static bool IsTitle(string? title) => !string.IsNullOrWhiteSpace(title) && title.Length <= 256;
}
