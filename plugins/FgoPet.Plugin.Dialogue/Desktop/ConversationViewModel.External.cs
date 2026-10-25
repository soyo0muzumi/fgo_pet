using System.Collections.Immutable;
using System.Text;
using FgoPet.Core.Dialogue;
using FgoPet.Dialogue.Contracts;
using FgoPet.Kernel.Agent;

namespace FgoPet.App.Dialogue;

public sealed partial class ConversationViewModel
{
    private ExternalInputOwnership? _externalInputOwnership;
    private ExternalApprovalOwnership? _externalApprovalOwnership;

    internal ExternalConversationBinding? CaptureExternalBinding()
    {
        if (_disposed || string.IsNullOrWhiteSpace(ActiveServantId) || string.IsNullOrWhiteSpace(CurrentConversationId)) return null;
        var binding = new ExternalConversationBinding(ActiveServantId, CurrentConversationId, CurrentInputScope().ProjectId);
        return MatchesExternalBinding(binding) ? binding : null;
    }

    internal async Task<ExternalConversationResult> SendExternalAsync(ExternalConversationBinding binding, string text,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ExternalAdmission(binding) is { } denied) return denied;
        if (HasPendingRun) return ExternalFailure(ExternalConversationStatus.Busy, "CHAT_EXTERNAL_WAITING_INPUT");
        if (_configurationRequired) return ExternalFailure(ExternalConversationStatus.Unavailable, "CHAT_EXTERNAL_CONFIGURATION_REQUIRED");
        if (!ValidExternalText(text)) return ExternalFailure(ExternalConversationStatus.InvalidInput, "CHAT_EXTERNAL_INVALID_TEXT");

