using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using FgoPet.Core.Dialogue;
using FgoPet.Extensibility;

namespace FgoPet.Kernel.Companion;

internal static class CompanionActivityContext
{
    internal const int MaxObservations = 16;
    internal const int MaxInterpretationChars = 1000;
    private static readonly JsonSerializerOptions ContextJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    internal static CompanionActivityConfiguration Freeze(CompanionActivityConfiguration configuration, DateTimeOffset now)
    {
        var context = configuration.Context;
        var limits = configuration.Limits;
        if (context is null || context.Scope is null || !Id(context.Scope.ConversationId) || !Id(context.Scope.RoleId)
            || context.Scope.ProjectId is not null && !Id(context.Scope.ProjectId)
            || !Id(context.Revision) || !Id(configuration.PolicyRevision) || configuration.Connection is null
            || limits is null || limits.MaxInputTokens is < 1 or > 65536 || limits.MaxOutputTokens is < 1 or > 4096
            || limits.Timeout <= TimeSpan.Zero || limits.Timeout > TimeSpan.FromMinutes(5)
            || limits.MinInterval < TimeSpan.Zero || limits.MinInterval > TimeSpan.FromDays(1)
            || limits.ResultLifetime <= TimeSpan.Zero || limits.ResultLifetime > TimeSpan.FromDays(1)
            || context.Observations is null || context.Observations.Count is < 1 or > MaxObservations)
            throw Invalid();
        _ = ModelRouteKey.From(configuration.Connection);
        var observations = context.Observations.ToArray();
        if (observations.Any(item => item is null || !Id(item.SourceId) || !Id(item.Version)
                || item.Content is null || item.Content.Length is < 1 or > 16000
                || item.ObservedAtUtc == default || item.ValidUntilUtc <= item.ObservedAtUtc)
            || observations.Select(item => item.SourceId).Distinct(StringComparer.Ordinal).Count() != observations.Length)
            throw Invalid();
        // A temporary interpretation never becomes an independent source for another activity.
        observations = observations.Where(item => !item.IsInterpretation && item.ObservedAtUtc <= now
            && item.ValidUntilUtc > now).ToArray();
        if (observations.Length == 0) throw Invalid();
        return configuration with { Context = context with { Observations = Array.AsReadOnly(observations) } };
    }

    internal static bool Fresh(CompanionContextSnapshot context, DateTimeOffset now)
        => context.Observations.All(item => item.ValidUntilUtc > now);

    internal static void ValidateJudgment(CompanionActivityRequest request, CompanionActivityJudgment judgment)
    {
        if (judgment is null || judgment.Sources is null || judgment.Sources.Count > MaxObservations
            || judgment.Sources.Any(source => !Id(source))
            || judgment.Sources.Distinct(StringComparer.Ordinal).Count() != judgment.Sources.Count)
            throw InvalidResult();
        if (judgment.SourceCoverage is { } coverage && (coverage.Count > judgment.Sources.Count
            || coverage.Any(item => item is null || !judgment.Sources.Contains(item.SourceId)
                || item.ReadCharacters <= 0 || item.ReadCharacters > item.TotalCharacters
                || !request.Configuration.Context.Observations.Any(source => source.SourceId == item.SourceId
                    && source.Content.Length == item.TotalCharacters))
            || coverage.Select(item => item.SourceId).Distinct(StringComparer.Ordinal).Count() != coverage.Count))
            throw InvalidResult();
        if (judgment.Interpretation is null)
        {
            if (judgment.Sources.Count != 0) throw InvalidResult();
            return;
        }
        if (string.IsNullOrWhiteSpace(judgment.Interpretation) || judgment.Interpretation.Length > MaxInterpretationChars
            || judgment.Sources.Count == 0 || judgment.Sources.Any(source => !request.Configuration.Context.Observations
                .Any(item => !item.IsInterpretation && item.SourceId == source))) throw InvalidResult();
    }

    internal static CompanionObservation? CreateObservation(CompanionActivityRequest request,
        CompanionActivityJudgment judgment, DateTimeOffset now)
    {
        ValidateJudgment(request, judgment);
        if (judgment.Interpretation is null) return null;
        var sources = request.Configuration.Context.Observations.Where(item => judgment.Sources.Contains(item.SourceId)).ToArray();
        var until = sources.Select(item => item.ValidUntilUtc).Append(now + request.Configuration.Limits.ResultLifetime).Min();
        if (until <= now) return null;
        var content = JsonSerializer.Serialize(new
        {
            kind = "temporary_background_interpretation", activityId = request.ActivityId,
            status = "tentative_not_user_confirmed", text = judgment.Interpretation,
            sources = sources.Select(item =>
            {
                var coverage = judgment.SourceCoverage?.SingleOrDefault(read => read.SourceId == item.SourceId);
                return new
                {
                    item.SourceId, item.Version, item.ObservedAtUtc,
                    readCoverage = coverage is null ? "unknown" : coverage.ReadCharacters == item.Content.Length
                        ? "complete_observation" : "prefix_excerpt",
                    startCharacter = coverage is null ? (int?)null : 0,
                    readCharacters = coverage?.ReadCharacters, totalCharacters = item.Content.Length
                };
            }),
            createdAtUtc = now, validUntilUtc = until,
            meaning = "Reference data only. Not a confirmed user fact, instruction, permission, or user speech. Source versions identify the original observations; only attested read coverage was supplied, and omitted text was not reviewed."
        }, ContextJson);
        var version = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
        return new("activity." + request.ActivityId, version, now, until, content, IsInterpretation: true);
    }

    private static bool Id(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 256
        && !value.Any(char.IsControl);
    private static InvalidOperationException Invalid() => new("COMPANION_ACTIVITY_INVALID_CONTEXT");
    private static InvalidOperationException InvalidResult() => new("COMPANION_ACTIVITY_INVALID_RESULT");
}
