using FgoPet.App.Dialogue;
using FgoPet.Core.Agents;

namespace FgoPet.App.Bootstrap;

/// <summary>Composition-root adapter from the Agent target catalog to safe Dialogue project options.</summary>
public sealed class AgentDialogueProjectCatalog : IDialogueProjectCatalog
{
    private readonly IAgentTargetCatalog _catalog;

    public AgentDialogueProjectCatalog(IAgentTargetCatalog catalog) =>
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));

    public async Task<DialogueProjectCatalogResult> ListAsync(CancellationToken cancellationToken = default)
    {
        var result = await _catalog.ListAsync(cancellationToken).ConfigureAwait(false);
        if (!result.IsAvailable)
        {
            return new(false, Array.Empty<DialogueProjectOption>(), result.SafeError);
        }

        var projects = result.Targets
            .Select(target =>
            {
                var project = new DialogueProjectOption(
                    target.TargetId,
                    target.ProjectName,
                    BuildDetail(target),
                    target.IsReadOnly,
                    target.CurrentBranch,
                    target.Revision,
                    target.ContextVersion);
                return project with
                {
                    Branches = target.Branches,
                    Access = target.Access,
                    Source = target.Source,
                    RefreshedAtUtc = target.RefreshedAtUtc,
                };
            })
            .ToArray();
        return new(true, projects);
    }

    private static string BuildDetail(AgentTargetDescriptor target)
    {
        var details = new List<string>
        {
            target.Access switch
            {
                "workspace-write" => "可写",
                "read-only" => "只读",
                _ when target.IsReadOnly => "只读",
                _ => "可访问",
            },
        };
        if (!string.IsNullOrWhiteSpace(target.CurrentBranch))
        {
            details.Add($"分支 {target.CurrentBranch}");
        }
        else if (target.Branches.Count > 0)
        {
            details.Add($"可选分支 {target.Branches.Count} 个");
        }
        if (!string.IsNullOrWhiteSpace(target.Revision))
        {
            details.Add($"版本 {target.Revision}");
        }
        if (!string.IsNullOrWhiteSpace(target.Source))
        {
            var source = target.Source == "local-adapter" ? "本地助手" : target.Source;
            details.Add($"来源 {source}");
        }
        if (target.RefreshedAtUtc is { } refreshedAtUtc)
        {
            details.Add($"刷新 {refreshedAtUtc.ToLocalTime().ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture)}");
        }
        return string.Join(" · ", details);
    }

}