        var generation = _sessionGeneration;
        ClearExternalOwnership();
        try
        {
            var result = await SendCoreResultAsync(text.Trim(), cancellationToken);
            if (!ExternalOperationCurrent(binding, generation)) return ExternalScopeChanged();
            if (cancellationToken.IsCancellationRequested)
            {
                return await CancelExternalResultWaitAsync(result?.RunId, binding, generation)
                    ? ExternalCancelled() : ExternalFailure(ExternalConversationStatus.Failed, "CHAT_EXTERNAL_CANCEL_NOT_CONFIRMED");
            }
            return ProjectExternalResult(result, binding, generation);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ExternalCancelled();
        }
        catch (Exception)
        {
            return ExternalOperationCurrent(binding, generation)
                ? ExternalFailure(ExternalConversationStatus.Failed, "CHAT_EXTERNAL_REQUEST_FAILED") : ExternalScopeChanged();
        }
    }

    internal async Task<ExternalConversationResult> AnswerExternalAsync(ExternalConversationBinding binding, ExternalInputReply reply,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ExternalAdmission(binding) is { } denied) return denied;
        if (reply?.Reference is null || !OwnsExternalInput(binding, reply.Reference))
            return ExternalFailure(ExternalConversationStatus.InvalidInput, "CHAT_EXTERNAL_INPUT_STALE");
        if (reply.Answers is null || reply.Answers.Count is < 1 or > 3 || reply.Answers.Any(answer =>
            answer is null || answer.QuestionId is null || answer.SelectedOptionIndices is null ||
            answer.SelectedOptionIndices.Count > 8 || answer.Text is { Length: > 12000 }))
            return ExternalFailure(ExternalConversationStatus.InvalidInput, "CHAT_EXTERNAL_INVALID_ANSWER");
        var input = new UserInputReply(reply.Reference.RunId, reply.Reference.RequestId, reply.Reference.Revision,
            reply.Answers.Select(answer => new QuestionAnswer(answer.QuestionId,
                answer.SelectedOptionIndices.ToImmutableArray(), answer.Text)).ToImmutableArray());
        if (!PrepareInputReply(input, out var code))
            return ExternalFailure(ExternalConversationStatus.InvalidInput, code switch
            {
                "CHAT_INPUT_INVALID" => "CHAT_EXTERNAL_INVALID_ANSWER",
                "CHAT_INPUT_EXPIRED" => "CHAT_EXTERNAL_INPUT_EXPIRED",
                _ => "CHAT_EXTERNAL_INPUT_STALE",
            });
        var generation = _sessionGeneration;
        try
        {
            var result = await ResumeInputReplyAsync(input, generation, _activeRequestId, cancellationToken);
            if (!ExternalOperationCurrent(binding, generation)) return ExternalScopeChanged();
            if (cancellationToken.IsCancellationRequested)
            {
                return await CancelExternalResultWaitAsync(result?.RunId ?? reply.Reference.RunId, binding, generation)
                    ? ExternalCancelled() : ExternalFailure(ExternalConversationStatus.Failed, "CHAT_EXTERNAL_CANCEL_NOT_CONFIRMED");
            }
            return ProjectExternalResult(result, binding, generation);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ExternalCancelled();
        }
        catch (Exception)
        {
            return ExternalOperationCurrent(binding, generation)
                ? ExternalFailure(ExternalConversationStatus.Failed, "CHAT_EXTERNAL_REQUEST_FAILED") : ExternalScopeChanged();
        }
    }

    internal async Task<ExternalConversationResult> CancelExternalAsync(ExternalConversationBinding binding,
        ExternalInputReference reference, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ExternalAdmission(binding) is { } denied) return denied;
        if (reference is null || !OwnsExternalInput(binding, reference) ||
            !MatchInput(reference.RunId, reference.RequestId, reference.Revision, out _, allowExpired: true))
            return ExternalFailure(ExternalConversationStatus.InvalidInput, "CHAT_EXTERNAL_INPUT_STALE");
        var generation = _sessionGeneration;
        var scope = CurrentInputScope();
        IsInputSubmitting = true;
        CancelInputExpiry();
        NotifyInputWait();
        try
        {
            var cancelled = await _orchestrator.CancelAgentRunAsync(reference.RunId, scope, cancellationToken);
            if (!ExternalOperationCurrent(binding, generation)) return ExternalScopeChanged();
            if (!cancelled) return ExternalFailure(ExternalConversationStatus.Failed, "CHAT_EXTERNAL_CANCEL_NOT_CONFIRMED");
            ClearInputWait();
            _activeRequestId = null;
            _acceptRequestUpdates = false;
            RequestStatusText = "已取消。";
            ClearExternalOwnership();
            return new(ExternalConversationStatus.Cancelled, Text: "已取消这次提问。");
        }
        catch (Exception)
        {
            return ExternalOperationCurrent(binding, generation)
                ? ExternalFailure(ExternalConversationStatus.Failed, "CHAT_EXTERNAL_CANCEL_NOT_CONFIRMED") : ExternalScopeChanged();
        }
        finally
        {
            if (ExternalOperationCurrent(binding, generation) && HasPendingRun)
            {
                IsInputSubmitting = false;
                StartInputExpiry();
                NotifyInputWait();
            }
        }
    }

    internal Task<ExternalConversationResult> ExchangeExternalTextAsync(ExternalConversationBinding binding, string text,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ExternalAdmission(binding) is { } denied) return Task.FromResult(denied);
        if (!ValidExternalText(text))
            return Task.FromResult(ExternalFailure(ExternalConversationStatus.InvalidInput, "CHAT_EXTERNAL_INVALID_TEXT"));
        // A desktop action may have ended the old remote question. This is a new text message;
        // explicit Answer/Cancel calls still require the exact live input reference below.
        if (_externalInputOwnership is { } stale && !OwnsExternalInput(binding, stale.Reference))
            ClearExternalOwnership();
        if (_externalInputOwnership is { } ownership)
        {
            var normalized = text.Trim();
            if (normalized is "/取消" or "取消" || normalized.Equals("/cancel", StringComparison.OrdinalIgnoreCase))
                return CancelExternalAsync(binding, ownership.Reference, cancellationToken);
            var pending = ProjectExternalInput(PendingInput!);
            if (pending.Questions.Count != 1)
                return Task.FromResult(new ExternalConversationResult(ExternalConversationStatus.WaitingInput,
                    Text: "这次需要回答多个问题，请在桌面聊天窗口完成；也可以回复 /取消 结束这次提问。",
                    Input: pending, ErrorCode: "CHAT_EXTERNAL_DESKTOP_INPUT_REQUIRED"));
            var question = pending.Questions[0];
            IReadOnlyList<int> selected = Array.Empty<int>();
            string? answerText = normalized;
            if (question.Options.Count > 0 && IsNumericSelection(normalized))
            {
                var parts = normalized.Split(new char[] { ',', '，', '、', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                var numbers = new List<int>();
                foreach (var part in parts)
                {
                    if (!int.TryParse(part, out var number) || number < 1 || number > question.Options.Count || numbers.Contains(number - 1))
                        return Task.FromResult(InvalidSelection(pending));
                    numbers.Add(number - 1);
                }
                if (numbers.Count == 0 || !question.AllowMultiple && numbers.Count != 1)
                    return Task.FromResult(InvalidSelection(pending));
                selected = numbers;
                answerText = null;
            }
            return AnswerExternalAsync(binding, new(ownership.Reference,
                [new(question.Id, selected, answerText)]), cancellationToken);
        }
        if (_externalApprovalOwnership is { } approval && approval.Binding == binding &&
            approval.Generation == _sessionGeneration && _waitingRunId == approval.RunId && HasPendingRun)
            return Task.FromResult(ExternalApprovalRequired());
        return SendExternalAsync(binding, text, cancellationToken);
    }

    private ExternalConversationResult? ExternalAdmission(ExternalConversationBinding binding)
    {
        if (_disposed) return ExternalFailure(ExternalConversationStatus.Unavailable, "CHAT_EXTERNAL_CLOSED");
        if (!MatchesExternalBinding(binding)) return ExternalScopeChanged();
        if (IsStreaming || IsInputSubmitting) return ExternalFailure(ExternalConversationStatus.Busy, "CHAT_EXTERNAL_BUSY");
        return null;
    }

    private bool MatchesExternalBinding(ExternalConversationBinding? binding)
    {
        if (_disposed || binding is null || string.IsNullOrWhiteSpace(binding.RoleId) || string.IsNullOrWhiteSpace(binding.ConversationId)
            || binding.RoleId != ActiveServantId || binding.ConversationId != CurrentConversationId ||
            binding.ProjectId != CurrentInputScope().ProjectId) return false;
        try
        {
            var conversation = _orchestrator.GetConversation(binding.ConversationId, binding.RoleId);
            return conversation is { IsArchived: false } && conversation.ProjectId == binding.ProjectId;
        }
        catch (Exception) { return false; }
    }

    private bool ExternalOperationCurrent(ExternalConversationBinding binding, long generation) =>
        generation == _sessionGeneration && MatchesExternalBinding(binding);

    private bool OwnsExternalInput(ExternalConversationBinding binding, ExternalInputReference reference) =>
        _externalInputOwnership is { } owner && owner.Binding == binding && owner.Reference == reference &&
        owner.Generation == _sessionGeneration && PendingInput is { } input && _waitingRunId == reference.RunId &&
        input.Binding.Identity.RunId == reference.RunId && input.Binding.RequestId == reference.RequestId &&
        input.Binding.WaitingRevision == reference.Revision;

    private ExternalConversationResult ProjectExternalResult(ConversationSendResult? result,
        ExternalConversationBinding binding, long generation)
    {
        if (result is null || !ExternalOperationCurrent(binding, generation))
            return ExternalScopeChanged();
        // Content/provider admission can fail before the orchestrator has selected a conversation.
        // Such failures carry no text, but must not be confused with a successfully selected different scope.
        if (result.ConversationId != binding.ConversationId)
            return string.IsNullOrEmpty(result.ConversationId) ? result.Status switch
            {
                ConversationSendStatus.ConfigurationRequired => ExternalFailure(ExternalConversationStatus.Unavailable, "CHAT_EXTERNAL_CONFIGURATION_REQUIRED"),
                ConversationSendStatus.Cancelled => ExternalCancelled(),
                ConversationSendStatus.Failed => ExternalFailure(ExternalConversationStatus.Failed, "CHAT_EXTERNAL_REQUEST_FAILED"),
                _ => ExternalScopeChanged(),
            } : ExternalScopeChanged();
        if (result.Status == ConversationSendStatus.WaitingUserInput && PendingInput is { } input &&
            result.RunId == input.Binding.Identity.RunId)
        {
            var pending = ProjectExternalInput(input);
            _externalInputOwnership = new(binding, generation, pending.Reference);
            _externalApprovalOwnership = null;
            return new(ExternalConversationStatus.WaitingInput, Text: FormatExternalQuestion(pending), Input: pending,
                ErrorCode: pending.Questions.Count == 1 ? null : "CHAT_EXTERNAL_DESKTOP_INPUT_REQUIRED");
        }
        if (result.Status == ConversationSendStatus.WaitingApproval && result.RunId is { } runId)
        {
            _externalInputOwnership = null;
            _externalApprovalOwnership = new(binding, generation, runId);
            return ExternalApprovalRequired();
        }
        // A failed resumption can leave a still-valid wait. Retain its ownership so it can be cancelled or retried.
        if (!HasPendingRun) ClearExternalOwnership();
        if (result.Status == ConversationSendStatus.Completed && result.AssistantMessageId is { } messageId)
        {
            var final = _orchestrator.ListConversationMessages(binding.ConversationId, binding.RoleId)
                .SingleOrDefault(message => message.MessageId == messageId && message.Role == ChatMessageRole.Assistant &&
                    message.Status == ChatMessageStatus.Completed);
            return final is null ? ExternalFailure(ExternalConversationStatus.Failed, "CHAT_EXTERNAL_FINAL_UNAVAILABLE")
                : new(ExternalConversationStatus.Completed, Text: final.Text, MessageId: final.MessageId);
        }
        return result.Status switch
        {
            ConversationSendStatus.Cancelled => ExternalCancelled(),
            ConversationSendStatus.ConfigurationRequired => ExternalFailure(ExternalConversationStatus.Unavailable, "CHAT_EXTERNAL_CONFIGURATION_REQUIRED"),
            _ => ExternalFailure(ExternalConversationStatus.Failed, "CHAT_EXTERNAL_REQUEST_FAILED"),
        };
    }

    private async Task<bool> CancelExternalResultWaitAsync(string? runId, ExternalConversationBinding binding, long generation)
    {
        if (!ExternalOperationCurrent(binding, generation) || runId is null || _waitingRunId != runId) return true;
        // This is the exact run created/resumed by the cancelled call. Never use CancelCurrent, which affects other surfaces.
        IsInputSubmitting = true;
        CancelInputExpiry();
        NotifyInputWait();
        try
        {
            var cancelled = await _orchestrator.CancelAgentRunAsync(runId, CurrentInputScope(), CancellationToken.None);
            if (!ExternalOperationCurrent(binding, generation) || _waitingRunId != runId) return true;
            if (!cancelled) return false;
            ClearInputWait();
            _activeRequestId = null;
            _acceptRequestUpdates = false;
            ClearExternalOwnership();
            return true;
        }
        catch (Exception) { return false; }
        finally
        {
            if (ExternalOperationCurrent(binding, generation) && _waitingRunId == runId)
            {
                IsInputSubmitting = false;
                StartInputExpiry();
                NotifyInputWait();
            }
        }
    }

    private static ExternalConversationInput ProjectExternalInput(UserInputRequest input) => new(
        input.Binding.Identity.RunId, input.Binding.RequestId, input.Binding.WaitingRevision, input.Binding.ExpiresAt,
        input.Questions.Select(question => new ExternalInputQuestion(question.Id, question.Text,
            question.Options.ToArray(), question.AllowMultiple)).ToArray());

    private static string FormatExternalQuestion(ExternalConversationInput input)
    {
        if (input.Questions.Count != 1)
            return "这次需要回答多个问题，请在桌面聊天窗口完成；也可以回复 /取消 结束这次提问。";
        var question = input.Questions[0];
        var text = new StringBuilder(question.Text);
        for (var index = 0; index < question.Options.Count; index++) text.Append('\n').Append(index + 1).Append(". ").Append(question.Options[index]);
        return text.Append(question.Options.Count == 0
            ? "\n请直接回复答案；回复 /取消 可结束这次提问。"
            : question.AllowMultiple
                ? "\n回复选项编号（多个编号用逗号分隔），或直接回答；回复 /取消 可结束这次提问。"
                : "\n回复选项编号，或直接回答；回复 /取消 可结束这次提问。").ToString();
    }

    private static ExternalConversationResult InvalidSelection(ExternalConversationInput pending) => new(
        ExternalConversationStatus.InvalidInput, Text: "选项编号无效，请重新回答。\n" + FormatExternalQuestion(pending),
        Input: pending, ErrorCode: "CHAT_EXTERNAL_INVALID_ANSWER");
    private static ExternalConversationResult ExternalApprovalRequired() => new(ExternalConversationStatus.WaitingApproval,
        Text: "这次请求需要在桌面完成确认；请回到桌面聊天窗口处理或停止本次任务。", ErrorCode: "CHAT_EXTERNAL_LOCAL_APPROVAL_REQUIRED");
    private static ExternalConversationResult ExternalScopeChanged() => ExternalFailure(
        ExternalConversationStatus.ScopeChanged, "CHAT_EXTERNAL_SCOPE_CHANGED");
    private static ExternalConversationResult ExternalCancelled() => new(ExternalConversationStatus.Cancelled, Text: "已取消本次任务。");
    private static ExternalConversationResult ExternalFailure(ExternalConversationStatus status, string code) => new(status, ErrorCode: code);
    private void ClearExternalOwnership() { _externalInputOwnership = null; _externalApprovalOwnership = null; }
    private static bool IsNumericSelection(string text) => text.Any(char.IsAsciiDigit) &&
        text.All(character => char.IsAsciiDigit(character) || character is ',' or '，' or '、' or ' ' or '\t');
    private static bool ValidExternalText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 12000) return false;
        for (var index = 0; index < text.Length;)
        {
            if (!Rune.TryGetRuneAt(text, index, out var rune)) return false;
            index += rune.Utf16SequenceLength;
        }
        return true;
    }

    private sealed record ExternalInputOwnership(ExternalConversationBinding Binding, long Generation, ExternalInputReference Reference);
    private sealed record ExternalApprovalOwnership(ExternalConversationBinding Binding, long Generation, string RunId);
}
