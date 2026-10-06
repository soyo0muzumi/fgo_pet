using System.Text;
using FgoPet.Extensibility;

namespace FgoPet.Kernel.Agent;

/// <summary>M1 resolve/schema boundary. The executor retains authorization and invocation guards.</summary>
public sealed class ToolExecutionPipeline(ToolRegistry registry, ToolExecutor executor,
    IToolPolicy? policy = null, ApprovalBroker? approvals = null, string? rootAuthorizationId = null) : IToolExecutionPipeline
{
    public ValueTask<ToolExecutionOutcome> AdvanceAsync(ToolExecutionRequest request, IToolExecutionIntent intent,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(intent);
        if (!request.Call.IsResolved || !registry.TryResolve(request.Call.Name, request.Identity.Scope, out var tool))
            return ValueTask.FromResult(ToolResultNormalizer.Failure("TOOL_NOT_FOUND"));
        if (request.Call.ArgumentsJson is null || Encoding.UTF8.GetByteCount(request.Call.ArgumentsJson) > ModelProtocol.MaxArgumentsBytes)
            return ValueTask.FromResult(ToolResultNormalizer.Failure("TOOL_ARGUMENTS_TOO_LARGE"));
        if (!request.Call.TryGetArguments(out var arguments))
            return ValueTask.FromResult(ToolResultNormalizer.Failure("TOOL_INVALID_ARGUMENTS"));
        if (!ToolArgumentsValidator.Validate(tool.Descriptor.Parameters, arguments, out var error))
            return ValueTask.FromResult(ToolResultNormalizer.Failure(error!));
        var guard = executor.Guard(request, tool, token);
        if (guard is not null) return ValueTask.FromResult(guard);
        ToolResourceAuthorization? resource;
        try { resource = (tool.Provider as IToolResourceAuthorizationProvider)?.GetAuthorization(request.Identity.Scope); }
        catch (WorkspaceAccessException) { return ValueTask.FromResult(ToolResultNormalizer.Failure("TOOL_SCOPE_DENIED")); }
        ToolExecutionOutcome? CheckResource()
        {
            try
            {
                return (tool.Provider as IToolResourceAuthorizationProvider)?.GetAuthorization(request.Identity.Scope) == resource
                    ? null : ToolResultNormalizer.Failure("TOOL_SCOPE_DENIED");
            }
            catch (WorkspaceAccessException) { return ToolResultNormalizer.Failure("TOOL_SCOPE_DENIED"); }
        }
        var root = resource?.Id ?? rootAuthorizationId;
        var decision = policy?.Decide(request, tool) ?? (tool.Descriptor.Effect == FgoPet.Extensibility.ToolEffect.Command
            ? ToolPolicyDecision.Deny : ToolPolicyDecision.Allow);
        if (!Enum.IsDefined(decision) || decision == ToolPolicyDecision.Deny)
            return ValueTask.FromResult(ToolResultNormalizer.Failure("TOOL_AUTHORIZATION_DENIED"));
        if (request.Approval is not null && approvals is not null)
        {
            var approved = request.Approval;
            var business = approved.Tool.BusinessConfirmation;
            ToolExecutionOutcome? Revalidate()
            {
                var resourceDenied = CheckResource();
                if (resourceDenied is not null) return resourceDenied;
                var currentDecision = policy?.Decide(request, tool) ?? ToolPolicyDecision.Deny;
                if (!Enum.IsDefined(currentDecision) || currentDecision == ToolPolicyDecision.Deny)
                    return ToolResultNormalizer.Failure("TOOL_AUTHORIZATION_DENIED");
                if (tool.Provider is IToolBusinessConfirmationProvider owner)
                {
                    if (business is null) return ToolResultNormalizer.Failure("TOOL_BUSINESS_CONFIRMATION_REQUIRED");
                    try { owner.ValidateConfirmation(ToolExecutor.CreateInvocation(request, arguments, resource, business), business, token); }
                    catch (ToolBusinessConfirmationException) { return ToolResultNormalizer.Failure("TOOL_BUSINESS_CONFIRMATION_STALE"); }
                }
                else if (business is not null) return ToolResultNormalizer.Failure("TOOL_APPROVAL_STALE");
                var current = approvals.Create(request.Identity, request.StepNumber, request.Call.CallId,
                    request.CheckpointRevision + 1, tool, registry.GetPluginVersion(tool), arguments, root, resource, business);
                if (approved.Binding.Identity != request.Identity || approved.Binding.StepNumber != request.StepNumber
                    || approved.Binding.CallId != request.Call.CallId || approved.Tool != current.Tool)
                    return ToolResultNormalizer.Failure("TOOL_APPROVAL_STALE");
                try
                {
                    approvals.Validate(approved, new(approved.Binding.Identity.RunId, approved.Binding.RequestId,
                        approved.Binding.WaitingRevision, ApprovalDecision.Allow), request.Identity.Scope);
                }
                catch (AgentStateException error)
                {
                    return ToolResultNormalizer.Failure(error.Code == "RUN_INTERACTION_EXPIRED"
                        ? "TOOL_APPROVAL_EXPIRED" : "TOOL_APPROVAL_STALE");
                }
                return null;
            }
            var approvalDenied = Revalidate();
            if (approvalDenied is not null) return ValueTask.FromResult(approvalDenied);
            return executor.ExecuteAsync(request, tool, arguments, intent, token, commandApproved: true, approvalGuard: Revalidate,
                resourceAuthorization: resource, businessConfirmation: business);
        }
        if (decision == ToolPolicyDecision.Ask && approvals is not null)
        {
            ToolBusinessConfirmation? business = null;
            if (tool.Provider is IToolBusinessConfirmationProvider owner)
            {
                try { business = owner.PrepareConfirmation(ToolExecutor.CreateInvocation(request, arguments, resource), token); }
                catch (ToolBusinessConfirmationException) { return ValueTask.FromResult(ToolResultNormalizer.Failure("TOOL_BUSINESS_CONFIRMATION_UNAVAILABLE")); }
            }
            var approval = approvals.Create(request.Identity, request.StepNumber, request.Call.CallId,
                request.CheckpointRevision + 1, tool, registry.GetPluginVersion(tool), arguments, root, resource, business);
            var binding = approval.Binding;
            return ValueTask.FromResult(new ToolExecutionOutcome(ToolExecutionOutcomeKind.WaitingApproval,
                Waiting: new(binding.RequestId, AgentWaitKind.Approval, binding.StepNumber, binding.CallId,
                    binding.WaitingRevision, binding.ExpiresAt)) { ApprovalRequest = approval });
        }
        // A policy's Allow alone cannot manufacture evidence of a human-approved command.
        return executor.ExecuteAsync(request, tool, arguments, intent, token, approvalGuard: CheckResource, resourceAuthorization: resource);
    }
}
