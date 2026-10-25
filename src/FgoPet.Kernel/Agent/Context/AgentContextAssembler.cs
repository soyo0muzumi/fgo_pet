using System.Collections.Immutable;
using System.Text;
using System.Text.RegularExpressions;
using FgoPet.App.Dialogue;
using FgoPet.Extensibility;

namespace FgoPet.Kernel.Agent;

/// <summary>Fresh, provenance-bearing provider data within the caller's complete request budget.</summary>
public sealed class AgentContextAssembler(PluginCatalog catalog, PluginRuntime runtime)
{
    private const int MaxProviders = 64;
    private const int MaxBlocksPerProvider = 16;
    private const int MaxBlockBytes = 32 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly Regex ResourceId = new("\\A[A-Za-z0-9][A-Za-z0-9_-]*(?:\\.[A-Za-z0-9][A-Za-z0-9_-]*)*\\z", RegexOptions.CultureInvariant);
    private readonly PluginCatalog _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    private readonly PluginRuntime _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));

    public ValueTask<ImmutableArray<ModelMessage>> AssembleAsync(AgentContextRequest request,
        ImmutableArray<ModelMessage> baseMessages, Func<ImmutableArray<ModelMessage>, int> measureInputTokens,
        CancellationToken token, Action<ContextBudgetDiagnostic>? diagnostics = null) =>
        AssembleCoreAsync(request, baseMessages, measureInputTokens, false, token, diagnostics);

    // The Native host compacts the complete request next, then enforces its input budget.
    // Keep fresh context intact rather than rejecting/truncating it before that opportunity.
    internal ValueTask<ImmutableArray<ModelMessage>> AssembleForCompactionAsync(AgentContextRequest request,
        ImmutableArray<ModelMessage> baseMessages, Func<ImmutableArray<ModelMessage>, int> measureInputTokens,
        CancellationToken token, Action<ContextBudgetDiagnostic>? diagnostics = null) =>
        AssembleCoreAsync(request, baseMessages, measureInputTokens, true, token, diagnostics);

    private async ValueTask<ImmutableArray<ModelMessage>> AssembleCoreAsync(AgentContextRequest request,
        ImmutableArray<ModelMessage> baseMessages, Func<ImmutableArray<ModelMessage>, int> measureInputTokens,
        bool deferInputBudget, CancellationToken token, Action<ContextBudgetDiagnostic>? diagnostics)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(measureInputTokens);
        token.ThrowIfCancellationRequested();
        ValidateRequest(request);
        ModelProtocol.ValidateTranscript(baseMessages);
        if (_catalog.AgentContexts.Length > MaxProviders) throw Reject(ContextRejectionPoint.ProviderCount);
        if (!deferInputBudget)
        {
            var initial = Measure(baseMessages);
            if (initial > request.InputTokenBudget) throw Reject(ContextRejectionPoint.InitialInputBudget, initial);
        }

        var blocks = new List<CollectedBlock>();
        foreach (var entry in _catalog.AgentContexts)
        {
            token.ThrowIfCancellationRequested();
            if (!_runtime.IsActive(entry.PluginId)) continue;
            try
            {
                EnsureCapturedIdentity(entry);
                var supplied = await entry.Provider.BuildAsync(request, token);
                token.ThrowIfCancellationRequested();
                if (!_runtime.IsActive(entry.PluginId)) throw new InvalidOperationException();
                EnsureCapturedIdentity(entry);
                if (supplied is null || supplied.Count > MaxBlocksPerProvider) throw new InvalidOperationException();
                var unique = new Dictionary<(string Source, AgentContextKind Kind), AgentContextBlock>();
                var conflict = false;
                foreach (var block in supplied)
                {
                    ValidateBlock(block, entry.ProvidesTrustedInstructions, request.InputTokenBudget);
                    var key = (block.Source, block.Kind);
                    if (unique.TryGetValue(key, out var previous)) { conflict |= previous != block; continue; }
                    unique.Add(key, block);
                }
                if (conflict) blocks.Add(Unavailable(entry, "CONTEXT_CONFLICT"));
                else blocks.AddRange(unique.Values.Select(block => new CollectedBlock(entry, block, false)));
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception)
            {
                token.ThrowIfCancellationRequested();
                blocks.Add(Unavailable(entry, "CONTEXT_UNAVAILABLE"));
            }
        }

        // A later awaited provider can revoke admission or alter an earlier provider's declaration.
        var currentBlocks = new List<CollectedBlock>();
        foreach (var group in blocks.GroupBy(block => block.Entry))
        {
            var entry = group.Key;
            var current = false;
            try { EnsureCapturedIdentity(entry); current = _runtime.IsActive(entry.PluginId); }
            catch (Exception) { token.ThrowIfCancellationRequested(); }
            if (current) currentBlocks.AddRange(group);
            else currentBlocks.Add(Unavailable(entry, "CONTEXT_UNAVAILABLE"));
        }
        var insertion = 0;
        while (insertion < baseMessages.Length && baseMessages[insertion].Role == ModelMessageRole.System) insertion++;
        var context = ImmutableArray.CreateBuilder<ModelMessage>();
        foreach (var item in currentBlocks.OrderByDescending(b => b.Block.Priority)
            .ThenBy(b => b.Entry.PluginId, StringComparer.Ordinal)
            .ThenBy(b => b.Entry.ProviderId, StringComparer.Ordinal)
            .ThenBy(b => b.Block.Source, StringComparer.Ordinal)
            .ThenBy(b => b.Block.Kind))
        {
            token.ThrowIfCancellationRequested();
            var block = item.Block;
            var source = $"{item.Entry.PluginId}:{item.Entry.ProviderId}:{block.Source}";
            var projected = block.Kind == AgentContextKind.Instruction
                ? "可信指导来源：" + source + "\n" + block.Content
                : PromptInjectionGuard.Wrap(source, block.Content);
            // Compaction cannot make an optional whole block fit when that block alone exceeds
            // the complete task input allowance. Keep mandatory instructions and tool schemas intact.
            if(deferInputBudget&&block.Kind==AgentContextKind.Data&&block.Whole&&!item.IsMarker&&
                Measure([new(ModelMessageRole.System,projected,[])])>request.InputTokenBudget)continue;
            if (!Fits(projected))
            {
                if (deferInputBudget) throw Reject(ContextRejectionPoint.DeferredBlockBytes,
                    blockBytes: Encoding.UTF8.GetByteCount(projected));
                if (block.Kind == AgentContextKind.Instruction || item.IsMarker)
                    throw Reject(ContextRejectionPoint.MandatoryBlock, blockBytes: Encoding.UTF8.GetByteCount(projected));
                if (block.Whole) continue;
                projected = TruncateData(source, block.Content, Fits,
                    () => Reject(ContextRejectionPoint.NoTruncatableData));
            }
            context.Add(new(ModelMessageRole.System, projected, []));
        }
        token.ThrowIfCancellationRequested();
        var result = baseMessages.InsertRange(insertion, context);
        ModelProtocol.ValidateTranscript(result);
        if (!deferInputBudget)
        {
            var final = Measure(result);
            if (final > request.InputTokenBudget) throw Reject(ContextRejectionPoint.FinalAssemblyInputBudget, final);
        }
        if (deferInputBudget) StepEnvironmentBuilder.CheckSize(result);
        return result;

        AgentProtocolException Reject(ContextRejectionPoint point, int? input = null, int? blockBytes = null)
        {
            ContextBudgetDiagnostics.Emit(diagnostics, new(ContextDiagnosticStage.Rejected) {
                Point = point, InputBudget = request.InputTokenBudget, MeasuredInput = input,
                BlockUtf8Bytes = blockBytes, MessageCount = baseMessages.Length,
                ToolCount = request.AvailableTools.Length, Step = request.StepNumber
            });
            return TooLarge();
        }

        int Measure(ImmutableArray<ModelMessage> messages)
        {
            token.ThrowIfCancellationRequested();
            var measured = measureInputTokens(messages);
            if (measured < 0) throw new AgentProtocolException("RUN_CONTEXT_INVALID_MEASUREMENT");
            return measured;
        }
        bool Fits(string text) => Encoding.UTF8.GetByteCount(text) <= ModelProtocol.MaxContentBytes
            && (deferInputBudget || Measure(baseMessages.InsertRange(insertion, context.ToImmutable().Add(new(ModelMessageRole.System, text, []))))
                <= request.InputTokenBudget);
    }

    private static string TruncateData(string source, string content, Func<string, bool> fits,
        Func<AgentProtocolException> rejected)
    {
        var scalarEnds = new List<int> { 0 };
        for (var index = 0; index < content.Length;)
        {
            index += char.IsHighSurrogate(content[index]) ? 2 : 1;
            scalarEnds.Add(index);
        }
        var low = 0;
        var high = scalarEnds.Count - 1;
        string? best = null;
        while (low <= high)
        {
            var middle = low + (high - low) / 2;
            var candidate = PromptInjectionGuard.Wrap(source, content[..scalarEnds[middle]] + "\n[CONTEXT_TRUNCATED]");
            if (fits(candidate)) { best = candidate; low = middle + 1; }
            else high = middle - 1;
        }
        return best ?? throw rejected();
    }

    private static CollectedBlock Unavailable(RegisteredAgentContext entry, string code) =>
        new(entry, new("unavailable", AgentContextKind.Data, code, 1000), true);

    private static void EnsureCapturedIdentity(RegisteredAgentContext entry)
    {
        if (entry.Provider.Id != entry.ProviderId || entry.Provider.ProvidesTrustedInstructions != entry.ProvidesTrustedInstructions)
            throw new InvalidOperationException();
    }

    private static void ValidateBlock(AgentContextBlock? block, bool trusted, int budget)
    {
        if (block is null || block.Source is not { Length: > 0 and <= 256 } || !ResourceId.IsMatch(block.Source)
            || !Enum.IsDefined(block.Kind) || block.Kind == AgentContextKind.Instruction && !trusted
            || block.Content is null || StrictUtf8.GetByteCount(block.Content) > MaxBlockBytes
            || block.Priority is < -1000 or > 1000 || block.BudgetHint is int hint && (hint <= 0 || hint > budget))
            throw new InvalidOperationException();
    }

    private static void ValidateRequest(AgentContextRequest request)
    {
        var scope = request.Scope;
        if (scope is null || !ValidId(scope.ConversationId) || !ValidId(scope.RoleId)
            || scope.ProjectId is not null && !ValidId(scope.ProjectId) || !ValidId(request.RunId)
            || request.StepNumber <= 0 || request.Query is null || !ValidId(request.Query.UserMessageId)
            || !ValidId(request.Query.Fingerprint) || request.InputTokenBudget <= 0 || request.AvailableTools.IsDefault)
            throw new AgentProtocolException("RUN_CONTEXT_INVALID_REQUEST");
    }

    private static bool ValidId(string? value) => value is { Length: > 0 and <= 128 }
        && !value.Any(char.IsControl) && !value.Any(char.IsWhiteSpace);
    private static AgentProtocolException TooLarge() => new("RUN_CONTEXT_TOO_LARGE");
    private sealed record CollectedBlock(RegisteredAgentContext Entry, AgentContextBlock Block, bool IsMarker);
}
