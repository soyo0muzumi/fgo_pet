const english = {
  "model.feedback.modelsRefreshed": "Refreshed {count} models.",
  "model.feedback.testSucceeded": "Connection succeeded (not saved) · {details}",
  "model.feedback.saved": "Saved · {details}",
  "model.feedback.notTested": "Connection not tested.",
  "model.feedback.testDraftChanged": "Test completed, but the draft changed. The result was not applied.",
  "model.feedback.refreshDraftChanged": "Refresh completed, but the draft changed. The result was not applied.",
  "model.feedback.keyCleared": "API key cleared.",
  "model.feedback.outputLimit": "Maximum output exceeds the model limit or leaves no room for input.",
  "model.feedback.invalidTest": "Connection test failed. Check the settings.",
  "model.feedback.invalidRefresh": "Model refresh failed. Check the settings.",
  "model.feedback.refreshTimeout": "Model refresh timed out. Try again.",
  "model.feedback.testTimeout": "Connection test timed out. Try again.",
  "model.feedback.refreshCredentials": "Model refresh failed. The saved credential could not be read.",
  "model.feedback.testCredentials": "Connection test failed. The saved credential could not be read.",
  "model.feedback.refreshUnavailable": "Model refresh failed. The service could not be reached.",
  "model.feedback.testUnavailable": "Connection test failed. The service could not be reached.",
  "model.dirty": "You have unsaved changes. Save to apply them.",
  "model.heading": "Model",
  "model.lead": "Connect your preferred model service.",
  "model.provider": "Provider",
  "model.model": "Model",
  "model.chooseModel": "Choose an available model",
  "model.models": "Available models",
  "model.endpoint": "Endpoint",
  "model.key": "API key",
  "model.clearKey": "Clear saved key",
  "model.context": "Context & output",
  "model.contextLead": "Set limits for model context and output.",
  "model.contextLabel": "Context window (tokens, optional)",
  "model.contextAria": "Context window override",
  "model.output": "Maximum output (tokens)",
  "model.outputAria": "Maximum output",
  "model.reasoning": "Show model reasoning",
  "model.reasoningHint": "Show reasoning while a reply is generated. When off, show only generation status.",
  "model.offline": "Your companion and local Focus remain available without a model connection.",
  "model.test": "Test connection",
  "model.save": "Save",
  "model.testHint": "Testing the connection does not save changes.",
  "model.retry": "Retry",
  "model.retryChange": "Retry change",
  "model.reload": "Reload",
  "model.numericHint": "Use positive whole numbers, or leave blank to use model metadata. The service validates these limits.",
  "model.instant": "Saved immediately; does not wait for Save.",
  "model.noModels": "No models available. Check your endpoint and API key, then reopen the list or test the connection.",
  "model.loadingModels": "Loading available models…",
  "model.loadFailed": "Model settings could not be loaded. Try again.",
  "model.notCompleted": "The action did not complete. Current settings were reloaded; you can retry.",
  "model.styleFailed": "The page style could not be loaded. Reopen the page.",
  "model.advanced": "Advanced configuration",
  "model.keySaved": "Key saved in Windows Credential Manager.",
  "model.keyNotSaved": "No key saved.",
  "model.current": "Current model: {name}"
};
const t = (key, values = {}) => (english[key] || key).replace(/\{(\w+)\}/g, (match, name) => values[name] === undefined ? match : String(values[name]));

// Translate only known owner UI feedback. Service names and unrecognized content
// remain literal text; the owner snapshot and command behavior are unchanged.
const ownerFeedbackKeys = new Map([
  ["未测试连接。", "model.feedback.notTested"],
  ["测试完成，但草稿已更改；结果未应用。", "model.feedback.testDraftChanged"],
  ["刷新完成，但草稿已更改；结果未应用。", "model.feedback.refreshDraftChanged"],
  ["API Key 已清除。", "model.feedback.keyCleared"],
  ["最大输出超出模型限制，或没有为输入留下空间。", "model.feedback.outputLimit"],
  ["测试连接失败：设置无效。", "model.feedback.invalidTest"],
  ["刷新模型失败：设置无效。", "model.feedback.invalidRefresh"],
  ["刷新模型失败：请求超时，请重试。", "model.feedback.refreshTimeout"],
  ["测试连接失败：请求超时，请重试。", "model.feedback.testTimeout"],
  ["刷新模型失败：无法读取已保存的凭据。", "model.feedback.refreshCredentials"],
  ["测试连接失败：无法读取已保存的凭据。", "model.feedback.testCredentials"],
  ["刷新模型失败：无法连接模型服务。", "model.feedback.refreshUnavailable"],
  ["测试连接失败：无法连接模型服务。", "model.feedback.testUnavailable"],
]);
function ownerFeedback(text) {
  const key = ownerFeedbackKeys.get(text);
  if (key) return t(key);
  const count = /^已刷新 ([0-9]+) 个模型。$/.exec(text);
  if (count) return t("model.feedback.modelsRefreshed", { count: count[1] });
  for (const [source, target] of [["连接成功（尚未保存） · ", "model.feedback.testSucceeded"], ["已保存 · ", "model.feedback.saved"]]) {
    if (text.startsWith(source)) return t(target, { details: text.slice(source.length) });
  }
  return text;
}

