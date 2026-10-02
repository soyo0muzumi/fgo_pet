// Model Connection settings page (Web module).
// 完整、可读地呈现原 WPF ModelConnectionPage 的能力，不新增业务语义。
// 命令契约（pageId 由 root 自动注入）：
//   modelConnection.get / setDraft / setShowReasoning / test / refreshModels / save / clearKey
// 密钥只写不读：API Key 输入框为 type=password，提交前保留用户输入，但任何快照都不含密钥值。
const MARKUP = `
  <div class="mc-page" data-state="loading">
    <p class="mc-dirty" data-dirty role="status" aria-live="polite" hidden>你有未保存的更改，点“保存连接”后才会生效。</p>

    <section class="mc-card" aria-labelledby="mc-connection-heading">
      <div class="mc-card-heading">
        <h2 id="mc-connection-heading">聊天模型</h2>
        <p>连接你习惯使用的模型服务。</p>
      </div>

      <div class="mc-fields">
        <div class="mc-field" data-field-wrap="providerId">
          <label class="mc-label" for="mc-provider">模型供应商</label>
          <select id="mc-provider" class="mc-input" data-field="providerId" aria-label="模型供应商"></select>
        </div>

        <div class="mc-field" data-field-wrap="modelId">
          <label class="mc-label" for="mc-model">默认模型 ID</label>
          <div class="mc-model">
            <input id="mc-model" class="mc-input" type="text" inputmode="text" autocomplete="off"
                   data-field="modelId" aria-label="默认模型 ID" aria-describedby="mc-model-hint">
            <button type="button" class="mc-icon-button" data-model-toggle aria-label="展开可用模型列表"
                    aria-expanded="false" aria-controls="mc-model-list">▾</button>
          </div>
          <p class="mc-field-hint" id="mc-model-hint" data-model-hint></p>
          <div class="mc-model-list" id="mc-model-list" data-model-list hidden role="listbox" aria-label="可用模型"></div>
        </div>
      </div>

      <div class="mc-field">
        <label class="mc-label" for="mc-base">Base URL</label>
        <input id="mc-base" class="mc-input" type="text" inputmode="url" autocomplete="off"
               data-field="baseUrl" aria-label="Base URL">
      </div>

      <div class="mc-field">
        <label class="mc-label" for="mc-key">API Key</label>
        <div class="mc-key">
          <input id="mc-key" class="mc-input" type="password" autocomplete="new-password" data-field="apiKey"
                 aria-label="API Key" aria-describedby="mc-key-state">
          <button type="button" class="mc-button" data-clear-key>清除已保存密钥</button>
        </div>
        <p class="mc-field-hint" id="mc-key-state" data-key-state></p>
      </div>
    </section>

    <section class="mc-card" aria-labelledby="mc-context-heading">
      <div class="mc-card-heading">
        <h2 id="mc-context-heading">上下文与输出</h2>
        <p>控制发送给模型的上下文与生成长度。</p>
      </div>
      <div class="mc-field">
        <label class="mc-label" for="mc-context-override">上下文上限（tokens，可留空使用模型资料）</label>
        <input id="mc-context-override" class="mc-input" type="text" inputmode="numeric" autocomplete="off"
               data-field="contextWindowOverride" aria-label="上下文上限覆盖" aria-describedby="mc-numeric-hint">
      </div>
      <div class="mc-field">
        <label class="mc-label" for="mc-max-output">最大输出（tokens）</label>
        <input id="mc-max-output" class="mc-input" type="text" inputmode="numeric" autocomplete="off"
               data-field="maxOutputTokens" aria-label="最大输出" aria-describedby="mc-numeric-hint">
      </div>
      <p class="mc-field-hint" data-numeric-hint></p>
      <p class="mc-context-limit" data-context-limit></p>
    </section>

    <section class="mc-card" aria-label="显示模型思考过程">
      <label class="mc-switch-row">
        <span class="mc-switch-copy">
          <span class="mc-switch-title">显示模型思考过程</span>
          <span class="mc-switch-description">对话窗口在生成回复时展示模型的思考流；关闭后仅显示生成状态。</span>
        </span>
        <input type="checkbox" data-field="showReasoning" aria-label="显示模型思考过程">
        <span class="mc-switch-control" aria-hidden="true"><i></i></span>
        <span class="mc-switch-flag" data-reasoning-flag aria-live="polite"></span>
      </label>
    </section>

    <p class="mc-offline-note">未连接模型时，桌宠与本地专注仍可离线使用。</p>

    <div class="mc-actions">
      <button type="button" class="mc-button" data-test>测试连接</button>
      <button type="button" class="mc-button mc-button--primary" data-save>保存连接</button>
    </div>
    <p class="mc-test-note">测试连接不会保存修改。</p>

    <div class="mc-feedback" aria-live="polite" aria-atomic="true">
      <p class="mc-status" role="status" data-status></p>
      <p class="mc-error" role="alert" aria-live="assertive" data-error></p>
      <button type="button" class="mc-button mc-retry" data-retry hidden>重试</button>
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

  let disposed = false;
  let mode = "loading";
  let snapshot = null;
  let localError = "";
  let styleError = "";
  let dirty = false;
  let pickerOpen = false;
  let retryCommand = null;
  let pendingSave = null;

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
    retry.textContent = retryCommand ? "重试此更改" : "重试读取";
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
    status.textContent = snapshot.statusText;
    error.textContent = localError || snapshot.errorText || styleError;

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
      ? `当前模型：${snapshot.modelStatusText}` : "";
    buildModelList();

    baseInput.value = snapshot.baseUrl;
    contextOverride.value = snapshot.contextWindowOverrideText;
    maxOutput.value = snapshot.maxOutputTokensText;
    contextLimit.textContent = snapshot.contextLimitText;
    numericHint.textContent = !positiveIntegerText(snapshot.contextWindowOverrideText) || !positiveIntegerText(snapshot.maxOutputTokensText)
      ? "上下文上限与最大输出须为正整数；留空表示使用模型资料。此提示仅为格式提醒，最终校验以后端为准。"
      : "";

    keyState.textContent = snapshot.keyStateText;
    showReasoning.checked = snapshot.showReasoning;
    updateReasoningFlag();
  }

  // 即时保存语义只在这里陈述一次；开关说明行只描述行为本身，避免同一句话出现在两处。
  function updateReasoningFlag() {
    reasoningFlag.textContent = "（更改后立即保存，不等保存连接）";
  }

  function buildModelList() {
    modelList.replaceChildren();
    if (!snapshot || snapshot.availableModels.length === 0) {
      const empty = document.createElement("p");
      empty.className = "mc-model-empty";
      empty.textContent = "尚未获取模型。点“测试连接”或“刷新”获取供应商模型。";
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
    } catch {
      if (disposed) return;
      localError = "模型服务设置读取失败，请重试。";
      retryCommand = null;
      if (snapshot) renderSnapshot();
      else error.textContent = localError;
      setMode("read-error");
    }
  }

  async function sendCommand(type, payload = {}) {
    if (disposed || mode !== "ready") return;
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
      if (type === "modelConnection.clearKey") keyInput.value = "";
      renderSnapshot();
      renderDirty();
      setMode("ready");
    } catch (caught) {
      if (disposed) return;
      const message = "操作未完成，已回读当前状态。你可以重试。";
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
      ? "上下文上限与最大输出须为正整数；留空表示使用模型资料。此提示仅为格式提醒，最终校验以后端为准。"
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
    if (retryCommand) void sendCommand(retryCommand.type, retryCommand.payload);
    else void refreshSnapshot();
  });

  const unsubscribeTheme = typeof context?.onThemeChanged === "function"
    ? context.onThemeChanged(() => { if (!disposed && mode === "ready") void refreshSnapshot(); })
    : () => {};

  function getState() {
    return {
      dirty,
      pickerOpen,
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
    styleError = "模型服务页面样式加载失败，请重新打开页面。";
    error.textContent = styleError;
  }
  void refreshSnapshot(true);
  return dispose;
}
