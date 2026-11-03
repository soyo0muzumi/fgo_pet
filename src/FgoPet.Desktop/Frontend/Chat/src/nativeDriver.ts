import {
  emptyState,
  type ActionName,
  type Answer,
  type ChatDriver,
  type Phase,
  type UiState,
} from "./domain.ts";
import type {
  ChatIdentity,
  NativeBridge,
  PendingRequestIdentity,
  Snapshot,
} from "./hostTypes";
import { NativeBridgeError, sameIdentity } from "./nativeBridge.ts";

type Draft = {
  text: string;
  revision: number;
  dirty: boolean;
  submittedText?: string;
};
type SendTransaction = {
  identity: ChatIdentity;
  revision: number;
  text: string;
  key: string;
};
const identityKey = (id: ChatIdentity) =>
  JSON.stringify([id.sessionId, id.servantId, id.conversationId]);
const requestKey = (value: PendingRequestIdentity) =>
  JSON.stringify([value.runId, value.requestId, value.revision]);
function fail(code: string): never {
  throw new NativeBridgeError(code);
}
const errorLabels: Record<string, string> = {
  CHAT_TIMEOUT: "桌面宿主暂时没有响应，请重试。操作可能仍在进行。",
  CHAT_BRIDGE_UNAVAILABLE:
    "无法连接桌面宿主。请检查 WebView2 页面是否正常加载。",
  CHAT_SNAPSHOT_INVALID: "桌面返回的界面数据不完整，请重试连接。",
  CHAT_IDENTITY_STALE: "对话已切换，请在当前对话中重试。",
  CHAT_TARGET_INVALID: "所选内容已变化，请刷新后重试。",
  CHAT_NOT_AVAILABLE: "当前状态下暂时不能执行此操作。",
  CHAT_DRAFT_TOO_LONG: "消息不能超过 12,000 个字符。",
  CHAT_DRAFT_STALE: "草稿版本已更新。保留了您的输入，请重试发送。",
  CHAT_DRAFT_NOT_ACCEPTED: "草稿还没有得到桌面确认，未发送。请重试。",
  CHAT_SEND_BUSY: "消息正在提交，请稍候。",
  CHAT_IMAGE_BUSY: "图片正在导入，请稍候。",
  CHAT_IMAGE_INVALID: "请选择一张不超过 8 MiB 的 PNG 或 JPEG 图片。",
  CHAT_IMAGE_TRANSFER_INVALID: "图片传输未完成，请重新选择图片。",
  CHAT_IMAGE_CANCELED: "已取消图片导入。",
  CHAT_INPUT_STALE: "这张提问卡片已经变化，请使用最新卡片。",
  CHAT_INPUT_EXPIRED: "这次提问已过期。",
  CHAT_INPUT_INVALID: "请回答每个问题，答案合计不能超过 12,000 个字符。",
  CHAT_APPROVAL_STALE: "待确认的操作已变化，请重新查看。",
  CHAT_APPROVAL_BUSY: "此操作正在原生窗口中确认。",
  CHAT_APPROVAL_EXPIRED: "这次确认已过期。",
  CHAT_NATIVE_ACTION_UNAVAILABLE: "这项操作需要使用桌面已有的任务或专注入口。",
  CHAT_COMMAND_INVALID: "操作参数不符合桌面接口，请重试。",
  CHAT_BRIDGE_DISPOSED: "桌面连接已关闭。",
};
function errorText(error: unknown): string {
  const code =
    error instanceof NativeBridgeError
      ? error.code
      : error instanceof Error
        ? error.message
        : "";
  return errorLabels[code] ?? "操作未完成。您的输入已保留，请重试。";
}
function notExpired(expiresAt: string): boolean {
  const time = Date.parse(expiresAt);
  return Number.isFinite(time) && time > Date.now();
}

/** The host supplies colors, not a theme-mode field. Infer only the background
 * scheme for ancillary UI; CSS still uses the exact host color variables. */