// Model Connection settings page (Web module).
// 完整、可读地呈现原 WPF ModelConnectionPage 的能力，不新增业务语义。
// 命令契约（pageId 由 root 自动注入）：
//   modelConnection.get / setDraft / setShowReasoning / test / refreshModels / save / clearKey
// 密钥只写不读：API Key 输入框为 type=password，提交前保留用户输入，但任何快照都不含密钥值。
const MARKUP = `
  <div class="mc-page" data-state="loading">
    <p class="mc-dirty" data-dirty role="status" aria-live="polite" hidden>${t("model.dirty")}</p>

    <section class="mc-card" aria-labelledby="mc-connection-heading">
      <div class="mc-card-heading">
        <h2 id="mc-connection-heading">${t("model.heading")}</h2>
        <p>${t("model.lead")}</p>
      </div>

      <div class="mc-fields">
        <div class="mc-field" data-field-wrap="providerId">
          <label class="mc-label" for="mc-provider">${t("model.provider")}</label>
          <select id="mc-provider" class="mc-input" data-field="providerId" aria-label="${t("model.provider")}"></select>
        </div>

        <div class="mc-field" data-field-wrap="modelId">
          <label class="mc-label" for="mc-model">${t("model.model")}</label>
          <div class="mc-model">
            <input id="mc-model" class="mc-input" type="text" inputmode="text" autocomplete="off"
                   data-field="modelId" aria-label="${t("model.model")}" aria-describedby="mc-model-hint">
            <button type="button" class="mc-icon-button" data-model-toggle aria-label="${t("model.chooseModel")}"
                    aria-expanded="false" aria-controls="mc-model-list"><svg viewBox="0 0 16 16" aria-hidden="true"><path d="M4 6l4 4 4-4"/></svg></button>
          </div>
          <p class="mc-field-hint" id="mc-model-hint" data-model-hint></p>
          <div class="mc-model-list" id="mc-model-list" data-model-list hidden role="listbox" aria-label="${t("model.models")}"></div>
        </div>
      </div>

      <div class="mc-field">
        <label class="mc-label" for="mc-base">${t("model.endpoint")}</label>
        <input id="mc-base" class="mc-input" type="text" inputmode="url" autocomplete="off"
               data-field="baseUrl" aria-label="${t("model.endpoint")}">
      </div>

      <div class="mc-field">
        <label class="mc-label" for="mc-key">${t("model.key")}</label>
        <div class="mc-key">
          <input id="mc-key" class="mc-input" type="password" autocomplete="new-password" data-field="apiKey"
                 aria-label="${t("model.key")}" aria-describedby="mc-key-state">
          <button type="button" class="mc-button" data-clear-key>${t("model.clearKey")}</button>
        </div>
        <p class="mc-field-hint" id="mc-key-state" data-key-state></p>
      </div>
    </section>

    <section class="mc-card" aria-labelledby="mc-context-heading">
      <div class="mc-card-heading">
        <h2 id="mc-context-heading">${t("model.context")}</h2>
        <p>${t("model.contextLead")}</p>
      </div>
      <div class="mc-field">
        <label class="mc-label" for="mc-context-override">${t("model.contextLabel")}</label>
        <input id="mc-context-override" class="mc-input" type="text" inputmode="numeric" autocomplete="off"
               data-field="contextWindowOverride" aria-label="${t("model.contextAria")}" aria-describedby="mc-numeric-hint">
      </div>
      <div class="mc-field">
        <label class="mc-label" for="mc-max-output">${t("model.output")}</label>
        <input id="mc-max-output" class="mc-input" type="text" inputmode="numeric" autocomplete="off"
               data-field="maxOutputTokens" aria-label="${t("model.outputAria")}" aria-describedby="mc-numeric-hint">
      </div>
      <p class="mc-field-hint" data-numeric-hint></p>
      <p class="mc-context-limit" data-context-limit></p>
    </section>

    <section class="mc-card" aria-label="${t("model.reasoning")}">
      <label class="mc-switch-row">
        <span class="mc-switch-copy">
          <span class="mc-switch-title">${t("model.reasoning")}</span>
          <span class="mc-switch-description">${t("model.reasoningHint")}</span>
        </span>
        <input type="checkbox" data-field="showReasoning" aria-label="${t("model.reasoning")}">
        <span class="mc-switch-control" aria-hidden="true"><i></i></span>
        <span class="mc-switch-flag" data-reasoning-flag aria-live="polite"></span>
      </label>
    </section>

    <p class="mc-offline-note">${t("model.offline")}</p>

    <div class="mc-actions">
      <button type="button" class="mc-button" data-test>${t("model.test")}</button>
      <button type="button" class="mc-button mc-button--primary" data-save>${t("model.save")}</button>
    </div>
    <p class="mc-test-note">${t("model.testHint")}</p>

    <div class="mc-feedback" aria-live="polite" aria-atomic="true">
      <p class="mc-status" role="status" data-status></p>
      <p class="mc-error" role="alert" aria-live="assertive" data-error></p>
      <button type="button" class="mc-button mc-retry" data-retry hidden>${t("model.retry")}</button>
    </div>
  </div>`;

