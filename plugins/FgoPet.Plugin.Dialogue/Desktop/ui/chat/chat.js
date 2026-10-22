(() => {
  "use strict";

  const t = window.MarginI18n.t;
  const bridge = window.chrome && window.chrome.webview;
  const maxMessageChars = 12000;
  const draftDelayMs = 140;
  const requestTimeoutMs = 20000;
  const pending = new Map();
  const turnNodes = new Map();
  let inputCard = null;
  const draftCache = new Map();
  const acceptedDrafts = new Map();
  let requestSerial = 0;
  let localDraftRevision = 0;
  let draftTimer = 0;
  let draftQueue = Promise.resolve();
  let submitInProgress = false;
  let imageImporting = false;
  let stopInProgress = false;
  let copyFeedback = null;
  let copyTimer = 0;
  let tooltipTarget = null;
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
    contextBar: $("context-bar"),
    chips: $("context-chips"),
    composer: $("composer"),
    composerCard: $("composer-card"),
    composerFeedback: $("composer-feedback"),
    tooltip: $("chat-tooltip"),
    composerError: $("composer-error"),
    composerCount: $("composer-count"),
    send: $("send-message"),
    stop: $("stop-generation"),
    chooseWorkspace: $("choose-workspace"),
    chooseImage: $("choose-image"), imageSelection: $("image-selection"), imageLabel: $("image-label"),
    clearImage: $("clear-image"),
    workspaceSelection: $("workspace-selection"),
    workspaceLabel: $("workspace-label"),
    clearWorkspace: $("clear-workspace"),
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
      case "CHAT_IDENTITY_STALE": return t("chat.this.conversation.has.changed.please.try.again");
      case "CHAT_TARGET_INVALID": return t("chat.the.selection.has.changed.refresh.and.try.again");
      case "CHAT_DRAFT_TOO_LONG": return t("chat.inputLimit", {limit:maxMessageChars.toLocaleString("en-US")});
      case "CHAT_NOT_AVAILABLE": return t("chat.this.action.is.currently.unavailable");
      case "CHAT_COMMAND_INVALID": return t("chat.the.action.could.not.be.completed.try.again");
      case "CHAT_INPUT_STALE": return t("chat.input.changed");
      case "CHAT_INPUT_EXPIRED": return t("chat.input.expired");
      case "CHAT_INPUT_INVALID": return t("chat.input.complete");
      default: return t("chat.chat.could.not.complete.this.action.try.again.later");
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
      }, type === "chat.host" && ["chooseWorkspace", "chooseImage", "configureBrowserImages"].includes(payload.action) ? 600000 : requestTimeoutMs);
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

  function anchorContentTop(node) {
    // Entry transforms are visual motion; only layout positions belong in the reading anchor.
    const layoutTop = element => {
      let top = 0;
      for (let current = element; current; current = current.offsetParent) top += current.offsetTop;
      return top;
    };
    return layoutTop(node) - layoutTop(el.scroll);
  }

  function currentAnchor() {
    const atBottom = el.scroll.scrollHeight - el.scroll.scrollTop - el.scroll.clientHeight <= 42;
    if (atBottom) return { atBottom: true, turnId: "", offset: 0 };
    let anchor = null;
    for (const child of el.messages.children) {
      const top = anchorContentTop(child) - el.scroll.scrollTop;
      if (top + child.offsetHeight > 1) {
        anchor = { atBottom: false, turnId: child.dataset.turnId || "", offset: top };
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
      el.scroll.scrollTo({ top: el.scroll.scrollHeight, behavior: "instant" });
      followingLatest = true;
      el.jumpBottom.hidden = true;
      return;
    }
    const node = anchor.turnId ? turnNodes.get(anchor.turnId)?.article : null;
    if (node && node.isConnected) {
      el.scroll.scrollTo({ top: anchorContentTop(node) - anchor.offset, behavior: "instant" });
    }
    followingLatest = false;
    el.jumpBottom.hidden = el.scroll.scrollHeight - el.scroll.scrollTop - el.scroll.clientHeight <= 42;
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
    if (previousKey !== incomingKey) {
      if (copyTimer) window.clearTimeout(copyTimer);
      copyFeedback = null;
      hideTooltip();
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
    fitComposer();
    // Restore after this render's layout read, before another user scroll can occur.
    // A deferred frame could replay a stale anchor over newer user interaction.
    restoreAnchor(preservedAnchor);
    saveAnchor(snapshot);
    if (source === "event" && document.visibilityState === "hidden") setPageStatus(t("chat.new.messages"));
  }

  function syncComposerFromSnapshot(snapshot, identityChanged) {
    const draft = snapshot.draft && typeof snapshot.draft.text === "string" ? snapshot.draft : { text: "", revision: 0 };
    const cache = draftCache.get(lastIdentityKey);
    if (cache && cache.revision > draft.revision) {
      if (identityChanged || el.composer.value !== cache.text) el.composer.value = cache.text;
      showComposerError(cache.text.length > maxMessageChars ? t("chat.inputLimit", {limit:maxMessageChars.toLocaleString("en-US")}) : "");
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
      article.className = `turn ${role === "assistant" ? "her" : "user"}`;
      article.dataset.turnId = turn.id;
      article.setAttribute("data-role", role);
      record = { role, article, heading: null, body: null, reasoning: null, actions: null, streaming: null };
      if (role === "assistant") {
        const heading = document.createElement("div");
        heading.className = "who";

        record.heading = heading;
        article.append(heading);
      }
      const body = document.createElement("p");
      body.className = "chat-message-text";
      article.append(body);
      record.body = body;
      record.actions = document.createElement("div");
      record.actions.className = "turn-actions";
      article.append(record.actions);
      record.streaming = document.createElement("div");
      record.streaming.className = "streaming-indicator";
      record.streaming.textContent = t("chat.generating");
      record.streaming.hidden = true;
      article.append(record.streaming);
      turnNodes.set(turn.id, record);
    }
    return record;
  }

  function renderTurn(turn, presentation) {
    const record = getTurnNode(turn);
    if (record.heading) setText(record.heading, presentation.roleName || t("chat.companion"));
    setText(record.body, turn.text);

    const reasoningText = typeof turn.reasoningText === "string" ? turn.reasoningText : "";
    if (presentation.showReasoning && (reasoningText || turn.reasoningSummary)) {
      if (!record.reasoning) {
        const details = document.createElement("details");
        details.className = "think";
        const summary = document.createElement("summary");
        const text = document.createElement("div");
        text.className = "chat-message-text";
        details.append(summary, text);
        record.article.insertBefore(details, record.actions);
        record.reasoning = { details, summary, text };
        details.open = Boolean(turn.isReasoningExpanded);
      }
      const label = [turn.reasoningSummary || t("chat.reasoning"), turn.reasoningDurationText].filter(Boolean).join(" · ");
      setText(record.reasoning.summary, label);
      setText(record.reasoning.text, reasoningText);
      record.reasoning.details.hidden = false;
    } else if (record.reasoning) {
      record.reasoning.details.hidden = true;
    }

    const copyHadFocus = record.copyButton && document.activeElement === record.copyButton;
    record.actions.replaceChildren();
    if (turn.canCopy) {
      record.copyButton ||= makeButton("turn-action turn-copy", t("chat.copy.message"), "copy", "copyTurn", turn.id, t("chat.copy"));
      applyCopyFeedback(record.copyButton, turn.id);
      record.actions.append(record.copyButton);
      if (copyHadFocus) record.copyButton.focus({ preventScroll:true });
    }
    if (turn.canReadAloud) {
      const speechLabel = window.MarginI18n.speechText(turn.speechActionText) || t("chat.read.aloud");
      const readButton = makeButton("turn-action", speechLabel, "read", "readTurn", turn.id, speechLabel);
      record.actions.append(readButton);
      if (turn.speechStatusText) {
        const speechStatus = document.createElement("span");
        speechStatus.className = "turn-action-text";
        speechStatus.setAttribute("role", "status");
        setText(speechStatus, window.MarginI18n.speechText(turn.speechStatusText));
        record.actions.append(speechStatus);
      }
      if (turn.speechNeedsConfiguration) {
        const settingsButton = makeButton("turn-action", t("chat.open.speech.settings"), null, "openSpeechSettings", undefined, t("chat.speech.settings"));
        record.actions.append(settingsButton);
      }
    }
    if (turn.canOpenWorkspace) record.actions.append(makeButton("turn-action", t("chat.open.linked.item"), "folder", "openWorkspace", turn.id, t("chat.view.item")));
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
    const questionCard = renderInputCard(snapshot.conversation);
    if (questionCard) ordered.push(questionCard);
    for (const [id, record] of turnNodes) {
      if (!wanted.has(id)) {
        record.article.remove();
        turnNodes.delete(id);
      }
    }
    // Keep admitted turns mounted: detaching the whole thread restarts the v5
    // entry animation on every snapshot and shifts the saved reading anchor.
    ordered.forEach((node, index) => {
      const current = el.messages.children[index];
      if (current !== node) el.messages.insertBefore(node, current || null);
    });
    el.empty.hidden = turns.length > 0;
  }

  function inputKey(input) {
    return input ? `${identityOf(state)}\u0000${input.runId}\u0000${input.requestId}\u0000${input.revision}` : "";
  }

  function renderInputCard(conversation) {
    const input = conversation && conversation.pendingInput;
    if (!input || !Array.isArray(input.questions) || input.questions.length < 1 || input.questions.length > 3) {
      inputCard?.article.remove(); inputCard = null; return null;
    }
    const key = inputKey(input);
    if (!inputCard || inputCard.key !== key) {
      inputCard?.article.remove();
      const article = document.createElement("section");
      article.id = "user-input-card"; article.className = "turn her user-input-card";
      article.setAttribute("aria-label", t("chat.input.title"));
      const heading = document.createElement("div"); heading.className = "who";
      setText(heading, t("chat.input.title")); article.append(heading);
      const fields = [];
      for (const question of input.questions) {
        const field = document.createElement("fieldset"); field.className = "input-question";
        field.dataset.questionId = question.id;
        const legend = document.createElement("legend"); setText(legend, question.text); field.append(legend);
        const options = [];
        for (const [index, label] of (question.options || []).entries()) {
          const option = document.createElement("input");
          option.type = question.allowMultiple ? "checkbox" : "radio";
          option.name = `answer-${input.requestId}-${question.id}`; option.value = String(index);
          const optionLabel = document.createElement("label"); optionLabel.className = "input-option";
          optionLabel.append(option, document.createTextNode(label)); field.append(optionLabel); options.push(option);
        }
        const textLabel = document.createElement("label"); textLabel.className = "input-text-label";
        setText(textLabel, t(options.length ? "chat.input.detail" : "chat.input.answer"));
        const textarea = document.createElement("textarea"); textarea.rows = 2; textarea.maxLength = maxMessageChars;
        textarea.className = "input-answer"; textarea.setAttribute("aria-label", textLabel.textContent);
        textLabel.append(textarea); field.append(textLabel); article.append(field);
        fields.push({ id:question.id, options, textarea, labels:question.options || [] });
      }
      const error = document.createElement("div"); error.className = "input-error"; error.setAttribute("role", "alert"); error.hidden = true;
      const actions = document.createElement("div"); actions.className = "turn-actions input-actions";
      const submit = makeButton("btn primary", t("chat.input.submit"), null, "inputSubmit", undefined, t("chat.input.submit")); submit.id = "user-input-submit";
      const cancel = makeButton("btn ghost", t("chat.cancel"), null, "inputCancel", undefined, t("chat.cancel")); cancel.id = "user-input-cancel";
      actions.append(submit, cancel); article.append(error, actions);
      inputCard = { key, article, fields, error, submit, cancel, busy:false, input };
    }
    inputCard.input = input;
    if (input.errorText) { setText(inputCard.error, input.errorText); inputCard.error.hidden = false; }
    updateInputControls(inputCard);
    return inputCard.article;
  }

  function updateInputControls(card) {
    const expiresAt = Date.parse(card.input.expiresAt);
    const expired = !Number.isFinite(expiresAt) || Date.now() >= expiresAt;
    const busy = card.busy || card.input.isSubmitting === true;
    for (const field of card.fields) {
      field.textarea.disabled = busy || expired;
      for (const option of field.options) option.disabled = busy || expired;
    }
    card.submit.disabled = busy || expired; card.cancel.disabled = busy;
    card.article.setAttribute("aria-busy", String(busy));
    if (expired) { setText(card.error, t("chat.input.expired")); card.error.hidden = false; }
  }

  async function submitInputAnswer(cancel) {
    const card = inputCard;
    if (!card || card.busy || card.input.isSubmitting || card.key !== inputKey(state?.conversation?.pendingInput)) return;
    updateInputControls(card);
    if (!cancel && card.submit.disabled) return;
    const payload = { runId:card.input.runId, requestId:card.input.requestId, revision:card.input.revision };
    if (!cancel) {
      let total = 0;
      payload.answers = card.fields.map(field => {
        const selectedOptionIndices = field.options.flatMap((option, index) => option.checked ? [index] : []);
        const text = field.textarea.value.trim() ? field.textarea.value : null;
        total += (text?.length || 0) + selectedOptionIndices.reduce((sum, index) => sum + field.labels[index].length, 0);
        return { questionId:field.id, selectedOptionIndices, text };
      });
      if (total > maxMessageChars || payload.answers.some(answer => !answer.selectedOptionIndices.length && !answer.text)) {
        setText(card.error, t(total > maxMessageChars ? "chat.input.limit" : "chat.input.complete")); card.error.hidden = false; return;
      }
    }
    const identity = identityOf(state);
    card.busy = true; card.error.hidden = true; updateInputControls(card);
    try {
      const result = await post(cancel ? "chat.input.cancel" : "chat.input.answer", appendIdentity(payload));
      if (result.snapshot) applyCommandSnapshot(result.snapshot, identity, false);
    } catch (error) {
      if (inputCard === card && identityOf(state) === identity) {
        setText(card.error, safeErrorText(error.message)); card.error.hidden = false;
      }
    } finally {
      if (inputCard === card) { card.busy = false; updateInputControls(card); }
    }
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
      setText(button, source.title || t("chat.open.source"));
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
        option.textContent = typeof choice.label === "string" ? choice.label : t("chat.untitled");
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
    const models = Array.isArray(presentation.models) ? presentation.models : [];
    appendOptions(el.model, models, "", t("chat.no.models.available"));
    el.chooseImage.hidden = !presentation.imageDisclosure;
    el.chooseImage.disabled = actions.canChooseImage !== true || conversation.isStreaming === true || conversation.hasPendingRun === true;
    el.imageLabel.textContent = typeof presentation.imageLabel === "string" ? presentation.imageLabel : "";
    el.imageSelection.hidden = !el.imageLabel.textContent;
    el.clearImage.disabled = actions.canClearImage !== true;
    el.imageSelection.title = typeof presentation.imageDisclosure === "string" ? presentation.imageDisclosure : "";
    el.chooseWorkspace.hidden = actions.canChooseWorkspace !== true && !presentation.workspaceLabel;
    el.chooseWorkspace.disabled = actions.canChooseWorkspace !== true || conversation.isStreaming === true;
    el.workspaceLabel.textContent = typeof presentation.workspaceLabel === "string" ? presentation.workspaceLabel : "";
    el.workspaceSelection.hidden = !el.workspaceLabel.textContent;
    el.clearWorkspace.disabled = actions.canClearWorkspace !== true || conversation.isStreaming === true;
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
      const remove = makeButton("chip-remove", t("chat.removeContext", {label:chip.label || t("chat.this.item")}), "close", "removeContextChip", chip.id);
      remove.setAttribute("aria-label", t("chat.removeContext", {label:chip.label || t("chat.this.item")}));
      item.append(label, remove);
      el.chips.append(item);
    }
    el.chips.hidden = chips.length === 0;
  }

  function renderHistory(snapshot) {
    const history = snapshot.history || {};
    const items = Array.isArray(history.items) ? history.items : [];
    el.historyFilter.checked = history.onlyCurrentProject === true;
    setText(el.historyStatus, history.status || (history.isLoading ? t("chat.loading.conversations") : items.length === 0 ? t("chat.no.conversations.yet") : ""));
    el.historyRetry.hidden = history.isLoading === true || items.length > 0;
    el.historyRetry.disabled = history.isLoading === true;
    el.historyMore.hidden = history.hasMore !== true;
    el.historyMore.disabled = history.isLoading === true;
    el.historyMore.textContent = history.isLoading ? t("chat.loading") : t("chat.load.more");
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
      setText(title, item.title || t("chat.new.conversation"));
      const meta = document.createElement("span");
      meta.className = "history-item-meta";
      setText(meta, [item.updatedText, item.status].filter(Boolean).join(" · "));
      open.append(title, meta);
      row.append(open);
      if (item.conversationId === pendingId) {
        const confirmation = document.createElement("div");
        confirmation.className = "delete-confirmation";
        confirmation.setAttribute("role", "group");
        confirmation.setAttribute("aria-label", t("chat.confirm.conversation.deletion"));
        const prompt = document.createElement("span");
        prompt.textContent = t("chat.deletePrompt", {title:item.title || t("chat.new.conversation")});
        const confirm = document.createElement("button");
        confirm.type = "button";
        confirm.className = "confirm-delete";
        confirm.dataset.action = "confirmDelete";
        confirm.dataset.targetId = pendingId;
        confirm.textContent = t("chat.delete");
        confirm.disabled = history.canDelete !== true;
        const cancel = document.createElement("button");
        cancel.type = "button";
        cancel.dataset.action = "cancelDelete";
        cancel.textContent = t("chat.cancel");
        confirmation.append(prompt, confirm, cancel);
        row.append(confirmation);
      } else {
        const remove = makeButton("history-delete", t("chat.delete.conversation"), "delete", "requestDelete", item.conversationId, t("chat.delete"));
        remove.disabled = Boolean(pendingId) || history.canDelete !== true;
        row.append(remove);
      }
      el.historyItems.append(row);
    }
    if (pendingId && !pendingVisible) {
      setText(el.historyStatus, t("chat.the.conversation.is.no.longer.in.this.list"));
      const cancel = document.createElement("button");
      cancel.type = "button";
      cancel.className = "text-button";
      cancel.dataset.action = "cancelDelete";
      cancel.textContent = t("chat.cancel.deletion");
      el.historyItems.append(cancel);
    }
  }

  const hostActionLabels = [
    ["canOpenFocus", "openFocus", t("chat.open.focus")],
    ["canOpenWorkspaceOverview", "openWorkspaceOverview", t("chat.open.notes")],
    ["canCreateWorkspaceItem", "newWorkspaceItem", t("chat.new.note")],
    ["canOpenPersonalizationSettings", "openPersonalizationSettings", t("chat.companion.settings")],
    ["canOpenSpeechSettings", "openSpeechSettings", t("chat.speech.settings")],
    ["canOpenModelSettings", "openModelSettings", t("chat.model.settings")],
    ["canConfigureBrowserImages", "configureBrowserImages", t("chat.browser.images")]
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
    el.moreMenu.hidden = false;
  }

  function renderSnapshot(snapshot) {
    const presentation = snapshot.presentation || {};
    const conversation = snapshot.conversation || {};
    const history = snapshot.history || {};
    const roleName = typeof presentation.roleName === "string" && presentation.roleName.trim() ? presentation.roleName : t("chat.chat");
    setText(el.roleName, roleName);
    el.window.classList.toggle("is-expanded", window.innerWidth >= 720);
    const expanded = window.innerWidth >= 720;
    el.expand.setAttribute("aria-pressed", String(expanded));
    el.expand.setAttribute("aria-label", expanded ? t("chat.compact.window") : t("chat.expand.window"));
    el.expand.title = expanded ? t("chat.compact.window") : t("chat.expand.window");
    const expandUse = el.expand.querySelector("use");
    expandUse.setAttribute("href", expanded ? "#icon-collapse" : "#icon-expand");
    el.historyToggle.setAttribute("aria-expanded", String(!el.historyPanel.hidden));

    setText(el.providerStatus, conversation.providerStatusText);
    setText(el.modelStatus, conversation.modelStatusText);
    const status = conversation.isThinking
      ? [conversation.thinkingTimerText || t("chat.thinking"), conversation.requestStatusText].filter(Boolean).join(" · ")
      : conversation.requestStatusText || "";
    setText(el.conversationStatus, status);
    el.conversationStatus.hidden = !status;
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

    renderMessages(snapshot);
    renderSources(conversation);
    renderContextChips(conversation);
    renderChoices(snapshot);
    el.contextBar.hidden = el.chooseWorkspace.hidden && el.workspaceSelection.hidden && el.imageSelection.hidden && el.chips.childElementCount === 0;
    renderHistory(snapshot);
    renderMoreActions(snapshot);
    updateComposerControls();
  }

  function showComposerError(text) {
    setText(el.composerError, text || "");
    el.composerError.hidden = !text;
    el.composer.setAttribute("aria-invalid", String(Boolean(text)));
  }

  function composerCanSend() {
    if (imageImporting) return false;
    const text = el.composer.value;
    const conversation = state && state.conversation;
    const serverText = state?.draft?.text || "";
    const localReady = Boolean(text.trim() && text !== serverText && conversation && conversation.configurationRequired !== true);
    return Boolean(conversation && (conversation.canSend || localReady) && text.trim()
      && text.length <= maxMessageChars && !conversation.isStreaming && !conversation.hasPendingRun && !submitInProgress && !stopInProgress);
  }

  function updateComposerControls() {
    const text = el.composer.value;
    const overLimit = text.length > maxMessageChars;
    const conversation = state && state.conversation;

    const streaming = Boolean(state && state.conversation && state.conversation.isStreaming);
    const stoppable = streaming || Boolean(conversation?.hasPendingRun);
    el.composerCount.textContent = text.length > maxMessageChars - 500 ? `${text.length.toLocaleString("en-US")} / ${maxMessageChars.toLocaleString("en-US")}` : "";
    el.composer.disabled = !state;
    el.send.hidden = stoppable;
    el.stop.hidden = !stoppable;
    el.send.disabled = !composerCanSend();
    el.stop.disabled = !stoppable || conversation.canStop !== true || stopInProgress;
    el.send.setAttribute("aria-label", submitInProgress ? t("chat.sending") : t("chat.send"));
    el.stop.setAttribute("aria-label", stopInProgress ? t("chat.stopping") : t("chat.stop.generation"));
    el.send.setAttribute("aria-busy", String(submitInProgress));
    el.stop.setAttribute("aria-busy", String(stopInProgress));
    setText(el.composerFeedback, submitInProgress ? t("chat.sending") : stopInProgress ? t("chat.stopping") : copyFeedback?.status === "copied" ? t("chat.copied") : copyFeedback?.status === "pending" ? t("chat.copying") : "");
    if (tooltipTarget && (tooltipTarget.hidden || tooltipTarget.disabled)) hideTooltip();
    if (el.newConversation) el.newConversation.disabled = !state || streaming;
    el.historyNew.disabled = !state || streaming;
    if (overLimit) showComposerError(t("chat.inputLimit", {limit:maxMessageChars.toLocaleString("en-US")}));
    else if (!state || !state.draft || state.draft.text !== text) {
      if (el.composerError.textContent.includes(t("chat.inputLimit", {limit:maxMessageChars.toLocaleString("en-US")}))) showComposerError("");
    }
    const disableSelections = streaming;
    el.model.disabled = disableSelections || conversation?.hasPendingRun === true || el.model.options.length === 0;
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
    if (!composerCanSend()) return;
    const text = el.composer.value;
    if (!text.trim()) return;
    const serverDraftText = state.draft && typeof state.draft.text === "string" ? state.draft.text : "";
    if (state.conversation.canSend !== true && (text === serverDraftText || state.conversation.configurationRequired === true)) return;
    if (text.length > maxMessageChars) {
      showComposerError(t("chat.inputLimit", {limit:maxMessageChars.toLocaleString("en-US")}));
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
      if (identityOf(state) !== expected) return false;
      setPageStatus("");
      return true;
    } catch (error) {
      if (identityOf(state) === expected) setPageStatus(safeErrorText(error.message));
      return false;
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

  // Grow into available space while reserving a readable thread and fixed toolbar.
  // Round the cap to complete text lines; host window geometry remains unchanged.
  function fitComposer() {
    const style = getComputedStyle(el.composer);
    const minimum = parseFloat(style.minHeight) || 34;
    const section = el.composerCard.closest(".sheet-conversation");
    const cardStyle = getComputedStyle(el.composerCard);
    const chrome = el.composerCard.getBoundingClientRect().height - el.composer.getBoundingClientRect().height
      + parseFloat(cardStyle.marginTop) + parseFloat(cardStyle.marginBottom);
    const other = Array.from(section.children).filter(node => node !== el.composerCard && !node.classList.contains("thread-viewport"))
      .reduce((height, node) => height + node.getBoundingClientRect().height, 0);
    const reserve = Math.min(160, Math.max(72, section.clientHeight * .25));
    const available = Math.max(minimum, Math.min(section.clientHeight * .55, section.clientHeight - chrome - other - reserve));
    const lineHeight = parseFloat(style.lineHeight);
    const maximum = minimum + Math.floor((available - minimum) / lineHeight) * lineHeight;
    el.composer.style.maxHeight = `${maximum}px`;
    const borders = parseFloat(style.borderTopWidth) + parseFloat(style.borderBottomWidth);
    const previousScroll = el.composer.scrollTop;
    el.composer.style.height = `${minimum}px`;
    el.composer.style.height = `${Math.min(maximum, Math.max(minimum, el.composer.scrollHeight + borders))}px`;
    el.composer.scrollTop = previousScroll;
  }

  function onInput() {
    const anchor = currentAnchor();
    localDraftRevision = Math.max(localDraftRevision, Number(state && state.draft && state.draft.revision) || 0) + 1;
    const identityKey = identityOf(state);
    if (identityKey) draftCache.set(identityKey, { text: el.composer.value, revision: localDraftRevision });
    if (el.composer.value.length <= maxMessageChars) showComposerError("");
    updateComposerControls();
    fitComposer();
    restoreAnchor(anchor);
    saveAnchor();
    scheduleDraftWrite();
  }

  function resizeState() {
    if (resizeFrame) return;
    resizeFrame = requestAnimationFrame(() => {
      resizeFrame = 0;
      const expanded = window.innerWidth >= 720;
      el.window.classList.toggle("is-expanded", expanded);
      el.expand.setAttribute("aria-pressed", String(expanded));
      el.expand.setAttribute("aria-label", expanded ? t("chat.compact.window") : t("chat.expand.window"));
      el.expand.title = expanded ? t("chat.compact.window") : t("chat.expand.window");
      el.expand.querySelector("use").setAttribute("href", expanded ? "#icon-collapse" : "#icon-expand");
      if (el.historyPanel.hidden === false) el.historyBackdrop.hidden = expanded;
      const anchor = followingLatest ? null : readSavedAnchor(state);
      fitComposer();
      restoreAnchor(anchor);
      saveAnchor();
    });
  }

  function onScroll() {
    // Preserve the new reading intent before an already queued resize frame can restore the old one.
    followingLatest = el.scroll.scrollHeight - el.scroll.scrollTop - el.scroll.clientHeight <= 42;
    saveAnchor();
    if (scrollFrame) return;
    scrollFrame = requestAnimationFrame(() => {
      scrollFrame = 0;
      el.jumpBottom.hidden = followingLatest;
    });
  }

  function hideTooltip() {
    if (tooltipTarget) {
      const ids = (tooltipTarget.getAttribute("aria-describedby") || "").split(" ").filter(id => id && id !== "chat-tooltip");
      if (ids.length) tooltipTarget.setAttribute("aria-describedby", ids.join(" "));
      else tooltipTarget.removeAttribute("aria-describedby");
    }
    tooltipTarget = null;
    el.tooltip.hidden = true;
  }

  function showTooltip(button) {
    if (button.disabled || button.hidden) return;
    hideTooltip();
    tooltipTarget = button;
    el.tooltip.textContent = button.getAttribute("aria-label") || "";
    el.tooltip.hidden = false;
    const root = el.window.getBoundingClientRect(), target = button.getBoundingClientRect();
    el.tooltip.style.left = `${Math.max(8, Math.min(root.width - el.tooltip.offsetWidth - 8, target.right - root.left - el.tooltip.offsetWidth))}px`;
    el.tooltip.style.top = `${Math.max(4, target.top - root.top - el.tooltip.offsetHeight - 6)}px`;
    button.setAttribute("aria-describedby", [button.getAttribute("aria-describedby"), "chat-tooltip"].filter(Boolean).join(" "));
  }

  function applyCopyFeedback(button, id) {
    const current = copyFeedback && copyFeedback.identity === identityOf(state) && copyFeedback.targetId === id;
    const copied = current && copyFeedback.status === "copied";
    const pendingCopy = current && copyFeedback.status === "pending";
    // Keep keyboard focus on this command while its acknowledgement is pending.
    // copyTurn rejects repeated activation; busy is distinct from unavailable.
    button.setAttribute("aria-busy", String(Boolean(pendingCopy)));
    button.classList.toggle("is-copied", Boolean(copied));
    button.setAttribute("aria-label", copied ? t("chat.copied") : pendingCopy ? t("chat.copying") : t("chat.copy.message"));
    button.removeAttribute("title");
    if (tooltipTarget === button) el.tooltip.textContent = button.getAttribute("aria-label");
    button.querySelector("use").setAttribute("href", copied ? "#icon-check" : "#icon-copy");
    setText(button.querySelector(".turn-action-text"), copied ? t("chat.copied") : pendingCopy ? t("chat.copying") : t("chat.copy"));
  }

  function refreshCopyFeedback() {
    for (const [id, record] of turnNodes) if (record.copyButton) applyCopyFeedback(record.copyButton, id);
    updateComposerControls();
  }

  async function copyTurn(targetId) {
    if (!state || !targetId || copyFeedback?.status === "pending") return;
    if (copyTimer) window.clearTimeout(copyTimer);
    const feedback = { targetId, identity:identityOf(state), status:"pending" };
    copyFeedback = feedback;
    refreshCopyFeedback();
    const succeeded = await hostAction("copyTurn", { targetId });
    if (copyFeedback !== feedback || identityOf(state) !== feedback.identity) return;
    if (!succeeded) { copyFeedback = null; refreshCopyFeedback(); return; }
    feedback.status = "copied";
    refreshCopyFeedback();
    copyTimer = window.setTimeout(() => {
      if (copyFeedback === feedback) { copyFeedback = null; refreshCopyFeedback(); }
      copyTimer = 0;
    }, 2500);
  }

  async function handleAction(action, targetId) {
    if (action === "openHistory" || action === "requestDelete" || action === "confirmDelete" || action === "cancelDelete") {
      await historyOperation(action, targetId);
      return;
    }
    if (action === "copyTurn") { await copyTurn(targetId); return; }
    const targetActions = new Set(["readTurn", "openSource", "openWorkspace", "removeContextChip"]);
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
    el.stop.addEventListener("click", async () => {
      if (!(state?.conversation?.isStreaming || state?.conversation?.hasPendingRun) || state.conversation.canStop !== true || stopInProgress) return;
      stopInProgress = true;
      updateComposerControls();
      try { await commandWithSnapshot("chat.stop", identityPayload(state), false); }
      catch { /* commandWithSnapshot presents the safe error. */ }
      finally { stopInProgress = false; updateComposerControls(); }
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
    el.chooseImage.addEventListener("click", () => { void hostAction("chooseImage"); });
    el.clearImage.addEventListener("click", () => { void hostAction("clearImage"); });
    ChatImageImport.install(el.composerCard, {
      canImport: () => Boolean(state && !el.chooseImage.disabled && !imageImporting),
      identity: () => state ? identityOf(state) : "",
      send: (type, data) => commandWithSnapshot(type, appendIdentity(data)),
      busy: value => { imageImporting = value; updateComposerControls(); },
      error: kind => showComposerError(t(kind === "unavailable" ? "chat.image.unavailable" : "chat.image.invalid"))
    });
    el.chooseWorkspace.addEventListener("click", () => { void hostAction("chooseWorkspace"); });
    el.clearWorkspace.addEventListener("click", () => { void hostAction("clearWorkspace"); });
    el.capabilityRetry.addEventListener("click", () => {
      if (state) void commandWithSnapshot("chat.retryCapability", identityPayload(state), false).catch(() => {});
    });
    el.capabilitySettings.addEventListener("click", () => { void hostAction("openModelSettings"); });
    el.model.addEventListener("change", () => { if (el.model.value) void hostAction("selectModel", { targetId: el.model.value }); });
    el.jumpBottom.addEventListener("click", () => {
      // Instant restoration avoids intermediate smooth-scroll events disabling
      // following just as new reply content arrives.
      restoreAnchor(null);
      saveAnchor();
      el.composer.focus();
    });
    el.scroll.addEventListener("scroll", onScroll, { passive: true });
    el.messages.addEventListener("click", event => {
      const button = event.target.closest("button[data-action]");
      if (!button || button.disabled) return;
      const action = button.dataset.action;
      if (action === "inputSubmit" || action === "inputCancel") { void submitInputAnswer(action === "inputCancel"); return; }
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
      if (!el.tooltip.hidden) { event.preventDefault(); hideTooltip(); return; }
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
    const tooltipSelector = ".composer-toolbar button,.turn-copy";
    document.addEventListener("pointerover", event => { const button = event.target.closest(tooltipSelector); if (button) showTooltip(button); });
    document.addEventListener("pointerout", event => { if (tooltipTarget && !tooltipTarget.contains(event.relatedTarget)) hideTooltip(); });
    document.addEventListener("focusin", event => { const button = event.target.closest(tooltipSelector); if (button) showTooltip(button); });
    document.addEventListener("focusout", event => { if (event.target === tooltipTarget) hideTooltip(); });
    window.addEventListener("resize", resizeState, { passive: true });
    // Includes history/sidebar changes and notices, not only native window resize.
    const layoutObserver = new ResizeObserver(resizeState);
    layoutObserver.observe(el.scroll);
    layoutObserver.observe(el.composer);
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
      layoutObserver.disconnect();
      if (copyTimer) window.clearTimeout(copyTimer);
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
      setPageStatus(t("chat.open.this.page.in.fgo.pet"), true);
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
