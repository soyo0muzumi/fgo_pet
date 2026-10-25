using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using FgoPet.Extensibility;

namespace FgoPet.Kernel.Agent;

public sealed record SkillLoadResult(string? PluginId, SkillContent? Content, string? ErrorCode);

/// <summary>Trusted catalog view and bounded loader. Run activation and budgets belong to the coordinator.</summary>
public sealed class SkillRegistry(PluginCatalog catalog, PluginRuntime runtime, ToolRegistry tools)
{
    public const int MaxBodyBytes = 32 * 1024;
    private static readonly UTF8Encoding BodyEncoding = new(false, true);

    public ImmutableArray<SkillDescriptor> GetSkills(ToolScope scope)
    {
        if (!ValidScope(scope)) return [];
        var result = ImmutableArray.CreateBuilder<SkillDescriptor>();
        var listings = new Dictionary<ISkillProvider, (ImmutableArray<SkillDescriptor> Items, string? Error)>(ReferenceEqualityComparer.Instance);
        foreach (var entry in catalog.Skills)
        {
            if (!runtime.IsActive(entry.PluginId)) continue;
            if (!listings.TryGetValue(entry.Provider, out var listing))
            {
                var error = InspectListing(entry.Provider, scope, out var items);
                listing = (items, error);
                listings.Add(entry.Provider, listing);
            }
            if (listing.Error is null && listing.Items.Any(item => item.Id == entry.Descriptor.Id)
                && RequiredToolsAvailable(entry.Descriptor, scope) && runtime.IsActive(entry.PluginId))
                result.Add(entry.Descriptor);
        }
        return result.ToImmutable();
    }

    public async ValueTask<SkillLoadResult> LoadAsync(ToolScope scope, string id, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // Only a captured canonical ID is passed to the provider; lookup never interprets an ID as a path.
        var entry = catalog.Skills.FirstOrDefault(candidate => candidate.Descriptor.Id == id);
        if (entry is null) return new(null, null, "SKILL_NOT_FOUND");
        var error = CheckCurrent(entry, scope);
        if (error is not null) return new(entry.PluginId, null, error);
        SkillContent? content;
        try { content = await entry.Provider.LoadAsync(scope, entry.Descriptor.Id, token); }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return new(entry.PluginId, null, "SKILL_UNAVAILABLE"); }
        token.ThrowIfCancellationRequested();
        error = CheckCurrent(entry, scope);
        if (error is not null) return new(entry.PluginId, null, error);
        if (content is null) return new(entry.PluginId, null, "SKILL_UNAVAILABLE");
        if (!SameDescriptor(entry.Descriptor, content.Descriptor) || content.Instructions is null)
            return new(entry.PluginId, null, "SKILL_CONTENT_CHANGED");
        try
        {
            if (BodyEncoding.GetByteCount(content.Instructions) > MaxBodyBytes)
                return new(entry.PluginId, null, "SKILL_CONTENT_TOO_LARGE");
            var digest = Convert.ToHexString(SHA256.HashData(BodyEncoding.GetBytes(content.Instructions)));
            if (digest != entry.Descriptor.ContentDigest) return new(entry.PluginId, null, "SKILL_CONTENT_CHANGED");
        }
        catch (EncoderFallbackException) { return new(entry.PluginId, null, "SKILL_CONTENT_CHANGED"); }
        return new(entry.PluginId, new SkillContent(entry.Descriptor, content.Instructions), null);
    }

    private string? CheckCurrent(RegisteredSkill entry, ToolScope scope)
    {
        if (!ValidScope(scope) || !runtime.IsActive(entry.PluginId)) return "SKILL_UNAVAILABLE";
        var error = InspectListing(entry.Provider, scope, out var items);
        if (error is not null) return error;
        if (!runtime.IsActive(entry.PluginId)) return "SKILL_UNAVAILABLE";
        if (!items.Any(item => item.Id == entry.Descriptor.Id)) return "SKILL_NOT_FOUND";
        return RequiredToolsAvailable(entry.Descriptor, scope) ? null : "SKILL_REQUIRED_TOOL_UNAVAILABLE";
    }

    private string? InspectListing(ISkillProvider provider, ToolScope scope, out ImmutableArray<SkillDescriptor> items)
    {
        items = [];
        try
        {
            var listed = provider.ListSkills(scope);
            if (listed is null) return "SKILL_CONTENT_CHANGED";
            if (listed is ImmutableArray<SkillDescriptor> array && array.IsDefault) return "SKILL_CONTENT_CHANGED";
            var captured = catalog.Skills.Where(entry => ReferenceEquals(entry.Provider, provider)).ToArray();
            if (listed.Count > captured.Length) return "SKILL_CONTENT_CHANGED";
            var snapshot = listed.ToImmutableArray();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var descriptor in snapshot)
            {
                if (descriptor is null || !ids.Add(descriptor.Id)) return "SKILL_CONTENT_CHANGED";
                var entry = captured.FirstOrDefault(candidate => candidate.Descriptor.Id == descriptor.Id);
                if (entry is null || !SameDescriptor(entry.Descriptor, descriptor)) return "SKILL_CONTENT_CHANGED";
            }
            items = snapshot;
            return null;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return "SKILL_UNAVAILABLE"; }
    }

    private bool RequiredToolsAvailable(SkillDescriptor descriptor, ToolScope scope)
        => descriptor.RequiredTools.All(name => tools.TryResolve(name, scope, out _));

    private static bool SameDescriptor(SkillDescriptor captured, SkillDescriptor? current)
        => current is not null && captured.Id == current.Id && captured.Description == current.Description
            && captured.Version == current.Version && captured.ContentDigest == current.ContentDigest
            && !current.RequiredTools.IsDefault && !current.OptionalTools.IsDefault
            && captured.RequiredTools.SequenceEqual(current.RequiredTools, StringComparer.Ordinal)
            && captured.OptionalTools.SequenceEqual(current.OptionalTools, StringComparer.Ordinal);

    private static bool ValidScope(ToolScope? scope)
        => scope is not null && !string.IsNullOrWhiteSpace(scope.ConversationId)
            && !string.IsNullOrWhiteSpace(scope.RoleId)
            && (scope.ProjectId is null || !string.IsNullOrWhiteSpace(scope.ProjectId));
}
