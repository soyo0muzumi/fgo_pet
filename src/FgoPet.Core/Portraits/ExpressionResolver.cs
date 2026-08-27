using FgoPet.Core.Packs;

namespace FgoPet.Core.Portraits;

public sealed record ExpressionResolution(ExpressionSemantic Requested, string AssetId, bool UsedFallback);

public interface IExpressionResolver
{
    ExpressionResolution Resolve(ExpressionSemantic requested, AppearanceManifestV3 manifest);
}

/// <summary>
/// Resolves a requested core semantic to a declared expression asset, walking the
/// manifest's fallback map and terminating at <c>neutral</c> when a dead end is hit.
/// Cycles and unresolved semantics fail with <see cref="PackErrorCode.ExpressionMappingInvalid"/>.
/// </summary>
public sealed class ExpressionResolver : IExpressionResolver
{
    public ExpressionResolution Resolve(ExpressionSemantic requested, AppearanceManifestV3 manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return ResolveKey(ExpressionSemanticKeys.Key(requested), requested, manifest);
    }

    private static ExpressionResolution ResolveKey(
        string key,
        ExpressionSemantic requested,
        AppearanceManifestV3 manifest)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var current = key;
        var usedFallback = false;

        while (true)
        {
            if (!visited.Add(current))
            {
                throw new PackFailureException(new PackFailure(
                    PackErrorCode.ExpressionMappingInvalid,
                    $"表情语义 '{key}' 的回退链存在循环(再次到达 '{current}')。"));
            }

            if (manifest.ExpressionSemantics.TryGetValue(current, out var direct)
                && manifest.HasExpressionAsset(direct))
            {
                return new ExpressionResolution(
                    requested,
                    direct,
                    usedFallback || !string.Equals(current, key, StringComparison.Ordinal));
            }

            if (manifest.Fallback.TryGetValue(current, out var next))
            {
                usedFallback = true;
                current = next;
                continue;
            }

            // A dead end: fall back to neutral as the required terminal semantic.
            if (!string.Equals(current, ExpressionSemanticKeys.Neutral, StringComparison.Ordinal)
                && manifest.ExpressionSemantics.TryGetValue(ExpressionSemanticKeys.Neutral, out var neutralTarget)
                && manifest.HasExpressionAsset(neutralTarget))
            {
                return new ExpressionResolution(requested, neutralTarget, UsedFallback: true);
            }

            throw new PackFailureException(new PackFailure(
                PackErrorCode.ExpressionMappingInvalid,
                $"表情语义 '{key}' 无法解析到有效素材。"));
        }
    }
}