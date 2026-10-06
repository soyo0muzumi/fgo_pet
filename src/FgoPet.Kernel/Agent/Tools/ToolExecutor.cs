using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FgoPet.Extensibility;

namespace FgoPet.Kernel.Agent;

/// <summary>Guards invocation; only the state owner's intent port persists ToolStarted.</summary>
public sealed class ToolExecutor(ToolRegistry registry, ToolScope hostScope, IAgentRunFence fence)
{
    internal async ValueTask<ToolExecutionOutcome> ExecuteAsync(ToolExecutionRequest request, RegisteredTool tool,
        JsonElement arguments, IToolExecutionIntent intent, CancellationToken token, bool commandApproved = false,
        Func<ToolExecutionOutcome?>? approvalGuard = null, ToolResourceAuthorization? resourceAuthorization = null,
        ToolBusinessConfirmation? businessConfirmation = null)
    {
        var denied = Guard(request, tool, token);
        denied ??= approvalGuard?.Invoke();
        if (denied is not null) return denied;
        // M1 has no product policy/approval broker. Neither model text nor an intent grants permission.
        if (tool.Descriptor.Effect == ToolEffect.Command && (!commandApproved || approvalGuard is null))
            return ToolResultNormalizer.Failure("TOOL_AUTHORIZATION_DENIED");

        var invocation = CreateInvocation(request, arguments, resourceAuthorization, businessConfirmation);
        await intent.CommitStartedAsync(tool.Descriptor, token);
        // Persistence can yield to shutdown or a role/project switch. Never use the earlier admission check.
        denied = Guard(request, tool, token);
        denied ??= approvalGuard?.Invoke();
        if (denied is not null) return denied;
        try
        {
            var result = await tool.Provider.InvokeAsync(invocation, token);
            token.ThrowIfCancellationRequested();
            return ToolResultNormalizer.Normalize(tool.Descriptor.Effect, result);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            return tool.Descriptor.Effect == ToolEffect.Command
                ? ToolResultNormalizer.Unknown()
                : ToolResultNormalizer.Failure("TOOL_EXECUTION_FAILED");
        }
    }

    internal static ToolInvocation CreateInvocation(ToolExecutionRequest request, JsonElement arguments,
        ToolResourceAuthorization? resource = null, ToolBusinessConfirmation? business = null)
    {
        var key = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
            new { request.Identity.RunId, request.StepNumber, request.Call.CallId })));
        return new(request.Identity.Scope, arguments.Clone()) { ExecutionContext =
            new(request.Identity.RunId, request.StepNumber, request.Call.CallId, key)
                { ResourceAuthorization = resource, BusinessConfirmation = business } };
    }

    internal ToolExecutionOutcome? Guard(ToolExecutionRequest request, RegisteredTool tool, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (request.Identity.Scope != hostScope) return ToolResultNormalizer.Failure("TOOL_SCOPE_DENIED");
        fence.EnsureCurrent(request.Identity, token);
        return registry.IsCurrent(tool, hostScope) ? null : ToolResultNormalizer.Failure("TOOL_NOT_FOUND");
    }
}

/// <summary>Pure observation normalization; it cannot authorize or invoke a provider.</summary>
public static class ToolResultNormalizer
{
    public const int MaxPayloadBytes = 64 * 1024;
    public const int MaxErrorCodeLength = 128;

    public static ToolExecutionOutcome Normalize(ToolEffect effect, ToolResult? result)
    {
        if (effect == ToolEffect.Command && (result is null
            || result.ExecutionState is not (ToolExecutionState.NotExecuted or ToolExecutionState.Committed or ToolExecutionState.Unknown)
            || (result.Success && result.ExecutionState == ToolExecutionState.NotExecuted))) return Unknown();
        if (result is null) return Failure("TOOL_INVALID_OUTPUT");
        JsonElement payload;
        try
        {
            payload = result.Payload.ValueKind == JsonValueKind.Undefined ? EmptyPayload() : result.Payload.Clone();
            if (Encoding.UTF8.GetByteCount(payload.GetRawText()) > MaxPayloadBytes)
                return result.ExecutionState == ToolExecutionState.Unknown ? Unknown() : Failure("TOOL_OUTPUT_TOO_LARGE", result.ExecutionState);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ObjectDisposedException)
        {
            return result.ExecutionState == ToolExecutionState.Unknown ? Unknown() : Failure("TOOL_INVALID_OUTPUT", result.ExecutionState);
        }
        var code = IsSafeCode(result.ErrorCode) ? result.ErrorCode : null;
        if (!result.Success && code is null) code = "TOOL_EXECUTION_FAILED";
        if (result.ErrorCode is not null && code != result.ErrorCode)
            return result.ExecutionState == ToolExecutionState.Unknown ? Unknown() : Failure("TOOL_EXECUTION_FAILED", result.ExecutionState);
        if (effect == ToolEffect.Command && result.ExecutionState == ToolExecutionState.Unknown)
            return new(ToolExecutionOutcomeKind.ExecutionUnknown,
                result with { Success = false, Payload = payload, ErrorCode = code ?? "TOOL_EXECUTION_UNKNOWN", Conversation = null },
                ErrorCode: "TOOL_EXECUTION_UNKNOWN");
        return new(ToolExecutionOutcomeKind.Completed,
            result with { Payload = payload, ErrorCode = code, Conversation = null });
    }

    internal static ToolExecutionOutcome Failure(string code, ToolExecutionState? state = ToolExecutionState.NotExecuted)
        => new(ToolExecutionOutcomeKind.Completed, new ToolResult(false, EmptyPayload(), code) { ExecutionState = state });

    internal static ToolExecutionOutcome Unknown()
        => new(ToolExecutionOutcomeKind.ExecutionUnknown,
            new ToolResult(false, EmptyPayload(), "TOOL_EXECUTION_UNKNOWN") { ExecutionState = ToolExecutionState.Unknown },
            ErrorCode: "TOOL_EXECUTION_UNKNOWN");

    private static JsonElement EmptyPayload() => JsonSerializer.SerializeToElement(new { });
    private static bool IsSafeCode(string? code)
        => code is { Length: > 0 and <= MaxErrorCodeLength }
            && code[0] is >= 'A' and <= 'Z'
            && code.All(character => character is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_');
}