function isRecord(value) {
  return value !== null && typeof value === "object" && !Array.isArray(value);
}

function isProvider(record) {
  return isRecord(record) && typeof record.id === "string"
    && typeof record.displayName === "string" && typeof record.defaultBaseUrl === "string";
}

function isModel(record) {
  return isRecord(record) && typeof record.id === "string" && typeof record.displayName === "string";
}

function normalizeSnapshot(value) {
  const candidate = isRecord(value) && isRecord(value.snapshot) ? value.snapshot : value;
  if (!isRecord(candidate)
      || typeof candidate.providerId !== "string"
      || !Array.isArray(candidate.providers) || !candidate.providers.every(isProvider)
      || typeof candidate.baseUrl !== "string"
      || typeof candidate.modelId !== "string"
      || !Array.isArray(candidate.availableModels) || !candidate.availableModels.every(isModel)
      || typeof candidate.contextWindowOverrideText !== "string"
      || typeof candidate.maxOutputTokensText !== "string"
      || typeof candidate.contextLimitText !== "string"
      || typeof candidate.providerStatusText !== "string"
      || typeof candidate.modelStatusText !== "string"
      || typeof candidate.keyStateText !== "string"
      || typeof candidate.isKeySaved !== "boolean"
      || typeof candidate.showReasoning !== "boolean"
      || typeof candidate.isBusy !== "boolean"
      || typeof candidate.statusText !== "string"
      || typeof candidate.errorText !== "string") return null;

  return {
    providerId: candidate.providerId,
    providers: candidate.providers.map(provider => ({ ...provider })),
    baseUrl: candidate.baseUrl,
    modelId: candidate.modelId,
    availableModels: candidate.availableModels.map(model => ({ ...model })),
    contextWindowOverrideText: candidate.contextWindowOverrideText,
    maxOutputTokensText: candidate.maxOutputTokensText,
    contextLimitText: candidate.contextLimitText,
    providerStatusText: candidate.providerStatusText,
    modelStatusText: candidate.modelStatusText,
    keyStateText: candidate.keyStateText,
    isKeySaved: candidate.isKeySaved,
    showReasoning: candidate.showReasoning,
    isBusy: candidate.isBusy,
    statusText: candidate.statusText,
    errorText: candidate.errorText,
  };
}

