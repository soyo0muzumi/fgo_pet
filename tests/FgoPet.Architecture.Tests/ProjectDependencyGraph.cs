namespace FgoPet.Architecture.Tests;

internal static class ProjectDependencyGraph
{
    // All evaluated edges affect build order, including runtime companions.
    internal static async Task<int> VerifyAcyclicAsync(IEnumerable<string> roots,
        IProjectReferenceEvaluator evaluator, string configuration, string? runtimeIdentifier = null,
        CancellationToken cancellationToken = default)
    {
        var completed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var active = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var route = new List<string>();
        foreach (var root in roots) await Visit(Path.GetFullPath(root));
        return completed.Count;

        async Task Visit(string path)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (completed.Contains(path)) return;
            if (!active.Add(path))
                throw new InvalidOperationException("Project reference cycle: " + string.Join(" -> ", route.Append(path).Select(Path.GetFileName)));
            route.Add(path);
            var project = await evaluator.EvaluateAsync(path, configuration, runtimeIdentifier, cancellationToken);
            foreach (var reference in project.References) await Visit(Path.GetFullPath(reference.ProjectPath));
            route.RemoveAt(route.Count - 1);
            active.Remove(path);
            completed.Add(path);
        }
    }
}
