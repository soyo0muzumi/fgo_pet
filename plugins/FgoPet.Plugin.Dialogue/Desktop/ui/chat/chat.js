(() => {
  "use strict";

  const bridge = window.chrome && window.chrome.webview;
  const maxMessageChars = 12000;
  const draftDelayMs = 140;
  const requestTimeoutMs = 20000;
  const pending = new Map();
  const turnNodes = new Map();
  const draftCache = new Map();
  const acceptedDrafts = new Map();
  let requestSerial = 0;
  let localDraftRevision = 0;
  let draftTimer = 0;
  let draftQueue = Promise.resolve();
  let submitInProgress = false;
  let deferDraftWrites = false;
  let composing = false;
  let hideAfterSubmit = false;
  let state = null;
  let lastVersion = -1;
  let lastIdentityKey = "";
  let followingLatest = true;
  let hostVisible = true;
  let resizeFrame = 0;
  let scrollFrame = 0;

  const $ = (id) => document.getElementById(id);
  const el = {
    window: $("chat-window"),
    avatar: $("role-avatar-image"),
    avatarFallback: $("role-avatar-fallback"),
    roleName: $("role-name"),
    historyToggle: $("history-toggle"),
    historyPanel: $("history-panel"),
    historyBackdrop: $("history-backdrop"),
    historyClose: $("history-close"),
    historyNew: $("history-new"),
    historyFilter: $("history-current-project"),
    historyStatus: $("history-status"),
    historyRetry: $("history-retry"),
    historyItems: $("history-items"),
    historyMore: $("history-more"),
    expand: $("expand-window"),
    moreMenu: $("more-menu"),
    moreActions: $("more-actions"),
    providerStatus: $("provider-status"),
    modelStatus: $("model-status"),
    conversationStatus: $("conversation-status"),
    conversationError: $("conversation-error"),
    capabilityNotice: $("capability-notice"),
    capabilityText: $("capability-notice-text"),
    capabilityRetry: $("capability-retry"),
    capabilitySettings: $("capability-settings"),
    scroll: $("message-scroll"),
    messages: $("messages"),
    empty: $("empty-conversation"),
    jumpBottom: $("jump-bottom"),
    sources: $("recalled-sources"),
    sourceItems: $("source-items"),
    chips: $("context-chips"),
    composer: $("composer"),
    composerError: $("composer-error"),
    composerCount: $("composer-count"),
    send: $("send-message"),
    stop: $("stop-generation"),
    project: $("project-select"),
    refreshProjects: $("refresh-projects"),
    model: $("model-select"),
    pageStatus: $("page-status"),
    pageStatusText: $("page-status-text"),
    pageRetry: $("page-retry")
  };

  function logRequest(type, requestId, outcome, errorCode) {
    const entry = { type, requestId, outcome };
    if (errorCode) entry.errorCode = errorCode;
    console.info("[chat-ui]", entry);
  }

  function safeErrorText(code) {
    switch (code) {
      case "CHAT_IDENTITY_STALE": return "对话已切换，请重试刚才的操作。";
      case "CHAT_TARGET_INVALID": return "目标已变化，请刷新后重试。";
      case "CHAT_DRAFT_TOO_LONG": return `输入不能超过 ${maxMessageChars.toLocaleString("zh-CN")} 个字符。`;
      case "CHAT_NOT_AVAILABLE": return "此操作当前不可用。";
      case "CHAT_COMMAND_INVALID": return "操作未能完成，请重试。";
      default: return "聊天暂时无法完成此操作，请稍后重试。";
    }
  }

  function identityOf(snapshot) {
    const identity = snapshot && snapshot.identity;
    if (!identity || typeof identity.sessionId !== "string" || typeof identity.servantId !== "string" || typeof identity.conversationId !== "string") return "";
    return `${identity.sessionId}\u0000${identity.servantId}\u0000${identity.conversationId}`;
  }

  function conversationAnchorKey(snapshot) {
    const identity = snapshot && snapshot.identity;
    if (!identity || typeof identity.servantId !== "string" || typeof identity.conversationId !== "string") return "";
    return `fgopet.chat.anchor.v1:${encodeURIComponent(identity.servantId)}:${encodeURIComponent(identity.conversationId)}`;
  }

  function post(type, payload = {}) {
    if (!bridge) return Promise.reject(new Error("CHAT_BRIDGE_UNAVAILABLE"));
    const requestId = `chat_${++requestSerial}`;
    logRequest(type, requestId, "sent");
    return new Promise((resolve, reject) => {
      const timer = window.setTimeout(() => {
        pending.delete(requestId);
        logRequest(type, requestId, "timeout", "CHAT_TIMEOUT");
        reject(new Error("CHAT_TIMEOUT"));
      }, requestTimeoutMs);
      pending.set(requestId, { resolve, reject, timer, type });
      try {
        bridge.postMessage({ type, requestId, payload });
      } catch {
        window.clearTimeout(timer);
        pending.delete(requestId);
        logRequest(type, requestId, "failed", "CHAT_BRIDGE_UNAVAILABLE");
        reject(new Error("CHAT_BRIDGE_UNAVAILABLE"));
      }
    });
  }

  function identityPayload(snapshot = state) {
    const identity = snapshot && snapshot.identity;
    if (!identity) throw new Error("CHAT_IDENTITY_STALE");
    return {
      sessionId: identity.sessionId,
      servantId: identity.servantId,
      conversationId: identity.conversationId
    };
  }

  function appendIdentity(extra = {}, snapshot = state) {
    return Object.assign(identityPayload(snapshot), extra);
  }

  function applyTheme(variables, version) {
    if (variables && typeof variables === "object") {
      for (const [name, value] of Object.entries(variables)) {
        if (name.startsWith("--") && typeof value === "string") document.documentElement.style.setProperty(name, value);
      }
    }
    if (bridge && Number.isFinite(version)) bridge.postMessage({ type: "theme.ack", version });
  }

  function receive(event) {
    const message = event && event.data;
    if (!message || typeof message.type !== "string") return;
    if (message.type === "command.result") {
      const waiter = pending.get(message.requestId);
      if (!waiter) return;
      pending.delete(message.requestId);
      window.clearTimeout(waiter.timer);
      if (message.success === true) {
        logRequest(waiter.type, message.requestId, "succeeded");
        waiter.resolve(message.payload || {});
      } else {
        const code = typeof message.errorCode === "string" ? message.errorCode : "CHAT_COMMAND_INVALID";
        logRequest(waiter.type, message.requestId, "rejected", code);
        waiter.reject(new Error(code));
      }
      return;
    }
    if (message.type === "theme.changed") {
      applyTheme(message.variables, message.version);
      return;
    }
    if (message.type === "chat.visibility") {
      if (message.visible === false) {
        saveAnchor(); hostVisible = false;
        const key = state ? identityOf(state) : "";
        const cache = draftCache.get(key);
        if (cache && cache.text.length <= maxMessageChars) void flushDraft(cache.text, cache.revision, key);
      } else if (message.visible === true) {
        hostVisible = true;
        restoreAnchor(readSavedAnchor(state));
        const expected = state ? identityOf(state) : "";
        void post("chat.get", {}).then(result => {
          if (result.snapshot && (!state || identityOf(state) === expected)) applySnapshot(result.snapshot, "get");
        }).catch(error => setPageStatus(safeErrorText(error.message)));
      }
      return;
    }
    if (message.type === "chat.snapshot") applySnapshot(message.payload, "event");
  }

  function currentAnchor() {
    const scrollBox = el.scroll.getBoundingClientRect();
    const atBottom = el.scroll.scrollHeight - el.scroll.scrollTop - el.scroll.clientHeight <= 42;
    if (atBottom) return { atBottom: true, turnId: "", offset: 0 };
    let anchor = null;
    for (const child of el.messages.children) {
      const rect = child.getBoundingClientRect();
      if (rect.bottom > scrollBox.top + 1) {
        anchor = { atBottom: false, turnId: child.dataset.turnId || "", offset: rect.top - scrollBox.top };
        break;
      }
    }
    return anchor || { atBottom: false, turnId: "", offset: 0 };
  }

  function saveAnchor(snapshot = state) {
    const key = conversationAnchorKey(snapshot);
    if (!key) return;
    try { sessionStorage.setItem(key, JSON.stringify(currentAnchor())); } catch { /* WebView storage can be unavailable. */ }
  }

  function readSavedAnchor(snapshot) {
    const key = conversationAnchorKey(snapshot);
    if (!key) return null;
    try {
      const value = JSON.parse(sessionStorage.getItem(key) || "null");
      if (value && typeof value.atBottom === "boolean" && typeof value.turnId === "string" && Number.isFinite(value.offset)) return value;
    } catch { /* Ignore stale or unavailable view state. */ }
    return null;
  }

  function restoreAnchor(anchor) {
    if (!anchor || anchor.atBottom) {
      el.scroll.scrollTop = el.scroll.scrollHeight;
      followingLatest = true;
      el.jumpBottom.hidden = true;
      return;
    }
    const node = anchor.turnId ? turnNodes.get(anchor.turnId)?.article : null;
    if (node && node.isConnected) {
      const box = el.scroll.getBoundingClientRect();
      const rect = node.getBoundingClientRect();
      el.scroll.scrollTop += rect.top - box.top - anchor.offset;
    }
    followingLatest = false;
  }

  function applyCommandSnapshot(snapshot, expectedIdentityKey, allowIdentityChange = false) {
    if (!snapshot || !snapshot.identity) return;
    if (!state || identityOf(state) !== expectedIdentityKey) return;
    if (!allowIdentityChange && identityOf(snapshot) !== expectedIdentityKey) return;
    if (allowIdentityChange && snapshot.identity.sessionId !== state.identity.sessionId) return;
    applySnapshot(snapshot, "command");
  }

  function applySnapshot(snapshot, source) {
    if (!snapshot || !snapshot.identity || !Number.isFinite(snapshot.version)) return;
    const incomingKey = identityOf(snapshot);
    if (!incomingKey) return;
    const sameSession = state && state.identity.sessionId === snapshot.identity.sessionId;
    if (sameSession && snapshot.version <= lastVersion) return;

    const previousState = state;
    const previousKey = lastIdentityKey;
    let preservedAnchor = null;
    if (previousState && previousKey === incomingKey) {
      preservedAnchor = hostVisible ? currentAnchor() : readSavedAnchor(snapshot);
      followingLatest = !preservedAnchor || preservedAnchor.atBottom;
    } else if (previousState) {
      saveAnchor(previousState);
      preservedAnchor = readSavedAnchor(snapshot);
    } else {
      preservedAnchor = readSavedAnchor(snapshot);
    }

    // The first send assigns a conversation ID. Carry only newer local typing
    // across that admission; an explicit history/role switch must not inherit it.
    if (previousState && submitInProgress && !previousState.identity.conversationId
        && snapshot.identity.conversationId && sameSession
        && previousState.identity.servantId === snapshot.identity.servantId) {
      const pendingDraft = draftCache.get(previousKey);
      if (pendingDraft && pendingDraft.revision > (snapshot.draft?.revision || 0)) {
        draftCache.set(incomingKey, pendingDraft);
        draftCache.delete(previousKey);
      }
    }
    state = snapshot;
    lastIdentityKey = incomingKey;
    lastVersion = snapshot.version;
    if (Number.isFinite(snapshot.draft && snapshot.draft.revision)) localDraftRevision = Math.max(localDraftRevision, snapshot.draft.revision);
    if (snapshot.draft && typeof snapshot.draft.text === "string" && Number.isFinite(snapshot.draft.revision)) {
      acceptedDrafts.set(incomingKey, { text: snapshot.draft.text, revision: snapshot.draft.revision });
    }

    syncComposerFromSnapshot(snapshot, previousKey !== incomingKey);
    renderSnapshot(snapshot);
    // Restore after this render's layout read, before another user scroll can occur.
    // A deferred frame could replay a stale anchor over newer user interaction.
    restoreAnchor(preservedAnchor);
    saveAnchor(snapshot);
    if (source === "event" && document.visibilityState === "hidden") setPageStatus("有新的聊天内容");
  }

  function syncComposerFromSnapshot(snapshot, identityChanged) {
    const draft = snapshot.draft && typeof snapshot.draft.text === "string" ? snapshot.draft : { text: "", revision: 0 };
    const cache = draftCache.get(lastIdentityKey);
    if (cache && cache.revision > draft.revision) {
      if (identityChanged || el.composer.value !== cache.text) el.composer.value = cache.text;
      showComposerError(cache.text.length > maxMessageChars ? `输入不能超过 ${maxMessageChars.toLocaleString("zh-CN")} 个字符。` : "");
      updateComposerControls();
      return;
    }
    if (cache && cache.revision <= draft.revision) draftCache.delete(lastIdentityKey);
    // Unacknowledged input was handled above. The owner is authoritative otherwise,
    // including clearing the sent draft while the composer keeps keyboard focus.
    if (el.composer.value !== draft.text) el.composer.value = draft.text;
    showComposerError("");
    updateComposerControls();
  }

  function setText(node, value) {
    node.textContent = typeof value === "string" ? value : value == null ? "" : String(value);
  }

  function makeButton(className, label, iconId, actionName, targetId, textLabel) {
    const button = document.createElement("button");
    button.type = "button";
    button.className = className;
    button.setAttribute("aria-label", label);
    button.title = label;
    button.dataset.action = actionName;
    if (targetId !== undefined) button.dataset.targetId = targetId;
    if (iconId) {
      const icon = document.createElementNS("http://www.w3.org/2000/svg", "svg");
      icon.classList.add("icon");
      icon.setAttribute("aria-hidden", "true");
      const use = document.createElementNS("http://www.w3.org/2000/svg", "use");
      use.setAttribute("href", `#icon-${iconId}`);
      icon.append(use);
      button.append(icon);
    }
    if (textLabel) {
      const text = document.createElement("span");
      text.className = "turn-action-text";
      text.textContent = textLabel;
      button.append(text);
    }
    return button;
  }

  function getTurnNode(turn) {
    let record = turnNodes.get(turn.id);
    const role = turn.role === "user" ? "user" : "assistant";
    if (!record || record.role !== role) {
      if (record) record.article.remove();
      const article = document.createElement("article");
      article.className = `turn turn--${role}`;
      article.dataset.turnId = turn.id;
      article.setAttribute("data-role", role);
      record = { role, article, heading: null, body: null, reasoning: null, actions: null, streaming: null };
      if (role === "assistant") {
        const heading = document.createElement("div");
        heading.className = "turn-heading";
        const marker = document.createElement("span");
        marker.className = "turn-marker";
        marker.setAttribute("aria-hidden", "true");
        const name = document.createElement("span");
        heading.append(marker, name);
        record.heading = heading;
        article.append(heading);
      }
      const body = document.createElement("div");
      body.className = "chat-message-text";
      article.append(body);
      record.body = body;
      record.actions = document.createElement("div");
      record.actions.className = "turn-actions";
      article.append(record.actions);
      record.streaming = document.createElement("div");
      record.streaming.className = "streaming-indicator";
      record.streaming.textContent = "正在生成";
      record.streaming.hidden = true;
      article.append(record.streaming);
      turnNodes.set(turn.id, record);
    }
    return record;
  }

  function renderTurn(turn, presentation) {
    const record = getTurnNode(turn);
    if (record.heading) setText(record.heading.lastElementChild, presentation.roleName || "助手");
    setText(record.body, turn.text);

    const reasoningText = typeof turn.reasoningText === "string" ? turn.reasoningText : "";
    if (presentation.showReasoning && (reasoningText || turn.reasoningSummary)) {
      if (!record.reasoning) {
        const details = document.createElement("details");
        details.className = "turn-reasoning";
        const summary = document.createElement("summary");
        const text = document.createElement("div");
        text.className = "chat-message-text";
        details.append(summary, text);
        record.article.insertBefore(details, record.actions);
        record.reasoning = { details, summary, text };
        details.open = Boolean(turn.isReasoningExpanded);
      }
      const label = [turn.reasoningSummary || "思考过程", turn.reasoningDurationText].filter(Boolean).join(" · ");
      setText(record.reasoning.summary, label);
      setText(record.reasoning.text, reasoningText);
      record.reasoning.details.hidden = false;
    } else if (record.reasoning) {
      record.reasoning.details.hidden = true;
    }

    record.actions.replaceChildren();
    if (turn.canCopy) record.actions.append(makeButton("turn-action turn-copy", "复制消息", "copy", "copyTurn", turn.id));
    if (turn.canReadAloud) {
      const speechLabel = typeof turn.speechActionText === "string" && turn.speechActionText ? turn.speechActionText : "朗读";
      const readButton = makeButton("turn-action", speechLabel, "read", "readTurn", turn.id, speechLabel);
      record.actions.append(readButton);
      if (turn.speechStatusText) {
        const speechStatus = document.createElement("span");
        speechStatus.className = "turn-action-text";
        speechStatus.setAttribute("role", "status");
        setText(speechStatus, turn.speechStatusText);
        record.actions.append(speechStatus);
      }
      if (turn.speechNeedsConfiguration) {
        const settingsButton = makeButton("turn-action", "打开语音设置", null, "openSpeechSettings", undefined, "语音设置");
        record.actions.append(settingsButton);
      }
    }
    if (turn.canOpenWorkspace) record.actions.append(makeButton("turn-action", "查看关联工作项", "folder", "openWorkspace", turn.id, "查看事项"));
    record.actions.hidden = record.actions.childElementCount === 0;
    record.streaming.hidden = !turn.isStreaming;
    record.article.hidden = turn.role !== "user" && !String(turn.text || "").trim()
      && !turn.isStreaming && !turn.isThinkingActive && !String(turn.reasoningText || "").trim();
    return record.article;
  }

  function renderMessages(snapshot) {
    const turns = Array.isArray(snapshot.conversation && snapshot.conversation.turns) ? snapshot.conversation.turns : [];
    const wanted = new Set();
    const ordered = [];
    for (const turn of turns) {
      if (!turn || typeof turn.id !== "string" || !turn.id) continue;
      wanted.add(turn.id);
      ordered.push(renderTurn(turn, snapshot.conversation));
    }
    for (const [id, record] of turnNodes) {
      if (!wanted.has(id)) {
        record.article.remove();
        turnNodes.delete(id);
      }
    }
    const fragment = document.createDocumentFragment();
    for (const node of ordered) fragment.append(node);
    el.messages.append(fragment);
    el.empty.hidden = turns.length > 0;
  }

  function renderSources(conversation) {
    const sources = Array.isArray(conversation.recalledSources) ? conversation.recalledSources : [];
    el.sourceItems.replaceChildren();
    for (const source of sources) {
      if (!source || typeof source.id !== "string" || !source.id) continue;
      const row = document.createElement("div");
      row.className = "source-row";
      const button = document.createElement("button");
      button.type = "button";
      button.className = "source-open";
      button.dataset.action = "openSource";
      button.dataset.targetId = source.id;
      setText(button, source.title || "打开来源");
      row.append(button);
      if (source.detail) {
        const detail = document.createElement("span");
        detail.className = "source-detail";
        setText(detail, source.detail);
        row.append(detail);
      }
      el.sourceItems.append(row);
    }
    el.sources.hidden = sources.length === 0;
  }

  function appendOptions(select, choices, selectedId, emptyLabel) {
    const safeChoices = Array.isArray(choices) ? choices : [];
    const fragment = document.createDocumentFragment();
    let selectedFound = false;
    if (safeChoices.length === 0) {
      const option = document.createElement("option");
      option.value = "";
      option.textContent = emptyLabel;
      option.disabled = true;
      option.selected = true;
      fragment.append(option);
    } else {
      for (const choice of safeChoices) {
        if (!choice || typeof choice.id !== "string" || !choice.id) continue;
        const option = document.createElement("option");
        option.value = choice.id;
        option.textContent = typeof choice.label === "string" ? choice.label : "未命名";
        option.selected = choice.id === selectedId || (!selectedId && choice.selected === true);
        selectedFound = selectedFound || option.selected;
        option.disabled = choice.canSelect === false;
        fragment.append(option);
      }
    }
    select.replaceChildren(fragment);
    if (safeChoices.length > 0 && !selectedFound) select.selectedIndex = -1;
    select.disabled = safeChoices.length === 0 || Boolean(state && state.conversation && state.conversation.isStreaming);
  }

  function renderChoices(snapshot) {
    const presentation = snapshot.presentation || {};
    const conversation = snapshot.conversation || {};
    const actions = presentation.actions || {};
    const project = conversation.project || null;
    const projectId = project && typeof project.id === "string" ? project.id : "";
    const projects = Array.isArray(presentation.projects) ? presentation.projects : [];
    const models = Array.isArray(presentation.models) ? presentation.models : [];
    appendOptions(el.project, projects, projectId, "暂无可选项目");
    appendOptions(el.model, models, "", "暂无可用模型");
    el.refreshProjects.hidden = actions.canRefreshProjects !== true;
    el.refreshProjects.disabled = presentation.isProjectsLoading === true || conversation.isStreaming === true;
    el.refreshProjects.title = presentation.isProjectsLoading ? "正在刷新项目" : presentation.projectsStatus || "刷新项目列表";
    if (presentation.projectsStatus) el.refreshProjects.setAttribute("aria-description", presentation.projectsStatus);
    else el.refreshProjects.removeAttribute("aria-description");
  }

  function renderContextChips(conversation) {
    el.chips.replaceChildren();
    const chips = Array.isArray(conversation.contextChips) ? conversation.contextChips : [];
    for (const chip of chips) {
      if (!chip || typeof chip.id !== "string" || !chip.id) continue;
      const item = document.createElement("span");
      item.className = "context-chip";
      const label = document.createElement("span");
      label.className = "context-chip-label";
      setText(label, chip.label);
      const remove = makeButton("chip-remove", `移除上下文：${chip.label || "此项目"}`, "close", "removeContextChip", chip.id);
      remove.setAttribute("aria-label", `移除上下文：${chip.label || "此项目"}`);
      item.append(label, remove);
      el.chips.append(item);
    }
    el.chips.hidden = chips.length === 0;
  }

  function renderHistory(snapshot) {
    const history = snapshot.history || {};
    const items = Array.isArray(history.items) ? history.items : [];
    el.historyFilter.checked = history.onlyCurrentProject === true;
    setText(el.historyStatus, history.status || (history.isLoading ? "正在读取历史对话…" : items.length === 0 ? "暂无历史对话" : ""));
    el.historyRetry.hidden = history.isLoading === true || items.length > 0;
    el.historyRetry.disabled = history.isLoading === true;
    el.historyMore.hidden = history.hasMore !== true;
    el.historyMore.disabled = history.isLoading === true;
    el.historyMore.textContent = history.isLoading ? "正在加载…" : "加载更多";
    const pendingId = typeof history.pendingDeleteConversationId === "string" ? history.pendingDeleteConversationId : "";
    el.historyItems.replaceChildren();
    let pendingVisible = !pendingId;
    for (const item of items) {
      if (!item || typeof item.conversationId !== "string" || !item.conversationId) continue;
      if (pendingId && item.conversationId === pendingId) pendingVisible = true;
      const row = document.createElement("div");
      row.className = "history-item";
      row.dataset.conversationId = item.conversationId;
      const open = document.createElement("button");
      open.type = "button";
      open.className = "history-item-open";
      open.dataset.action = "openHistory";
      open.dataset.targetId = item.conversationId;
      open.setAttribute("aria-current", String(item.conversationId === snapshot.identity.conversationId));
      const title = document.createElement("span");
      title.className = "history-item-title";
      setText(title, item.title || "新对话");
      const meta = document.createElement("span");
      meta.className = "history-item-meta";
      setText(meta, [item.updatedText, item.status].filter(Boolean).join(" · "));
      open.append(title, meta);
      row.append(open);
      if (item.conversationId === pendingId) {
        const confirmation = document.createElement("div");
        confirmation.className = "delete-confirmation";
        confirmation.setAttribute("role", "group");
        confirmation.setAttribute("aria-label", "确认删除对话");
        const prompt = document.createElement("span");
        prompt.textContent = `删除“${item.title || "新对话"}”？`;
        const confirm = document.createElement("button");
        confirm.type = "button";
        confirm.className = "confirm-delete";
        confirm.dataset.action = "confirmDelete";
        confirm.dataset.targetId = pendingId;
        confirm.textContent = "删除";
        confirm.disabled = history.canDelete !== true;
        const cancel = document.createElement("button");
        cancel.type = "button";
        cancel.dataset.action = "cancelDelete";
        cancel.textContent = "取消";
        confirmation.append(prompt, confirm, cancel);
        row.append(confirmation);
      } else {
        const remove = makeButton("history-delete", "删除此对话", "delete", "requestDelete", item.conversationId, "删除");
        remove.disabled = Boolean(pendingId) || history.canDelete !== true;
        row.append(remove);
      }
      el.historyItems.append(row);
    }
    if (pendingId && !pendingVisible) {
      setText(el.historyStatus, "待确认的对话已不在当前列表中。");
      const cancel = document.createElement("button");
      cancel.type = "button";
      cancel.className = "text-button";
      cancel.dataset.action = "cancelDelete";
      cancel.textContent = "取消删除确认";
      el.historyItems.append(cancel);
    }
  }

  const hostActionLabels = [
    ["canOpenFocus", "openFocus", "打开专注模式"],
    ["canOpenWorkspaceOverview", "openWorkspaceOverview", "打开工作区"],
    ["canCreateWorkspaceItem", "newWorkspaceItem", "新建工作事项"],
    ["canOpenPersonalizationSettings", "openPersonalizationSettings", "个性化设置"],
    ["canOpenSpeechSettings", "openSpeechSettings", "语音设置"],
    ["canOpenModelSettings", "openModelSettings", "模型设置"]
  ];

  function renderMoreActions(snapshot) {
    const actions = snapshot.presentation && snapshot.presentation.actions || {};
    const canOpenModelSettings = actions.canOpenModelSettings === true || snapshot.conversation.canOpenModelSettings === true;
    el.moreActions.replaceChildren();
    for (const [availability, action, label] of hostActionLabels) {
      const available = availability === "canOpenModelSettings" ? canOpenModelSettings : actions[availability] === true;
      if (!available) continue;
      const button = document.createElement("button");
      button.type = "button";
      button.className = "more-action";
      button.dataset.action = action;
      button.textContent = label;
      el.moreActions.append(button);
    }
    el.moreMenu.hidden = el.moreActions.childElementCount === 0;
  }

  function renderSnapshot(snapshot) {
    const presentation = snapshot.presentation || {};
    const conversation = snapshot.conversation || {};
    const history = snapshot.history || {};
    const roleName = typeof presentation.roleName === "string" && presentation.roleName.trim() ? presentation.roleName : "聊天";
    setText(el.roleName, roleName);
    setText(el.avatarFallback, [...roleName][0] || "聊");
    const avatarUrl = typeof presentation.avatarPngDataUrl === "string" && /^data:image\/png;base64,[A-Za-z0-9+/=]+$/.test(presentation.avatarPngDataUrl)
      ? presentation.avatarPngDataUrl : "";
    if (avatarUrl) {
      if (el.avatar.src !== avatarUrl) el.avatar.src = avatarUrl;
      el.avatar.hidden = false;
      el.avatarFallback.hidden = true;
    } else {
      el.avatar.removeAttribute("src");
      el.avatar.hidden = true;
      el.avatarFallback.hidden = false;
    }

    el.window.classList.toggle("is-expanded", window.innerWidth >= 720);
    const expanded = window.innerWidth >= 720;
    el.expand.setAttribute("aria-pressed", String(expanded));
    el.expand.setAttribute("aria-label", expanded ? "缩小窗口" : "展开窗口");
    el.expand.title = expanded ? "缩小窗口" : "展开窗口";
    const expandUse = el.expand.querySelector("use");
    expandUse.setAttribute("href", expanded ? "#icon-collapse" : "#icon-expand");
    el.historyToggle.setAttribute("aria-expanded", String(!el.historyPanel.hidden));

    setText(el.providerStatus, conversation.providerStatusText);
    setText(el.modelStatus, conversation.modelStatusText);
    const status = conversation.isThinking
      ? [conversation.thinkingTimerText || "正在思考", conversation.requestStatusText].filter(Boolean).join(" · ")
      : conversation.requestStatusText || "";
    setText(el.conversationStatus, status);
    setText(el.conversationError, conversation.errorText || "");
    el.conversationError.hidden = !conversation.errorText;
    const capability = conversation.capabilityNotice || {};
    setText(el.capabilityText, capability.text || "");
    el.capabilityNotice.hidden = !capability.text;
    el.capabilityNotice.dataset.kind = ["error", "empty", "fallback"].includes(capability.kind) ? capability.kind : "none";
    el.capabilityRetry.hidden = conversation.canRetryCapability !== true;
    const actions = presentation.actions || {};
    const canOpenModelSettings = actions.canOpenModelSettings === true || conversation.canOpenModelSettings === true;
    el.capabilitySettings.hidden = !(conversation.configurationRequired === true && canOpenModelSettings);
    el.stop.hidden = conversation.canStop !== true;
    el.send.hidden = conversation.canStop === true;
    renderMessages(snapshot);
    renderSources(conversation);
    renderContextChips(conversation);
    renderChoices(snapshot);
    renderHistory(snapshot);
    renderMoreActions(snapshot);
    updateComposerControls();
  }

  function showComposerError(text) {
    setText(el.composerError, text || "");
    el.composerError.hidden = !text;
    el.composer.setAttribute("aria-invalid", String(Boolean(text)));
  }

  function updateComposerControls() {
    const text = el.composer.value;
    const overLimit = text.length > maxMessageChars;
    const conversation = state && state.conversation;
    const serverDraftText = state && state.draft && typeof state.draft.text === "string" ? state.draft.text : "";
    const localDraftReady = Boolean(text.trim() && text !== serverDraftText && conversation && conversation.configurationRequired !== true);
    const canSend = Boolean(conversation && conversation.canSend) || localDraftReady;
    const streaming = Boolean(state && state.conversation && state.conversation.isStreaming);
    el.composerCount.textContent = text.length > maxMessageChars - 500 ? `${text.length.toLocaleString("zh-CN")} / ${maxMessageChars.toLocaleString("zh-CN")}` : "";
    el.composer.disabled = !state;
    el.send.disabled = !canSend || streaming || overLimit || !text.trim() || submitInProgress;
    el.stop.disabled = !state || !state.conversation || state.conversation.canStop !== true;
    if (el.newConversation) el.newConversation.disabled = !state || streaming;
    el.historyNew.disabled = !state || streaming;
    if (overLimit) showComposerError(`输入不能超过 ${maxMessageChars.toLocaleString("zh-CN")} 个字符。`);
    else if (!state || !state.draft || state.draft.text !== text) {
      if (el.composerError.textContent.includes("不能超过")) showComposerError("");
    }
    const disableSelections = streaming;
    el.project.disabled = disableSelections || el.project.options.length === 0;
    el.model.disabled = disableSelections || el.model.options.length === 0;
  }

  function scheduleDraftWrite() {
    if (!state || !state.identity || deferDraftWrites) return;
    const text = el.composer.value;
    if (text.length > maxMessageChars) return;
    if (draftTimer) window.clearTimeout(draftTimer);
    draftTimer = window.setTimeout(() => {
      draftTimer = 0;
      const currentKey = identityOf(state);
      const cache = draftCache.get(currentKey);
      if (cache) queueDraftWrite(cache.text, cache.revision, currentKey);
    }, draftDelayMs);
  }

  function queueDraftWrite(text, revision, identityKey) {
    const identity = state && identityOf(state) === identityKey ? identityPayload(state) : null;
    if (!identity || text.length > maxMessageChars) return Promise.resolve(false);
    const accepted = acceptedDrafts.get(identityKey);
    if (accepted && accepted.revision === revision && accepted.text === text) return Promise.resolve(true);
    if (accepted && accepted.revision > revision) return Promise.resolve(false);
    const payload = Object.assign(identity, { text, revision });
    const task = draftQueue.catch(() => false).then(async () => {
      if (!state || identityOf(state) !== identityKey) return false;
      const latestAccepted = acceptedDrafts.get(identityKey);
      if (latestAccepted && latestAccepted.revision === revision && latestAccepted.text === text) return true;
      if (latestAccepted && latestAccepted.revision > revision) return false;
      const result = await post("chat.draft", payload);
      if (result && result.snapshot) applyCommandSnapshot(result.snapshot, identityKey, false);
      const cached = draftCache.get(identityKey);
      if (cached && cached.revision === revision && cached.text === text && result.snapshot && result.snapshot.draft && result.snapshot.draft.revision >= revision) {
        draftCache.delete(identityKey);
      }
      return true;
    }).catch(error => {
      if (identityOf(state) === identityKey) setPageStatus(safeErrorText(error.message));
      return false;
    });
    draftQueue = task;
    return task;
  }

  function setPageStatus(message, retry = false) {
    setText(el.pageStatusText, message || "");
    el.pageStatus.hidden = !message;
    el.pageRetry.hidden = !retry;
    el.pageRetry.disabled = !bridge;
  }

  async function flushDraft(text, revision, identityKey) {
    if (draftTimer) {
      window.clearTimeout(draftTimer);
      draftTimer = 0;
    }
    const accepted = acceptedDrafts.get(identityKey);
    if (accepted && accepted.revision === revision && accepted.text === text) return true;
    if (accepted && accepted.revision > revision) return false;
    await draftQueue.catch(() => false);
    const afterQueue = acceptedDrafts.get(identityKey);
    if (afterQueue && afterQueue.revision === revision && afterQueue.text === text) return true;
    if (afterQueue && afterQueue.revision > revision) return false;
    return queueDraftWrite(text, revision, identityKey);
  }

  async function submitMessage() {
    if (submitInProgress || !state || !state.conversation || state.conversation.isStreaming === true) return;
    const text = el.composer.value;
    if (!text.trim()) return;
    const serverDraftText = state.draft && typeof state.draft.text === "string" ? state.draft.text : "";
    if (state.conversation.canSend !== true && (text === serverDraftText || state.conversation.configurationRequired === true)) return;
    if (text.length > maxMessageChars) {
      showComposerError(`输入不能超过 ${maxMessageChars.toLocaleString("zh-CN")} 个字符。`);
      el.composer.focus();
      return;
    }
    const identityKey = identityOf(state);
    const revision = localDraftRevision;
    submitInProgress = true;
    deferDraftWrites = true;
    updateComposerControls();
    setPageStatus("");
    try {
      const flushed = await flushDraft(text, revision, identityKey);
      if (!flushed || !state || identityOf(state) !== identityKey) return;
      const result = await post("chat.send", identityPayload(state));
      if (result.snapshot) applyCommandSnapshot(result.snapshot, identityKey, true);
    } catch (error) {
      if (identityOf(state) === identityKey) setPageStatus(safeErrorText(error.message));
    } finally {
      submitInProgress = false;
      deferDraftWrites = false;
      updateComposerControls();
      const currentKey = state ? identityOf(state) : "";
      // A migrated draft is stored under the admitted owner's identity.
      const latest = draftCache.get(currentKey);
      if (latest && latest.revision > revision) void queueDraftWrite(latest.text, latest.revision, currentKey);
      if (hideAfterSubmit) {
        hideAfterSubmit = false;
        void hideWindow();
      }
    }
  }

  async function commandWithSnapshot(type, payload, allowIdentityChange = false) {
    const expected = state ? identityOf(state) : "";
    try {
      const result = await post(type, payload);
      if (result.snapshot && expected) applyCommandSnapshot(result.snapshot, expected, allowIdentityChange);
      setPageStatus("");
      return result;
    } catch (error) {
      setPageStatus(safeErrorText(error.message));
      throw error;
    }
  }

  async function hostAction(action, extras = {}) {
    if (!state) return;
    const expected = identityOf(state);
    const payload = appendIdentity(Object.assign({ action }, extras));
    try {
      const result = await post("chat.host", payload);
      if (result.snapshot) applyCommandSnapshot(result.snapshot, expected, false);
      setPageStatus("");
    } catch (error) {
      if (identityOf(state) === expected) setPageStatus(safeErrorText(error.message));
    }
  }

  async function loadHistory(append) {
    if (!state) return;
    const payload = appendIdentity({ onlyCurrentProject: el.historyFilter.checked, append });
    await commandWithSnapshot("chat.history", payload, false).catch(() => {});
  }

  function setHistoryOpen(open, load = true) {
    const wasOpen = !el.historyPanel.hidden;
    el.historyPanel.hidden = !open;
    el.historyBackdrop.hidden = !open || window.innerWidth >= 720;
    el.historyToggle.setAttribute("aria-expanded", String(open));
    if (open && load && !wasOpen) void loadHistory(false);
    if (!open && wasOpen) el.historyToggle.focus();
  }

  async function hideWindow() {
    if (!state) return;
    if (submitInProgress) {
      hideAfterSubmit = true;
      return;
    }
    const identityKey = identityOf(state);
    const cache = draftCache.get(identityKey);
    if (cache && cache.text.length <= maxMessageChars) await flushDraft(cache.text, cache.revision, identityKey);
    await hostAction("hide");
  }

  async function refreshSnapshot() {
    try {
      const result = await post("chat.get", {});
      if (result && result.snapshot) applySnapshot(result.snapshot, "get");
      setPageStatus("");
    } catch (error) {
      setPageStatus(safeErrorText(error.message), true);
    }
  }

  async function historyOperation(action, targetId) {
    if (!state) return;
    if (action === "openHistory") {
      const expected = identityOf(state);
      try {
        const result = await post("chat.history.open", appendIdentity({ targetConversationId: targetId }));
        if (result.snapshot) applyCommandSnapshot(result.snapshot, expected, true);
        setHistoryOpen(false, false);
        setPageStatus("");
      } catch (error) {
        setPageStatus(safeErrorText(error.message));
      }
      return;
    }
    let type;
    if (action === "requestDelete") type = "chat.history.requestDelete";
    else if (action === "confirmDelete") type = "chat.history.confirmDelete";
    else if (action === "cancelDelete") type = "chat.history.cancelDelete";
    else return;
    const payload = action === "cancelDelete" ? identityPayload(state) : appendIdentity({ targetConversationId: targetId });
    await commandWithSnapshot(type, payload, false).catch(() => {});
  }

  function onInput() {
    localDraftRevision = Math.max(localDraftRevision, Number(state && state.draft && state.draft.revision) || 0) + 1;
    const identityKey = identityOf(state);
    if (identityKey) draftCache.set(identityKey, { text: el.composer.value, revision: localDraftRevision });
    if (el.composer.value.length <= maxMessageChars) showComposerError("");
    updateComposerControls();
    scheduleDraftWrite();
  }

  function resizeState() {
    if (resizeFrame) return;
    resizeFrame = requestAnimationFrame(() => {
      resizeFrame = 0;
      const expanded = window.innerWidth >= 720;
      el.window.classList.toggle("is-expanded", expanded);
      el.expand.setAttribute("aria-pressed", String(expanded));
      el.expand.setAttribute("aria-label", expanded ? "缩小窗口" : "展开窗口");
      el.expand.title = expanded ? "缩小窗口" : "展开窗口";
      el.expand.querySelector("use").setAttribute("href", expanded ? "#icon-collapse" : "#icon-expand");
      if (el.historyPanel.hidden === false) el.historyBackdrop.hidden = expanded;
    });
  }

  function onScroll() {
    if (scrollFrame) return;
    scrollFrame = requestAnimationFrame(() => {
      scrollFrame = 0;
      const atBottom = el.scroll.scrollHeight - el.scroll.scrollTop - el.scroll.clientHeight <= 42;
      followingLatest = atBottom;
      el.jumpBottom.hidden = atBottom;
      saveAnchor();
    });
  }

  async function handleAction(action, targetId) {
    if (action === "openHistory" || action === "requestDelete" || action === "confirmDelete" || action === "cancelDelete") {
      await historyOperation(action, targetId);
      return;
    }
    const targetActions = new Set(["copyTurn", "readTurn", "openSource", "openWorkspace", "removeContextChip"]);
    const extras = targetActions.has(action) ? { targetId } : {};
    await hostAction(action, extras);
  }

  function bindEvents() {
    el.composer.addEventListener("input", onInput);
    el.composer.addEventListener("compositionstart", () => { composing = true; });
    el.composer.addEventListener("compositionend", () => { composing = false; });
    el.composer.addEventListener("keydown", event => {
      if (event.key !== "Enter" || event.shiftKey) return;
      if (composing || event.isComposing || event.keyCode === 229) return;
      event.preventDefault();
      void submitMessage();
    });
    el.send.addEventListener("click", () => { void submitMessage(); });
    el.stop.addEventListener("click", () => {
      if (state) void commandWithSnapshot("chat.stop", identityPayload(state), false).catch(() => {});
    });
    el.newConversation = $("new-conversation");
    el.newConversation.addEventListener("click", () => {
      if (!state || state.conversation.isStreaming) return;
      void commandWithSnapshot("chat.new", identityPayload(state), true).then(() => setHistoryOpen(false, false)).catch(() => {});
    });
    el.historyNew.addEventListener("click", () => el.newConversation.click());
    el.historyToggle.addEventListener("click", () => setHistoryOpen(el.historyPanel.hidden));
    el.historyClose.addEventListener("click", () => setHistoryOpen(false, false));
    el.historyBackdrop.addEventListener("click", () => setHistoryOpen(false, false));
    el.historyFilter.addEventListener("change", () => { void loadHistory(false); });
    el.historyRetry.addEventListener("click", () => { void loadHistory(false); });
    el.historyMore.addEventListener("click", () => { void loadHistory(true); });
    el.expand.addEventListener("click", () => {
      const expanded = window.innerWidth >= 720;
      void hostAction("setExpanded", { expanded: !expanded });
    });
    $("hide-window").addEventListener("click", () => { void hideWindow(); });
    el.pageRetry.addEventListener("click", () => { void refreshSnapshot(); });
    el.refreshProjects.addEventListener("click", () => { void hostAction("refreshProjects"); });
    el.capabilityRetry.addEventListener("click", () => {
      if (state) void commandWithSnapshot("chat.retryCapability", identityPayload(state), false).catch(() => {});
    });
    el.capabilitySettings.addEventListener("click", () => { void hostAction("openModelSettings"); });
    el.project.addEventListener("change", () => { if (el.project.value) void hostAction("selectProject", { targetId: el.project.value }); });
    el.model.addEventListener("change", () => { if (el.model.value) void hostAction("selectModel", { targetId: el.model.value }); });
    el.jumpBottom.addEventListener("click", () => {
      el.scroll.scrollTop = el.scroll.scrollHeight;
      followingLatest = true;
      el.jumpBottom.hidden = true;
      saveAnchor();
      el.composer.focus();
    });
    el.scroll.addEventListener("scroll", onScroll, { passive: true });
    el.messages.addEventListener("click", event => {
      const button = event.target.closest("button[data-action]");
      if (!button) return;
      const action = button.dataset.action;
      const targetId = button.dataset.targetId;
      if (action === "openSpeechSettings") { void hostAction("openSpeechSettings"); return; }
      void handleAction(action, targetId);
    });
    el.historyItems.addEventListener("click", event => {
      const button = event.target.closest("button[data-action]");
      if (!button) return;
      void handleAction(button.dataset.action, button.dataset.targetId);
    });
    el.sourceItems.addEventListener("click", event => {
      const button = event.target.closest("button[data-action='openSource']");
      if (button) void hostAction("openSource", { targetId: button.dataset.targetId });
    });
    el.chips.addEventListener("click", event => {
      const button = event.target.closest("button[data-action='removeContextChip']");
      if (button) void hostAction("removeContextChip", { targetId: button.dataset.targetId });
    });
    el.moreActions.addEventListener("click", event => {
      const button = event.target.closest("button[data-action]");
      if (!button) return;
      el.moreMenu.open = false;
      void hostAction(button.dataset.action);
    });
    document.addEventListener("keydown", event => {
      if (event.key !== "Escape" || event.isComposing || composing) return;
      if (!el.historyPanel.hidden) {
        event.preventDefault();
        setHistoryOpen(false, false);
        return;
      }
      if (el.moreMenu.open) {
        event.preventDefault();
        el.moreMenu.open = false;
        el.moreMenu.querySelector("summary").focus();
        return;
      }
      if (document.activeElement === el.composer) {
        event.preventDefault();
        el.composer.blur();
        return;
      }
      event.preventDefault();
      void hideWindow();
    });
    window.addEventListener("resize", resizeState, { passive: true });
    document.addEventListener("visibilitychange", () => {
      if (!state) return;
      if (document.visibilityState === "hidden") {
        saveAnchor();
        const key = identityOf(state);
        const cache = draftCache.get(key);
        if (cache && cache.text.length <= maxMessageChars) void flushDraft(cache.text, cache.revision, key);
        return;
      }
      restoreAnchor(readSavedAnchor(state));
      void refreshSnapshot();
    });
    window.addEventListener("pagehide", () => {
      if (draftTimer) window.clearTimeout(draftTimer);
      saveAnchor();
      if (state && el.composer.value.length <= maxMessageChars) {
        const identityKey = identityOf(state);
        const cache = draftCache.get(identityKey);
        if (cache) void queueDraftWrite(cache.text, cache.revision, identityKey);
      }
      for (const [requestId, waiter] of pending) {
        window.clearTimeout(waiter.timer);
        waiter.reject(new Error("CHAT_PAGE_CLOSED"));
        pending.delete(requestId);
      }
    });
  }

  function start() {
    bindEvents();
    if (!bridge) {
      setPageStatus("此页面需要在 FGO Pet 中打开。", true);
      el.composer.disabled = true;
      el.send.disabled = true;
      return;
    }
    bridge.addEventListener("message", receive);
    bridge.postMessage({ type: "ready" });
    post("chat.get", {}).then(result => {
      if (result && result.snapshot) applySnapshot(result.snapshot, "get");
    }).catch(error => setPageStatus(safeErrorText(error.message), true));
    resizeState();
  }

  start();
})();