function waitForStylesheet(link, signal) {
  return new Promise(resolve => {
    let settled = false;
    const finish = loaded => {
      if (settled) return;
      settled = true;
      window.clearTimeout(timer);
      link.removeEventListener("load", onLoad);
      link.removeEventListener("error", onError);
      signal?.removeEventListener("abort", onAbort);
      resolve(loaded);
    };
    const onLoad = () => finish(true);
    const onError = () => finish(false);
    const onAbort = () => finish(false);
    const timer = window.setTimeout(() => finish(Boolean(link.sheet)), 3000);
    link.addEventListener("load", onLoad, { once: true });
    link.addEventListener("error", onError, { once: true });
    signal?.addEventListener("abort", onAbort, { once: true });
    if (signal?.aborted) finish(false);
    else document.head.append(link);
    if (link.sheet) finish(true);
  });
}

function positiveIntegerText(value) {
  if (typeof value !== "string" || value.trim() === "") return true;
  return /^\d+$/.test(value.trim()) && Number(value.trim()) > 0;
}

export async function mount(container, context) {
  const signal = context?.signal;
  const stylesheet = document.createElement("link");
  stylesheet.rel = "stylesheet";
  stylesheet.href = new URL("./model-connection.css", import.meta.url).href;
  stylesheet.dataset.settingsOwner = "model-connection";
  container.innerHTML = MARKUP;
  const advanced = document.createElement("details");
  advanced.className = "mc-advanced";
  advanced.open = context?.state?.advancedOpen === true;
  const advancedLabel = document.createElement("summary");
  advancedLabel.textContent = t("model.advanced");
  const advancedFields = document.createElement("div");
  advancedFields.className = "mc-advanced-fields";
  advancedFields.append(container.querySelector("#mc-base").closest(".mc-field"),
    container.querySelector("#mc-context-heading").closest("section"),
    container.querySelector('[data-field="showReasoning"]').closest("section"));
  advanced.append(advancedLabel, advancedFields);
  const actions = container.querySelector(".mc-actions");
  actions.prepend(container.querySelector("[data-save]"));
  container.querySelector(".mc-card").after(actions, container.querySelector(".mc-test-note"), advanced,
    container.querySelector(".mc-offline-note"));

  let disposed = false;
  let mode = "loading";
  let snapshot = null;
  let localError = "";
  let styleError = "";
  let dirty = false;
  let pickerOpen = false;
  let retryCommand = null;
  let pendingSave = null;
  let modelsFlight = null;
  let draftRevision = 0;
  let displayedProviderId = null;

  const root = container.querySelector(".mc-page");
  const dirtyBanner = container.querySelector("[data-dirty]");
  const status = container.querySelector("[data-status]");
  const error = container.querySelector("[data-error]");
  const retry = container.querySelector("[data-retry]");
  const providerSelect = container.querySelector("#mc-provider");
  const modelInput = container.querySelector("#mc-model");
  const modelHint = container.querySelector("[data-model-hint]");
  const modelToggle = container.querySelector("[data-model-toggle]");
  const modelList = container.querySelector("[data-model-list]");
  const baseInput = container.querySelector("#mc-base");
  const keyInput = container.querySelector("#mc-key");
  const keyState = container.querySelector("[data-key-state]");
  const clearKeyButton = container.querySelector("[data-clear-key]");
  const contextOverride = container.querySelector("#mc-context-override");
  const maxOutput = container.querySelector("#mc-max-output");
  const numericHint = container.querySelector("[data-numeric-hint]");
  const contextLimit = container.querySelector("[data-context-limit]");
  const showReasoning = container.querySelector('[data-field="showReasoning"]');
  const reasoningFlag = container.querySelector("[data-reasoning-flag]");
  const testButton = container.querySelector("[data-test]");
  const saveButton = container.querySelector("[data-save]");

  const initialState = isRecord(context?.state) ? context.state : null;

  function setMode(nextMode) {
    mode = nextMode;
    renderMode();
  }

  function renderMode() {
    root.dataset.state = mode;
    const interactive = mode === "ready";
    for (const control of container.querySelectorAll("input, select, button[data-test], button[data-save], button[data-clear-key], button[data-model-toggle]"))
      control.disabled = !interactive;
    // 密钥输入框永不因读取失败而禁用：用户必须仍能输入。
    keyInput.disabled = mode === "busy";
    retry.hidden = mode === "loading" || mode === "busy" || (!localError && !(snapshot && snapshot.errorText));
    retry.textContent = retryCommand ? t("model.retryChange") : t("model.reload");
    retry.disabled = mode === "loading" || mode === "busy";
    testButton.disabled = !interactive;
    saveButton.disabled = !interactive;
    clearKeyButton.disabled = !interactive;
  }

  function renderDirty() {
    dirtyBanner.hidden = !dirty;
  }

  function renderSnapshot() {
    if (!snapshot) return;
    if (displayedProviderId !== null && displayedProviderId !== snapshot.providerId) keyInput.value = "";
    displayedProviderId = snapshot.providerId;
    status.textContent = ownerFeedback(snapshot.statusText);
    error.textContent = localError || ownerFeedback(snapshot.errorText) || styleError;

    providerSelect.replaceChildren();
    for (const provider of snapshot.providers) {
      const option = document.createElement("option");
      option.value = provider.id;
      option.textContent = provider.displayName;
      providerSelect.append(option);
    }
    providerSelect.value = snapshot.providerId;

    modelInput.value = snapshot.modelId;
    modelHint.textContent = snapshot.modelStatusText && snapshot.modelStatusText !== snapshot.modelId
      ? t("model.current", { name: snapshot.modelStatusText }) : "";
    buildModelList();

    baseInput.value = snapshot.baseUrl;
    contextOverride.value = snapshot.contextWindowOverrideText;
    maxOutput.value = snapshot.maxOutputTokensText;
    contextLimit.textContent = snapshot.contextLimitText;
    numericHint.textContent = !positiveIntegerText(snapshot.contextWindowOverrideText) || !positiveIntegerText(snapshot.maxOutputTokensText)
      ? t("model.numericHint")
      : "";

    keyState.textContent = t("model." + (snapshot.isKeySaved ? "keySaved" : "keyNotSaved"));
    showReasoning.checked = snapshot.showReasoning;
    updateReasoningFlag();
    if (numericHint.textContent) advanced.open = true;
  }

  // 即时保存语义只在这里陈述一次；开关说明行只描述行为本身，避免同一句话出现在两处。
  function updateReasoningFlag() {
    reasoningFlag.textContent = t("model.instant");
  }

  function buildModelList() {
    modelList.replaceChildren();
    modelList.setAttribute("aria-busy", String(modelsFlight !== null));
    if (!snapshot || snapshot.availableModels.length === 0) {
      const empty = document.createElement("p");
      empty.className = "mc-model-empty";
      empty.textContent = t(modelsFlight ? "model.loadingModels" : "model.noModels");
      modelList.append(empty);
      return;
    }
    for (const model of snapshot.availableModels) {
      const item = document.createElement("button");
      item.type = "button";
      item.className = "mc-model-item";
      item.setAttribute("role", "option");
      item.dataset.modelId = model.id;
      item.textContent = model.displayName;
      item.setAttribute("aria-selected", String(model.id === snapshot.modelId));
      if (model.id === snapshot.modelId) {
        const check = document.createElement("span");
        check.textContent = "✓";
        check.setAttribute("aria-hidden", "true");
        item.append(check);
      }
      item.addEventListener("click", () => selectModel(model.id));
      modelList.append(item);
    }
  }

  function renderPicker() {
    modelList.hidden = !pickerOpen;
    modelToggle.setAttribute("aria-expanded", String(pickerOpen));
  }

  function presentFailure(message, command = null) {
    localError = message;
    retryCommand = command;
    if (snapshot) renderSnapshot();
    else error.textContent = message;
    setMode("ready");
  }

  async function readAuthoritativeSnapshot() {
    const result = await context.request("modelConnection.get");
    const current = normalizeSnapshot(result);
    if (!current) throw new Error("SETTINGS_INVALID_SNAPSHOT");
    return current;
  }

  async function refreshSnapshot(force = false) {
    if (disposed) return;
    if (mode === "busy" || (mode === "loading" && !force)) return;
    setMode("busy");
    localError = "";
    retryCommand = null;
    try {
      const current = await readAuthoritativeSnapshot();
      if (disposed) return;
      snapshot = current;
      applyInitialState();
      renderSnapshot();
      setMode("ready");
      void refreshModels();
    } catch {
      if (disposed) return;
      localError = t("model.loadFailed");
      retryCommand = null;
      if (snapshot) renderSnapshot();
      else error.textContent = localError;
      setMode("read-error");
    }
  }

  async function sendCommand(type, payload = {}) {
    if (disposed || mode !== "ready") return;
    // Any explicit action supersedes an older background catalogue response.
    draftRevision++;
    modelsFlight = null;
    setMode("busy");
    localError = "";
    retryCommand = null;
    const previousSnapshot = snapshot;
    try {
      const result = await context.request(type, payload);
      const updated = normalizeSnapshot(result);
      if (!updated) throw new Error("SETTINGS_INVALID_SNAPSHOT");
      if (disposed) return;
      snapshot = updated;
      if (type === "modelConnection.save") dirty = false;
      if (type === "modelConnection.clearKey" || type === "modelConnection.save"
        || (type === "modelConnection.setDraft" && payload.field === "providerId")) keyInput.value = "";
      renderSnapshot();
      renderDirty();
      setMode("ready");
      if (pickerOpen && type === "modelConnection.setDraft"
        && (payload.field === "providerId" || payload.field === "baseUrl")) void refreshModels();
    } catch (caught) {
      if (disposed) return;
      const message = t("model.notCompleted");
      const failedSnapshot = normalizeSnapshot(caught?.details);
      if (failedSnapshot) {
        snapshot = failedSnapshot;
        renderSnapshot();
        renderDirty();
        presentFailure(failedSnapshot.errorText || message, { type, payload });
        return;
      }
      snapshot = previousSnapshot;
      if (snapshot) renderSnapshot();
      else error.textContent = message;
      setMode("ready");
    }
  }

  async function refreshModels() {
    if (disposed || mode !== "ready" || !snapshot || modelsFlight) return;
    const flight = { revision: draftRevision, providerId: snapshot.providerId, baseUrl: snapshot.baseUrl };
    modelsFlight = flight;
    buildModelList();
    const isCurrent = () => !disposed && modelsFlight === flight && draftRevision === flight.revision;
    const applyModels = updated => {
      if (!isCurrent() || !updated || updated.providerId !== flight.providerId || updated.baseUrl !== flight.baseUrl) return;
      // Update catalogue feedback only. Do not repaint text inputs while the user
      // is typing a new endpoint/model, or change the explicitly selected model.
      snapshot = { ...snapshot, availableModels: updated.availableModels,
        contextLimitText: updated.contextLimitText, statusText: updated.statusText, errorText: updated.errorText };
      status.textContent = ownerFeedback(snapshot.statusText);
      contextLimit.textContent = snapshot.contextLimitText;
      localError = "";
      error.textContent = ownerFeedback(snapshot.errorText) || styleError;
      retryCommand = snapshot.errorText ? { type: "modelConnection.refreshModels", payload: {} } : null;
      renderMode();
    };
    try {
      const result = await context.request("modelConnection.refreshModels");
      const updated = normalizeSnapshot(result);
      if (!updated) throw new Error("SETTINGS_INVALID_SNAPSHOT");
      applyModels(updated);
    } catch (caught) {
      if (!isCurrent()) return;
      const updated = normalizeSnapshot(caught?.details);
      if (updated) applyModels(updated);
      else {
        localError = t("model.notCompleted");
        retryCommand = { type: "modelConnection.refreshModels", payload: {} };
        error.textContent = localError;
        renderMode();
      }
    } finally {
      if (modelsFlight === flight) {
        modelsFlight = null;
        if (!disposed) buildModelList();
      }
    }
  }

  function markDirty() {
    if (!dirty) {
      dirty = true;
      renderDirty();
    }
  }

  function applyInitialState() {
    if (!initialState || !snapshot) return;
    const draft = isRecord(initialState.draft) ? initialState.draft : {};
    if (typeof draft.providerId === "string" && snapshot.providers.some(p => p.id === draft.providerId))
      providerSelect.value = draft.providerId;
    if (typeof draft.baseUrl === "string") baseInput.value = draft.baseUrl;
    if (typeof draft.modelId === "string") { modelInput.value = draft.modelId; modelHint.textContent = ""; }
    if (typeof draft.contextWindowOverrideText === "string") contextOverride.value = draft.contextWindowOverrideText;
    if (typeof draft.maxOutputTokensText === "string") maxOutput.value = draft.maxOutputTokensText;
    if (typeof initialState.dirty === "boolean" && initialState.dirty) dirty = true;
    if (typeof initialState.pickerOpen === "boolean") pickerOpen = initialState.pickerOpen;
    renderPicker();
    numericHint.textContent = !positiveIntegerText(contextOverride.value) || !positiveIntegerText(maxOutput.value)
      ? t("model.numericHint")
      : "";
  }

  function selectModel(id) {
    if (disposed || mode !== "ready") return;
    modelInput.value = id;
    modelHint.textContent = "";
    setPickerOpen(false);
    markDirty();
    void sendCommand("modelConnection.setDraft", { field: "modelId", value: id });
  }

  function setPickerOpen(next) {
    pickerOpen = next;
    renderPicker();
  }

  providerSelect.addEventListener("change", () => {
    if (mode !== "ready") return;
    const value = providerSelect.value;
    if (value !== snapshot?.providerId) {
      keyInput.value = "";
      markDirty();
      void sendCommand("modelConnection.setDraft", { field: "providerId", value });
    }
  });

  modelInput.addEventListener("change", () => {
    if (mode !== "ready") return;
    const value = modelInput.value;
    if (value !== snapshot?.modelId) {
      markDirty();
      void sendCommand("modelConnection.setDraft", { field: "modelId", value });
    }
  });

  baseInput.addEventListener("change", () => {
    if (mode !== "ready") return;
    const value = baseInput.value;
    if (value !== snapshot?.baseUrl) {
      markDirty();
      void sendCommand("modelConnection.setDraft", { field: "baseUrl", value });
    }
  });

  contextOverride.addEventListener("change", () => {
    if (mode !== "ready") return;
    const value = contextOverride.value;
    if (value !== snapshot?.contextWindowOverrideText) {
      markDirty();
      void sendCommand("modelConnection.setDraft", { field: "contextWindowOverride", value });
    }
  });

  maxOutput.addEventListener("change", () => {
    if (mode !== "ready") return;
    const value = maxOutput.value;
    if (value !== snapshot?.maxOutputTokensText) {
      markDirty();
      void sendCommand("modelConnection.setDraft", { field: "maxOutputTokens", value });
    }
  });

  // 密钥只写不读：输入即提交到 pending，但值从不回显到 DOM 文本或其它控件。
  keyInput.addEventListener("input", () => {
    if (mode !== "ready") return;
    markDirty();
    void sendCommand("modelConnection.setDraft", { field: "apiKey", value: keyInput.value });
  });

  clearKeyButton.addEventListener("click", () => {
    if (mode !== "ready") return;
    void sendCommand("modelConnection.clearKey", {});
  });

  showReasoning.addEventListener("change", () => {
    if (mode !== "ready") return;
    const value = showReasoning.checked;
    // 独立即时生效，不等“保存连接”；不属于未保存草稿。
    void sendCommand("modelConnection.setShowReasoning", { value });
  });

  modelToggle.addEventListener("click", () => {
    if (mode !== "ready") return;
    setPickerOpen(!pickerOpen);
    if (pickerOpen) void refreshModels();
  });

  testButton.addEventListener("click", () => {
    if (mode !== "ready") return;
    void sendCommand("modelConnection.test", {});
  });

  saveButton.addEventListener("click", () => {
    if (mode !== "ready") return;
    void sendCommand("modelConnection.save", {});
  });

  retry.addEventListener("click", () => {
    if (mode === "busy" || mode === "loading") return;
    if (retryCommand?.type === "modelConnection.refreshModels") void refreshModels();
    else if (retryCommand) void sendCommand(retryCommand.type, retryCommand.payload);
    else void refreshSnapshot();
  });

  const unsubscribeTheme = typeof context?.onThemeChanged === "function"
    ? context.onThemeChanged(() => { if (!disposed && mode === "ready") void refreshSnapshot(); })
    : () => {};

  function getState() {
    return {
      dirty,
      pickerOpen,
      advancedOpen: advanced.open,
      // 注意：apiKey 不写入页面状态（只写不读，避免把密钥暂存到内存快照）。
      draft: {
        providerId: providerSelect.value,
        baseUrl: baseInput.value,
        modelId: modelInput.value,
        contextWindowOverrideText: contextOverride.value,
        maxOutputTokensText: maxOutput.value,
      },
    };
  }

  const dispose = () => {
    if (disposed) return;
    disposed = true;
    unsubscribeTheme();
    signal?.removeEventListener("abort", dispose);
    stylesheet.remove();
  };
  dispose.getState = getState;
  signal?.addEventListener("abort", dispose, { once: true });
  if (signal?.aborted) dispose();

  const stylesheetLoaded = await waitForStylesheet(stylesheet, signal);
  if (disposed) return dispose;
  if (!stylesheetLoaded) {
    styleError = t("model.styleFailed");
    error.textContent = styleError;
  }
  void refreshSnapshot(true);
  return dispose;
}