function backgroundTheme(value: unknown): "light" | "dark" | undefined {
  if (typeof value !== "string") return undefined;
  let hex = value.trim().replace(/^#/, "");
  if (/^[\da-f]{3}$/i.test(hex))
    hex = [...hex].map((char) => char + char).join("");
  if (!/^[\da-f]{6}$/i.test(hex)) return undefined;
  const channels = [0, 2, 4]
    .map((offset) => parseInt(hex.slice(offset, offset + 2), 16) / 255)
    .map((channel) =>
      channel <= 0.04045 ? channel / 12.92 : ((channel + 0.055) / 1.055) ** 2.4,
    );
  const luminance =
    0.2126 * channels[0] + 0.7152 * channels[1] + 0.0722 * channels[2];
  return luminance < 0.179 ? "dark" : "light";
}

/** Real C# state only. UI-local data is limited to unsaved input and request feedback.
 * No localStorage, scripted replies, browser clipboard fallback or local tasks. */
export function createNativeDriver(bridge: NativeBridge): ChatDriver {
  let state = emptyState();
  let owner: Snapshot | undefined;
  let connected = false,
    disposed = false,
    connecting: Promise<void> | undefined;
  let localRevision = 0,
    uiRevision = 0;
  let connectionError: string | undefined, commandError: string | undefined;
  let compatibilityWarning: string | undefined;
  let nativeTheme: "light" | "dark" | undefined;
  let themeVersion = -1;
  let visible = true,
    sending = false,
    stopping = false;
  let requestBusy: string | undefined;
  let imagePending: string | undefined;
  let imageAbort: AbortController | undefined;
  let sendTransaction: SendTransaction | undefined;
  let sendTask: Promise<void> | undefined;
  let draftTimer: ReturnType<typeof setTimeout> | undefined;
  let expiryTimer: ReturnType<typeof setTimeout> | undefined;
  let draftQueue: Promise<void> = Promise.resolve();
  let presentationQueue: Promise<void> = Promise.resolve();
  let presentationSerial = 0;
  const drafts = new Map<string, Draft>();
  const listeners = new Set<() => void>();

  function emit() {
    if (disposed) return;
    state = project();
    for (const listener of listeners) {
      try {
        listener();
      } catch {
        console.warn("Native driver subscriber failed.");
      }
    }
  }
  function requireOwner(): Snapshot {
    if (!owner || !connected || disposed) fail("CHAT_BRIDGE_UNAVAILABLE");
    return owner;
  }
  function currentDraft(): Draft | undefined {
    return owner ? drafts.get(identityKey(owner.identity)) : undefined;
  }
  function phaseFor(snapshot: Snapshot): Phase {
    const c = snapshot.conversation;
    if (stopping) return "stopping";
    if (sending) return "submitting";
    if (c.pendingApproval) return "approval";
    if (c.pendingInput) return "input";
    // The wire contract has no tool-progress percentage or separate tool phase.
    if (c.turns.some((turn) => turn.isStreaming && turn.text.length > 0))
      return "streaming";
    if (c.isThinking || c.isStreaming || c.hasPendingRun) return "waiting";
    if (commandError || c.errorText) return "error";
    return "idle";
  }
  function project(): UiState {
    if (!owner)
      return {
        ...emptyState(),
        revision: ++uiRevision,
        connectionError,
        nativeTheme,
        status: connectionError ? "桌面连接不可用" : "正在连接桌面…",
      };
    const c = owner.conversation,
      p = owner.presentation,
      h = owner.history,
      a = p.actions;
    const draft = currentDraft()?.text ?? owner.draft.text;
    const phase = phaseFor(owner);
    const input = c.pendingInput,
      approval = c.pendingApproval;
    const approvalBusy =
      !!approval &&
      (approval.isSubmitting || requestBusy === requestKey(approval));
    const inputBusy =
      !!input && (input.isSubmitting || requestBusy === requestKey(input));
    const approvalLive = !!approval && notExpired(approval.expiresAt);
    const inputLive = !!input && notExpired(input.expiresAt);
    const idle = !c.isStreaming && !c.hasPendingRun;
    const available: Partial<Record<ActionName, boolean>> = {
      model: idle && p.models.some((item) => item.canSelect),
      project: !c.isStreaming && p.projects.some((item) => item.canSelect),
      chooseWorkspace: a?.canChooseWorkspace === true && idle,
      clearWorkspace: a?.canClearWorkspace === true && idle,
      removeContextChip: c.contextChips.length > 0 && idle,
      chooseImage: a?.canChooseImage === true && idle && !imagePending,
      clearImage: a?.canClearImage === true && idle && !imagePending,
      cancelImage: !!imagePending,
      configureBrowserImages: a?.canConfigureBrowserImages === true,
      openWorkspaceOverview: a?.canOpenWorkspaceOverview === true,
      newWorkspaceItem: a?.canCreateWorkspaceItem === true,
      openFocus: a?.canOpenFocus === true,
      openPersonalizationSettings: a?.canOpenPersonalizationSettings === true,
      openSpeechSettings: a?.canOpenSpeechSettings === true,
      openModelSettings:
        a?.canOpenModelSettings === true &&
        (c.canOpenModelSettings || c.configurationRequired),
      refreshModels: idle,
      refreshProjects:
        a?.canRefreshProjects === true &&
        !p.isProjectsLoading &&
        !c.isStreaming,
      historyLoad: !h.isLoading,
      historyOpen: h.items.length > 0,
      historyRequestDelete: h.canDelete,
      historyConfirmDelete: h.canDelete && !!h.pendingDeleteConversationId,
      historyCancelDelete: !!h.pendingDeleteConversationId,
      copy: c.turns.some((turn) => turn.canCopy),
      readAloud: c.turns.some((turn) => turn.canReadAloud),
      openWorkspace: c.turns.some((turn) => turn.canOpenWorkspace),
      openSource: c.recalledSources.length > 0,
      retryCapability: c.canRetryCapability,
      approvalReview: approvalLive && !approvalBusy,
      approvalDeny: approvalLive && !approvalBusy,
      approvalCancel: !!approval && !approvalBusy,
      inputAnswer: inputLive && !inputBusy,
      inputCancel: !!input && !inputBusy,
    };
    const localReady =
      draft.trim().length > 0 &&
      draft !== owner.draft.text &&
      !c.configurationRequired;
    const status = sending
      ? "正在提交…"
      : stopping
        ? "正在停止…"
        : approval
          ? "需要您确认"
          : input
            ? "等待您的回答"
            : c.requestStatusText ||
              (phase === "waiting"
                ? "正在等待回复…"
                : phase === "streaming"
                  ? "正在回复…"
                  : "在这里，陪着您。");
    return {
      revision: ++uiRevision,
      ready: connected,
      connectionError,
      ownerKey: JSON.stringify([owner.identity.sessionId, owner.identity.servantId]),
      conversationId: owner.identity.conversationId,
      roleName: p.roleName || "当前角色",
      avatarUrl: p.avatarPngDataUrl || undefined,
      draft,
      messages: c.turns.map((turn) => ({
        id: turn.id,
        role: turn.role === "user" ? "user" : "assistant",
        text: turn.text,
        streaming: turn.isStreaming,
        reasoning: c.showReasoning ? turn.reasoningText : undefined,
        reasoningSummary: c.showReasoning ? turn.reasoningSummary : undefined,
        reasoningDuration: c.showReasoning
          ? turn.reasoningDurationText
          : undefined,
        reasoningExpanded: turn.isReasoningExpanded,
        thinkingActive: turn.isThinkingActive,
        canCopy: turn.canCopy,
        canReadAloud: turn.canReadAloud,
        speechBusy: turn.speechBusy,
        speechStatus: turn.speechStatusText,
        speechAction: turn.speechActionText,
        speechNeedsConfiguration: turn.speechNeedsConfiguration,
        canOpenWorkspace: turn.canOpenWorkspace,
      })),
      phase,
      status,
      detail: c.isThinking ? c.thinkingTimerText || undefined : undefined,
      error: commandError || c.errorText || undefined,
      canSend:
        connected &&
        !sending &&
        !stopping &&
        !imagePending &&
        idle &&
        !c.configurationRequired &&
        draft.trim().length > 0 &&
        draft.length <= 12_000 &&
        (c.canSend || localReady),
      canStop: connected && c.canStop && !stopping,
      models: p.models.map((item) => ({
        id: item.id,
        label: item.label,
        selected: item.selected,
        disabled: !item.canSelect || !idle,
      })),
      projects: p.projects.map((item) => ({
        id: item.id,
        label: item.label,
        selected: item.selected,
        disabled: !item.canSelect || c.isStreaming,
      })),
      modelLabel:
        p.models.find((item) => item.selected)?.label ||
        c.modelStatusText ||
        "模型",
      projectLabel: c.project.label || "选择项目文件夹",
      contextLabel: p.workspaceLabel || undefined,
      contextChips: c.contextChips.map((chip) => ({
        ...chip,
        removable: idle,
      })),
      recalledSources: c.recalledSources,
      capabilityNotice: c.capabilityNotice?.text
        ? c.capabilityNotice
        : undefined,
      canRetryCapability: c.canRetryCapability,
      configurationRequired: c.configurationRequired,
      providerStatus: c.providerStatusText,
      projectsLoading: p.isProjectsLoading,
      projectsStatus: p.projectsStatus,
      image: imagePending
        ? { label: imagePending, pending: true, status: "正在导入图片…" }
        : p.imageLabel
          ? { label: p.imageLabel }
          : undefined,
      imageDisclosure: p.imageDisclosure || undefined,
      history: h.items.map((item) => ({
        id: item.conversationId,
        title: item.title,
        updated: item.updatedText,
        status: item.status,
        current: item.conversationId === owner!.identity.conversationId,
        pendingDelete: item.conversationId === h.pendingDeleteConversationId,
      })),
      historyLoading: h.isLoading,
      historyHasMore: h.hasMore,
      historyOnlyProject: h.onlyCurrentProject,
      historyCanDelete: h.canDelete,
      historyStatus: h.status,
      approval: approval
        ? {
            runId: approval.runId,
            requestId: approval.requestId,
            revision: approval.revision,
            title: approval.actionTitle || approval.toolName || "确认操作",
            description:
              approval.plainTextSummary || "请在原生确认窗口中查看完整操作。",
            detail: approval.plainTextDetails,
            detailExpanded: !approval.plainTextSummary?.trim(),
            expiresAt: approval.expiresAt,
            busy: approvalBusy,
            error:
              approval.errorText ||
              (!approvalLive ? "这次确认已过期。" : undefined),
            canReview: approvalLive && !approvalBusy,
            canDeny: approvalLive && !approvalBusy,
            canCancel: !approvalBusy,
          }
        : undefined,
      input: input
        ? {
            runId: input.runId,
            requestId: input.requestId,
            revision: input.revision,
            title: "需要补充一点信息",
            questions: input.questions.map((q, index) => ({
              id: q.id,
              label: "问题 " + (index + 1),
              prompt: q.text,
              options: q.options.map((label) => ({ label })),
              multiSelect: q.allowMultiple,
              allowText: true,
            })),
            expiresAt: input.expiresAt,
            busy: inputBusy,
            error:
              input.errorText || (!inputLive ? "这次提问已过期。" : undefined),
            canSubmit: inputLive && !inputBusy,
            canCancel: !inputBusy,
          }
        : undefined,
      tasks: [],
      focus: null,
      actions: {
        image: available.chooseImage === true,
        model: available.model === true,
        project: available.project === true,
        context:
          available.chooseWorkspace === true ||
          available.clearWorkspace === true ||
          c.contextChips.length > 0,
        workspace:
          available.openWorkspaceOverview === true ||
          available.newWorkspaceItem === true,
        settings:
          available.openPersonalizationSettings === true ||
          available.openSpeechSettings === true ||
          available.openModelSettings === true,
        newConversation: !c.isStreaming,
      },
      visibility: visible,
      nativeTheme,
      hostPresentationSupported: !!p.window,
      hostPresentation: p.window || undefined,
      compatibilityWarning:
        compatibilityWarning ||
        (!p.window
          ? "当前桌面尚未安装窗口呈现补丁，收起和展开会使用旧尺寸。"
          : undefined),
      availableActions: available,
    };
  }

  function syncSnapshot(next: Snapshot) {
    if (disposed) return;
    const previous = owner;
    if (
      previous?.identity.sessionId === next.identity.sessionId &&
      next.version < previous.version
    )
      return;
    const key = identityKey(next.identity);
    // First send admits a conversation ID in the same session. Only newer typing
    // from that admission may follow; history/role switches never inherit it.
    if (
      previous &&
      sendTransaction &&
      !previous.identity.conversationId &&
      next.identity.conversationId &&
      previous.identity.sessionId === next.identity.sessionId &&
      previous.identity.servantId === next.identity.servantId
    ) {
      const oldKey = identityKey(previous.identity);
      const draft = drafts.get(oldKey);
      if (draft) {
        drafts.set(key, draft);
        drafts.delete(oldKey);
        sendTransaction.key = key;
      }
    }
    owner = next;
    localRevision = Math.max(localRevision, next.draft.revision);
    const draft = drafts.get(key);
    const holdSending = sendTransaction?.key === key;
    const suppressSent =
      draft?.submittedText === next.draft.text &&
      next.draft.revision <= draft.revision;
    if (!draft || (!draft.dirty && !holdSending && !suppressSent)) {
      drafts.set(key, {
        text: next.draft.text,
        revision: next.draft.revision,
        dirty: false,
      });
    } else if (
      draft.text === next.draft.text &&
      draft.revision === next.draft.revision
    ) {
      draft.dirty = false;
    }
    if (previous && !sameIdentity(previous.identity, next.identity))
      commandError = undefined;
    if (expiryTimer) clearTimeout(expiryTimer);
    const expiries = [
      next.conversation.pendingApproval?.expiresAt,
      next.conversation.pendingInput?.expiresAt,
    ]
      .filter((time): time is string => !!time)
      .map((time) => Date.parse(time) - Date.now())
      .filter((time) => Number.isFinite(time) && time > 0);
    if (expiries.length)
      expiryTimer = setTimeout(
        emit,
        Math.min(2_147_483_647, Math.min(...expiries) + 10),
      );
    emit();
  }
  const offSnapshot = bridge.onSnapshot(syncSnapshot);
  const offEvents = bridge.subscribe((event) => {
    if (
      event.type === "chat.visibility" &&
      typeof event.visible === "boolean"
    ) {
      visible = event.visible;
      emit();
      if (visible)
        void bridge.get().catch((error) => {
          commandError = errorText(error);
          emit();
        });
      else if (!sending)
        void flushDraft().catch((error) => {
          commandError = errorText(error);
          emit();
        });
    } else if (
      event.type === "theme.changed" &&
      typeof event.version === "number" &&
      Number.isFinite(event.version) &&
      event.version >= themeVersion
    ) {
      themeVersion = event.version;
      const variables = event.variables;
      if (variables && typeof variables === "object") {
        const palette = variables as Record<string, unknown>;
        const scheme = backgroundTheme(
          palette["--paper"] ?? palette["--surface"] ?? palette["--page"],
        );
        if (scheme) nativeTheme = scheme;
      }
      emit();
    } else if (event.type === "bridge.error") {
      connectionError =
        errorLabels[String(event.errorCode)] || "桌面连接发生错误。";
      connected = false;
      emit();
    }
  });

  function cancelDraftTimer() {
    if (draftTimer) clearTimeout(draftTimer);
    draftTimer = undefined;
  }
  function queueDraft(identity: ChatIdentity, draft: Draft): Promise<void> {
    const key = identityKey(identity);
    const task = draftQueue
      .catch(() => {})
      .then(async () => {
        if (disposed || !owner || !sameIdentity(owner.identity, identity))
          return;
        const latest = drafts.get(key);
        if (
          !latest ||
          latest.revision !== draft.revision ||
          latest.text !== draft.text
        )
          return;
        if (
          owner.draft.text === draft.text &&
          owner.draft.revision === draft.revision
        ) {
          latest.dirty = false;
          return;
        }
        await bridge.execute(
          "chat.draft",
          { text: draft.text, revision: draft.revision },
          { expectedIdentity: identity },
        );
      });
    draftQueue = task;
    return task;
  }
  function scheduleDraft() {
    cancelDraftTimer();
    if (sending || !owner || !connected) return;
    const draft = currentDraft();
    if (!draft?.dirty || draft.text.length > 12_000) return;
    const identity = { ...owner.identity };
    draftTimer = setTimeout(() => {
      draftTimer = undefined;
      void queueDraft(identity, { ...draft }).catch((error) => {
        if (owner && sameIdentity(owner.identity, identity)) {
          commandError = errorText(error);
          emit();
        }
      });
    }, 140);
  }
  async function flushDraft() {
    cancelDraftTimer();
    if (sending) await sendTask?.catch(() => {});
    if (!owner) return;
    const identity = { ...owner.identity };
    // Typing can continue while an earlier write is awaiting its ACK. A
    // navigation may leave only after the latest revision is saved as well.
    for (;;) {
      await draftQueue.catch(() => {});
      if (!owner || !sameIdentity(owner.identity, identity))
        fail("CHAT_IDENTITY_STALE");
      const draft = currentDraft();
      if (!draft?.dirty) break;
      if (draft.text.length > 12_000) fail("CHAT_DRAFT_TOO_LONG");
      await queueDraft(identity, { ...draft });
    }
  }
  function setDraft(text: string) {
    if (!owner || !connected || disposed) return;
    const key = identityKey(owner.identity);
    const existing = drafts.get(key);
    if (existing?.text === text) return;
    localRevision = Math.max(localRevision, owner.draft.revision) + 1;
    drafts.set(key, { text, revision: localRevision, dirty: true });
    if (text.length > 12_000) commandError = errorLabels.CHAT_DRAFT_TOO_LONG;
    else if (commandError === errorLabels.CHAT_DRAFT_TOO_LONG)
      commandError = undefined;
    emit();
    scheduleDraft();
  }
  async function perform(work: (identity: ChatIdentity) => Promise<unknown>) {
    const identity = { ...requireOwner().identity };
    commandError = undefined;
    emit();
    try {
      await work(identity);
    } catch (error) {
      if (owner && sameIdentity(identity, owner.identity)) {
        commandError = errorText(error);
        emit();
      }
      throw error;
    }
    emit();
  }
  function target(payload: Record<string, unknown>): string {
    const id = payload.targetId ?? payload.id ?? payload.targetConversationId;
    return typeof id === "string" && id.length > 0
      ? id
      : fail("CHAT_TARGET_INVALID");
  }
  function pendingIdentity(
    kind: "approval" | "input",
    payload: Record<string, unknown>,
  ): PendingRequestIdentity {
    const value =
      kind === "approval"
        ? requireOwner().conversation.pendingApproval
        : requireOwner().conversation.pendingInput;
    if (
      !value ||
      payload.runId !== value.runId ||
      payload.requestId !== value.requestId ||
      payload.revision !== value.revision
    ) {
      fail(kind === "approval" ? "CHAT_APPROVAL_STALE" : "CHAT_INPUT_STALE");
    }
    return {
      runId: value.runId,
      requestId: value.requestId,
      revision: value.revision,
    };
  }
  function readAnswers(payload: Record<string, unknown>): Answer[] {
    const input = requireOwner().conversation.pendingInput;
    if (
      !input ||
      !Array.isArray(payload.answers) ||
      payload.answers.length !== input.questions.length
    )
      fail("CHAT_INPUT_INVALID");
    let total = 0;
    return input.questions.map((question) => {
      const answer = (payload.answers as Answer[]).find(
        (item) => item?.questionId === question.id,
      );
      if (
        !answer ||
        !Array.isArray(answer.selectedOptionIndices) ||
        answer.selectedOptionIndices.length > 8 ||
        (!question.allowMultiple && answer.selectedOptionIndices.length > 1)
      )
        fail("CHAT_INPUT_INVALID");
      const indices = answer.selectedOptionIndices;
      if (
        new Set(indices).size !== indices.length ||
        indices.some(
          (index) =>
            !Number.isInteger(index) ||
            index < 0 ||
            index >= question.options.length,
        )
      )
        fail("CHAT_INPUT_INVALID");
      if (answer.text !== null && typeof answer.text !== "string")
        fail("CHAT_INPUT_INVALID");
      const text = answer.text?.trim() ? answer.text : null;
      if (!indices.length && !text) fail("CHAT_INPUT_INVALID");
      total +=
        (text?.length || 0) +
        indices.reduce((sum, index) => sum + question.options[index].length, 0);
      if (total > 12_000) fail("CHAT_INPUT_INVALID");
      return {
        questionId: question.id,
        selectedOptionIndices: [...indices],
        text,
      };
    });
  }
  async function action(
    name: ActionName,
    payload: Record<string, unknown> = {},
  ) {
    return perform(async (identity) => {
      const s = requireOwner();
      const execute = (type: string, fields: Record<string, unknown> = {}) =>
        bridge.execute(type, fields, { expectedIdentity: identity });
      const host = (actionName: string, extras: Record<string, unknown> = {}) =>
        execute("chat.host", { action: actionName, ...extras });
      if (
        [
          "taskSave",
          "taskAdd",
          "taskToggle",
          "taskDelete",
          "focusStart",
          "focusToggle",
          "focusFinish",
        ].includes(name)
      )
        fail("CHAT_NATIVE_ACTION_UNAVAILABLE");
      if (state.availableActions?.[name] === false) fail("CHAT_NOT_AVAILABLE");
      switch (name) {
        case "model":
        case "project": {
          const id = target(payload);
          const choice = (
            name === "model" ? s.presentation.models : s.presentation.projects
          ).find((item) => item.id === id);
          if (!choice?.canSelect) fail("CHAT_TARGET_INVALID");
          await flushDraft();
          await host(name === "model" ? "selectModel" : "selectProject", {
            targetId: id,
          });
          break;
        }
        case "copy":
        case "readAloud":
        case "openWorkspace": {
          const id = target(payload),
            turn = s.conversation.turns.find((item) => item.id === id);
          if (
            !turn ||
            (name === "copy"
              ? !turn.canCopy
              : name === "readAloud"
                ? !turn.canReadAloud
                : !turn.canOpenWorkspace)
          )
            fail("CHAT_TARGET_INVALID");
          await host(
            name === "copy"
              ? "copyTurn"
              : name === "readAloud"
                ? "readTurn"
                : "openWorkspace",
            { targetId: id },
          );
          break;
        }
        case "openSource":
        case "removeContextChip": {
          const id = target(payload);
          if (
            !(
              name === "openSource"
                ? s.conversation.recalledSources
                : s.conversation.contextChips
            ).some((item) => item.id === id)
          )
            fail("CHAT_TARGET_INVALID");
          await host(name, { targetId: id });
          break;
        }
        case "historyLoad":
          await execute("chat.history", {
            onlyCurrentProject:
              typeof payload.onlyCurrentProject === "boolean"
                ? payload.onlyCurrentProject
                : s.history.onlyCurrentProject,
            append: payload.append === true,
          });
          break;
        case "historyOpen":
        case "historyRequestDelete":
        case "historyConfirmDelete":
          if (name === "historyOpen") await flushDraft();
          await execute(
            name === "historyOpen"
              ? "chat.history.open"
              : name === "historyRequestDelete"
                ? "chat.history.requestDelete"
                : "chat.history.confirmDelete",
            { targetConversationId: target(payload) },
          );
          break;
        case "historyCancelDelete":
          await execute("chat.history.cancelDelete");
          break;
        case "approvalReview":
        case "approvalDeny":
        case "approvalCancel":
        case "inputAnswer":
        case "inputCancel": {
          const approval = name.startsWith("approval");
          const binding = pendingIdentity(
            approval ? "approval" : "input",
            payload,
          );
          requestBusy = requestKey(binding);
          emit();
          try {
            const type =
              name === "approvalReview"
                ? "chat.approval.review"
                : name === "approvalDeny"
                  ? "chat.approval.deny"
                  : name === "approvalCancel"
                    ? "chat.approval.cancel"
                    : name === "inputAnswer"
                      ? "chat.input.answer"
                      : "chat.input.cancel";
            await execute(
              type,
              name === "inputAnswer"
                ? { ...binding, answers: readAnswers(payload) }
                : binding,
            );
          } finally {
            requestBusy = undefined;
            emit();
          }
          break;
        }
        case "retryCapability":
          await execute("chat.retryCapability");
          break;
        case "cancelImage":
          if (imageAbort) imageAbort.abort();
          else await execute("chat.image.cancel");
          break;
        default:
          await host(name);
          break;
      }
    });
  }

  const driver: ChatDriver = {
    kind: "native",
    getSnapshot: () => state,
    subscribe(listener) {
      listeners.add(listener);
      return () => {
        listeners.delete(listener);
      };
    },
    connect() {
      if (disposed)
        return Promise.reject(new NativeBridgeError("CHAT_BRIDGE_DISPOSED"));
      if (connecting) return connecting;
      connectionError = undefined;
      emit();
      connecting = bridge
        .ready()
        .then((snapshot) => {
          connected = true;
          connectionError = undefined;
          syncSnapshot(snapshot);
        })
        .catch((error) => {
          connected = false;
          connectionError = errorText(error);
          emit();
          throw error;
        })
        .finally(() => {
          connecting = undefined;
        });
      return connecting;
    },
    setDraft,
    send(text) {
      if (sending)
        return Promise.reject(new NativeBridgeError("CHAT_SEND_BUSY"));
      const invokedIdentity = owner ? { ...owner.identity } : undefined;
      sendTask = (async () => {
        requireOwner();
        setDraft(text);
        if (!text.trim()) fail("CHAT_INPUT_INVALID");
        if (text.length > 12_000) fail("CHAT_DRAFT_TOO_LONG");
        if (!state.canSend) fail("CHAT_NOT_AVAILABLE");
        const identity = { ...owner!.identity },
          key = identityKey(identity);
        const draft = drafts.get(key)!;
        sendTransaction = { identity, key, revision: draft.revision, text };
        const transaction = sendTransaction;
        sending = true;
        commandError = undefined;
        cancelDraftTimer();
        emit();
        try {
          await draftQueue.catch(() => {});
          // A history/role switch while an earlier draft was in flight must not
          // move this send to the newly active owner.
          if (!owner || !sameIdentity(identity, owner.identity))
            fail("CHAT_IDENTITY_STALE");
          await bridge.sendDraftThenSend(text, transaction.revision);
          const latest = drafts.get(transaction.key);
          if (
            latest?.revision === transaction.revision &&
            latest.text === text
          ) {
            // Clear only after both native draft and send acknowledgements.
            drafts.set(transaction.key, {
              text: "",
              revision: transaction.revision,
              dirty: false,
              submittedText: text,
            });
          }
        } catch (error) {
          if (
            owner &&
            (sameIdentity(identity, owner.identity) ||
              identityKey(owner.identity) === transaction.key)
          )
            commandError = errorText(error);
          throw error;
        } finally {
          sending = false;
          sendTransaction = undefined;
          emit();
          scheduleDraft();
        }
      })().catch((error) => {
        if (
          owner &&
          invokedIdentity &&
          sameIdentity(invokedIdentity, owner.identity)
        ) {
          commandError ||= errorText(error);
          emit();
        }
        throw error;
      });
      return sendTask;
    },
    async stop() {
      if (stopping) return;
      await perform(async () => {
        if (!requireOwner().conversation.canStop) fail("CHAT_NOT_AVAILABLE");
        stopping = true;
        emit();
        try {
          await bridge.stop();
        } finally {
          stopping = false;
          emit();
        }
      });
    },
    async newConversation() {
      await perform(async (identity) => {
        await flushDraft();
        await bridge.execute("chat.new", {}, { expectedIdentity: identity });
      });
    },
    action,
    async importImage(file) {
      await perform(async () => {
        if (imagePending || state.availableActions?.chooseImage !== true)
          fail("CHAT_NOT_AVAILABLE");
        imagePending = file.name;
        imageAbort = new AbortController();
        emit();
        try {
          await bridge.importImage(file, imageAbort.signal);
        } finally {
          imagePending = undefined;
          imageAbort = undefined;
          emit();
        }
      });
    },
    presentation(view, wide, composerHeight) {
      const serial = ++presentationSerial;
      presentationQueue = presentationQueue
        .catch(() => {})
        .then(async () => {
          if (disposed || serial !== presentationSerial) return;
          try {
            requireOwner();
            const result = await bridge.resizePresentation(
              view,
              wide,
              composerHeight,
            );
            compatibilityWarning = result.warning;
            emit();
          } catch (error) {
            // A window request can outlive its conversation identity, including
            // first-send admission. It is obsolete, not a failed model turn.
            if (error instanceof NativeBridgeError && error.code === "CHAT_IDENTITY_STALE")
              return;
            // Other presentation failures still reach the caller's notice,
            // but must never replace send/stop or native generation errors.
            throw error;
          }
        });
      return presentationQueue;
    },
    async hide() {
      if (sending) await sendTask?.catch(() => {});
      await perform(async (identity) => {
        await flushDraft();
        await bridge.execute(
          "chat.host",
          { action: "hide" },
          { expectedIdentity: identity },
        );
      });
    },
    dispose() {
      if (disposed) return;
      disposed = true;
      cancelDraftTimer();
      if (expiryTimer) clearTimeout(expiryTimer);
      imageAbort?.abort();
      offSnapshot();
      offEvents();
      listeners.clear();
      drafts.clear();
      bridge.dispose();
    },
  };
  state = project();
  return driver;
}
