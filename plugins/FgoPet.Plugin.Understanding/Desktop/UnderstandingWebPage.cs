using System.Text.Json;
using FgoPet.Core.Dialogue;
using FgoPet.UiSdk;
using FgoPet.Understanding.Contracts;

namespace FgoPet.Understanding.Desktop;

public sealed record UnderstandingPagePresentation(string RoleName = "Current companion", string ProjectName = "",
    string ModelRecipient = "No model configured", bool AutomaticReviewAvailable = false);

/// <summary>Host-scoped review adapter. Payloads cannot choose another role/project.</summary>
public sealed class UnderstandingWebPage(UnderstandingService owner, Func<ConversationScope?> currentScope,
    IDialogueContextLifetime lifetime, IExperienceTimeline timeline,
    Func<ConversationScope, UnderstandingPagePresentation>? presentation = null) : ISettingsWebPage
{
    private ConversationScope? _boundScope;
    private long _boundGeneration = -1, _boundPolicyGeneration = -1;
    private string? _contextToken;
    public string SettingsPageId => "Understanding";
    public string ModulePath => "pages/understanding.js";
    public IReadOnlyList<string> Commands => ["understanding.get", "understanding.configure", "understanding.review"];
    public ISettingsWebPage CreateSession() => new UnderstandingWebPage(owner, currentScope, lifetime, timeline, presentation);
    public async ValueTask<WebSurfaceCommandResult> HandleCommandAsync(WebSurfaceMessage message, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            var payload = message.Payload;
            if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("pageId", out var page) || page.GetString() != SettingsPageId)
                return new(false, ErrorCode: "SETTINGS_INVALID_INPUT");
            var scope = currentScope();
            if (scope is null)
            { _boundScope = null; _contextToken = null; return new(false, ErrorCode: "UNDERSTANDING_SCOPE_UNAVAILABLE"); }
            if (message.Type == "understanding.get")
            {
                if (!Only(payload, "pageId")) return new(false, ErrorCode: "SETTINGS_INVALID_INPUT");
                return new(true, Snapshot(scope));
            }
            if (_boundScope != scope)
            { _boundScope = null; _contextToken = null; return new(false, ErrorCode: "UNDERSTANDING_SCOPE_CHANGED"); }
            if (_boundGeneration != lifetime.Generation ||
                _boundPolicyGeneration != owner.State(scope).Generation ||
                !Text(payload, "contextToken", out var contextToken) || contextToken != _contextToken)
                return new(false, ErrorCode: "UNDERSTANDING_SCOPE_CHANGED");
            UnderstandingPolicy? newPolicy = null; long expectedState = 0;
            string operationId = "", id = ""; int version = 0; UnderstandingAction action = default; string? correction = null;
            if (message.Type == "understanding.configure")
            {
                if (!Only(payload, "pageId", "contextToken", "revision", "policy") || !payload.TryGetProperty("revision", out var rev) || !rev.TryGetInt64(out expectedState) ||
                    !payload.TryGetProperty("policy", out var policy) || !TryPolicy(policy, out var value)) return new(false, ErrorCode: "SETTINGS_INVALID_INPUT");
                newPolicy = value;
            }
            else if (message.Type == "understanding.review")
            {
                if (!Only(payload, "pageId", "contextToken", "operationId", "id", "revision", "action", "correction") ||
                    !Text(payload, "operationId", out operationId) || !Text(payload, "id", out id) ||
                    !payload.TryGetProperty("revision", out var revision) || !revision.TryGetInt32(out version) ||
                    !Text(payload, "action", out var actionText) || !Enum.TryParse(actionText, true, out action) || !Enum.IsDefined(action))
                    return new(false, ErrorCode: "SETTINGS_INVALID_INPUT");
                if (payload.TryGetProperty("correction", out var text))
                { if (text.ValueKind != JsonValueKind.String) return new(false, ErrorCode: "SETTINGS_INVALID_INPUT"); correction = text.GetString(); }
                if (version < 1 || operationId.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)) || id.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)) ||
                    action == UnderstandingAction.Correct && (string.IsNullOrWhiteSpace(correction) || correction.Length > 2000))
                    return new(false, ErrorCode: "SETTINGS_INVALID_INPUT");
            }
            else return new(false, ErrorCode: "SETTINGS_INVALID_INPUT");
            // Validate first; only a valid, host-bound command may cancel/drain existing turns.
            using var suspension = await lifetime.SuspendAsync(token).ConfigureAwait(true);
            token.ThrowIfCancellationRequested();
            if (currentScope() != scope) return new(false, ErrorCode: "UNDERSTANDING_SCOPE_CHANGED");
            if (newPolicy is { } configured)
            {
                var boundary = configured.Mode != UnderstandingMode.Off && configured.ReadSources ? timeline.Boundary(scope) : 0;
                owner.Configure(scope, expectedState, configured, boundary, token);
            }
            else
            {
                var receipt = owner.Review(scope, operationId, id, version, action, correction, token);
                if (receipt.Items.FirstOrDefault(i => i.Code is not null)?.Code is { } code) return new(false, Snapshot(scope), code);
            }
            return new(true, Snapshot(scope));
        }
        catch (OperationCanceledException) { throw; }
        catch (InvalidOperationException) { return new(false, ErrorCode: "UNDERSTANDING_STALE_OR_UNAVAILABLE"); }
        catch (ArgumentException) { return new(false, ErrorCode: "SETTINGS_INVALID_INPUT"); }
        catch (Exception) { return new(false, ErrorCode: "UNDERSTANDING_UNAVAILABLE"); }
    }
    private object Snapshot(ConversationScope scope)
    {
        var accumulated = owner.List(scope, false); var pending = owner.List(scope, true);
        var state = owner.State(scope);
        if (_boundScope != scope || _boundGeneration != lifetime.Generation || _boundPolicyGeneration != state.Generation)
            _contextToken = Guid.NewGuid().ToString("N");
        _boundScope = scope; _boundGeneration = lifetime.Generation; _boundPolicyGeneration = state.Generation;
        var display = presentation?.Invoke(scope) ?? new(ProjectName: scope.ProjectId is null ? "Across this companion's conversations" : "Current project");
        return new { Scope = scope, State = state, ContextToken = _contextToken, Presentation = display,
            EffectiveIds = owner.Query(scope, "").Items.Select(e => e.Id).ToArray(), Accumulated = accumulated, Pending = pending,
            Changes = accumulated.Concat(pending).ToDictionary(e => e.Id, e => owner.Changes(scope, e.Id), StringComparer.Ordinal) };
    }
    private static bool Only(JsonElement p, params string[] names) => p.EnumerateObject().All(e => names.Contains(e.Name, StringComparer.Ordinal)) &&
        p.EnumerateObject().Select(e => e.Name).Distinct().Count() == p.EnumerateObject().Count();
    private static bool Text(JsonElement p, string name, out string value)
    {
        value = ""; if (!p.TryGetProperty(name, out var e) || e.ValueKind != JsonValueKind.String || e.GetString() is not { Length: > 0 and <= 128 } text) return false;
        value = text; return true;
    }
    private static bool TryPolicy(JsonElement p, out UnderstandingPolicy value)
    {
        value = new();
        if (p.ValueKind != JsonValueKind.Object || !Only(p, "mode", "enabled", "readSources", "externalProcessing", "saveCandidates", "useInDialogue") ||
            !Text(p, "mode", out var mode) || !Enum.TryParse<UnderstandingMode>(mode, true, out var parsed) || !Enum.IsDefined(parsed)) return false;
        var booleans = new List<bool>();
        foreach (var name in new[] { "enabled", "readSources", "externalProcessing", "saveCandidates", "useInDialogue" })
        {
            if (!p.TryGetProperty(name, out var b) || b.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
            booleans.Add(b.GetBoolean());
        }
        value = new(parsed, booleans[0], booleans[1], booleans[2], booleans[3], booleans[4]); return true;
    }
}
