// Speech (voice reading) settings page — Web module, dynamically imported by the
// unified settings root. Presents the original WPF SpeechConnectionPage capabilities
// without adding business semantics.
//
// Command contract (pageId is injected by the root, never authored here):
//   speech.get / setDraft / selectVoice / save / preview / stopPreview
//   / clearKey / importVoice / deleteVoice
//
// Security boundary: API Key input is type=password and write-only — its value is
// sent as a pending draft but never echoed back into the DOM. Voice entries only
// ever expose { id, name }; the on-disk reference audio path is never returned.
const MARKUP = `
  <div class="sc-page" data-state="loading">
    <p class="sc-dirty" data-dirty role="status" aria-live="polite" hidden>你有未保存的更改，点“保存朗读设置”后才会生效。</p>

    <section class="sc-card" aria-label="语音朗读">
      <label class="sc-switch-row">
        <span class="sc-switch-copy">
          <span class="sc-switch-title">启用角色朗读</span>
          <span class="sc-switch-description">首版只朗读已完成的角色回复；不会安装、启动或下载任何本地模型。</span>
        </span>
        <input type="checkbox" data-field="enabled" aria-label="启用角色朗读">
        <span class="sc-switch-control" aria-hidden="true"><i></i></span>
      </label>
    </section>

    <section class="sc-card" aria-labelledby="sc-provider-heading">
      <div class="sc-card-heading"><h2 id="sc-provider-heading">朗读服务</h2></div>
      <div class="sc-field">
        <label class="sc-label" for="sc-provider">朗读服务</label>
        <select id="sc-provider" class="sc-input" data-field="provider" aria-label="朗读服务"></select>
      </div>
    </section>

    <section class="sc-card" data-card="openai" aria-labelledby="sc-openai-heading">
      <div class="sc-card-heading"><h2 id="sc-openai-heading">OpenAI 风格 HTTP</h2></div>
      <div class="sc-field">
        <label class="sc-label" for="sc-openai-base">Base URL</label>
        <input id="sc-openai-base" class="sc-input" type="text" inputmode="url" autocomplete="off"
               data-field="openAiBaseUrl" aria-label="朗读 Base URL">
      </div>
      <div class="sc-field">
        <label class="sc-label" for="sc-openai-model">模型</label>
        <input id="sc-openai-model" class="sc-input" type="text" autocomplete="off"
               data-field="openAiModel" aria-label="朗读模型">
      </div>
      <div class="sc-field">
        <label class="sc-label" for="sc-openai-voice">音色（手动填写）</label>
        <input id="sc-openai-voice" class="sc-input" type="text" autocomplete="off"
               data-field="openAiVoice" aria-label="朗读音色">
      </div>
      <div class="sc-field">
        <label class="sc-label" for="sc-key">API Key</label>
        <div class="sc-key">
          <input id="sc-key" class="sc-input" type="password" autocomplete="new-password" data-field="apiKey"
                 aria-label="API Key" aria-describedby="sc-key-state">
          <button type="button" class="sc-button sc-immediate" data-clear-key aria-label="清除已保存密钥">清除密钥</button>
        </div>
        <p class="sc-field-hint" id="sc-key-state" data-key-state></p>
      </div>
    </section>

    <section class="sc-card" data-card="indextts" aria-labelledby="sc-indextts-heading">
      <div class="sc-card-heading"><h2 id="sc-indextts-heading">IndexTTS 本机服务</h2></div>
      <div class="sc-field">
        <label class="sc-label" for="sc-indextts-base">IndexTTS 地址</label>
        <input id="sc-indextts-base" class="sc-input" type="text" inputmode="url" autocomplete="off"
               data-field="indexTtsBaseUrl" aria-label="IndexTTS 地址">
      </div>
      <p class="sc-field-hint">连接你已启动的 IndexTTS WebUI。不会安装模型或启动服务。</p>
      <div class="sc-field">
        <label class="sc-label" for="sc-voice">已保存音色</label>
        <div class="sc-voice-row">
          <select id="sc-voice" class="sc-input" data-voice-select aria-label="选择参考音色"></select>
          <button type="button" class="sc-button sc-immediate" data-delete-voice hidden>删除所选副本</button>
        </div>
        <div class="sc-delete-confirm" data-delete-confirm hidden role="group" aria-label="删除确认">
          <p class="sc-delete-text" data-delete-text></p>
          <div class="sc-delete-actions">
            <button type="button" class="sc-button" data-delete-cancel>取消</button>
            <button type="button" class="sc-button sc-immediate" data-delete-confirm-btn>删除</button>
          </div>
        </div>
      </div>
      <div class="sc-field">
        <label class="sc-label" for="sc-voice-name">新音色名称</label>
        <div class="sc-voice-row">
          <input id="sc-voice-name" class="sc-input" type="text" maxlength="80" autocomplete="off"
                 data-field="voiceName" aria-label="新音色名称">
          <button type="button" class="sc-button sc-immediate" data-import-voice>导入 WAV</button>
        </div>
        <p class="sc-field-hint">使用清晰、单人、无背景音乐的参考音频（WAV，最多 20 MB）。保存本机副本，克隆时发送给本机服务；不进行模型训练。</p>
      </div>
    </section>

    <section class="sc-card" aria-labelledby="sc-play-heading">
      <div class="sc-card-heading"><h2 id="sc-play-heading">播放</h2></div>
      <div class="sc-field">
        <label class="sc-label" for="sc-rate">语速</label>
        <div class="sc-slider-row">
          <input id="sc-rate" type="range" min="0.5" max="2" step="0.1" data-field="rate" aria-label="语速">
          <span class="sc-slider-value" data-rate-value></span>
        </div>
      </div>
      <div class="sc-field">
        <label class="sc-label" for="sc-volume">音量</label>
        <div class="sc-slider-row">
          <input id="sc-volume" type="range" min="0" max="1" step="0.05" data-field="volume" aria-label="音量">
          <span class="sc-slider-value" data-volume-value></span>
        </div>
      </div>
      <label class="sc-switch-row">
        <span class="sc-switch-copy"><span class="sc-switch-title">允许自动朗读当前会话的新回复</span></span>
        <input type="checkbox" data-field="autoReadEnabled" aria-label="允许自动朗读当前会话的新回复">
        <span class="sc-switch-control" aria-hidden="true"><i></i></span>
      </label>
      <label class="sc-switch-row">
        <span class="sc-switch-copy"><span class="sc-switch-title">免打扰时不自动朗读</span></span>
        <input type="checkbox" data-field="doNotDisturb" aria-label="免打扰时不自动朗读">
        <span class="sc-switch-control" aria-hidden="true"><i></i></span>
      </label>
      <div class="sc-field">
        <label class="sc-label" for="sc-limit">自动朗读上限（每条最多字数）</label>
        <input id="sc-limit" class="sc-input sc-input--narrow" type="text" inputmode="numeric" autocomplete="off"
               data-field="autoReadLimit" aria-label="自动朗读上限">
        <p class="sc-field-hint" data-limit-hint>自动朗读默认关闭，并严格限制为当前会话、每条最多 300 字。</p>
      </div>
    </section>

    <div class="sc-actions">
      <button type="button" class="sc-button sc-immediate" data-preview>试听</button>
      <button type="button" class="sc-button" data-stop-preview>停止试听</button>
      <button type="button" class="sc-button sc-button--primary" data-save>保存朗读设置</button>
    </div>
    <p class="sc-test-note">试听会先保存待处理密钥再播放；清除密钥、导入与删除音色立即生效。</p>

    <div class="sc-feedback" aria-live="polite" aria-atomic="true">
      <p class="sc-status" role="status" data-status></p>
      <p class="sc-error" role="alert" aria-live="assertive" data-error></p>
      <button type="button" class="sc-button sc-retry" data-retry hidden>重试</button>
    </div>
  </div>`;

function isRecord(value) {
  return value !== null && typeof value === "object" && !Array.isArray(value);
}

function isProvider(record) {
  return isRecord(record) && typeof record.id === "string" && typeof record.displayName === "string";
}

function isVoice(record) {
  return isRecord(record) && typeof record.id === "string" && typeof record.name === "string";
}

function normalizeSnapshot(value) {
  const candidate = isRecord(value) && isRecord(value.snapshot) ? value.snapshot : value;
  if (!isRecord(candidate)
      || typeof candidate.enabled !== "boolean"
      || !Array.isArray(candidate.providers) || !candidate.providers.every(isProvider)
      || typeof candidate.provider !== "string"
      || typeof candidate.isOpenAiCompatible !== "boolean"
      || typeof candidate.isIndexTts !== "boolean"
      || typeof candidate.openAiBaseUrl !== "string"
      || typeof candidate.openAiModel !== "string"
      || typeof candidate.openAiVoice !== "string"
      || typeof candidate.keyStateText !== "string"
      || typeof candidate.isKeySaved !== "boolean"
      || typeof candidate.indexTtsBaseUrl !== "string"
      || !Array.isArray(candidate.voices) || !candidate.voices.every(isVoice)
      || !(candidate.selectedVoiceId === null || typeof candidate.selectedVoiceId === "string")
      || typeof candidate.voiceName !== "string"
      || typeof candidate.voiceCount !== "number"
      || typeof candidate.voiceLimit !== "number"
      || typeof candidate.autoReadEnabled !== "boolean"
      || typeof candidate.doNotDisturb !== "boolean"
      || typeof candidate.autoReadLimit !== "number"
      || typeof candidate.rate !== "number"
      || typeof candidate.volume !== "number"
      || typeof candidate.isBusy !== "boolean"
      || typeof candidate.statusText !== "string"
      || typeof candidate.errorText !== "string") return null;

  return {
    enabled: candidate.enabled,
    providers: candidate.providers.map(provider => ({ ...provider })),
    provider: candidate.provider,
    isOpenAiCompatible: candidate.isOpenAiCompatible,
    isIndexTts: candidate.isIndexTts,
    openAiBaseUrl: candidate.openAiBaseUrl,
    openAiModel: candidate.openAiModel,
    openAiVoice: candidate.openAiVoice,
    keyStateText: candidate.keyStateText,
    isKeySaved: candidate.isKeySaved,
    indexTtsBaseUrl: candidate.indexTtsBaseUrl,
    voices: candidate.voices.map(voice => ({ ...voice })),
    selectedVoiceId: candidate.selectedVoiceId,
    voiceName: candidate.voiceName,
    voiceCount: candidate.voiceCount,
    voiceLimit: candidate.voiceLimit,
    autoReadEnabled: candidate.autoReadEnabled,
    doNotDisturb: candidate.doNotDisturb,
    autoReadLimit: candidate.autoReadLimit,
    rate: candidate.rate,
    volume: candidate.volume,
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

function clampRate(value) {
  const n = Number(value);
  if (!Number.isFinite(n)) return 1.0;
  return Math.min(2, Math.max(0.5, n));
}

function clampVolume(value) {
  const n = Number(value);
  if (!Number.isFinite(n)) return 1.0;
  return Math.min(1, Math.max(0, n));
}

function formatRate(value) {
  return `语速 ${clampRate(value).toFixed(1)}x`;
}

function formatVolume(value) {
  const n = clampVolume(value);
  return `音量 ${Math.round(n * 100)}%`;
}

export async function mount(container, context) {
  const signal = context?.signal;
  const stylesheet = document.createElement("link");
  stylesheet.rel = "stylesheet";
  stylesheet.href = new URL("./speech.css", import.meta.url).href;
  stylesheet.dataset.settingsOwner = "speech";
  container.innerHTML = MARKUP;

  let disposed = false;
  let mode = "loading";
  let snapshot = null;
  let localError = "";
  let styleError = "";
  let dirty = false;
  let retryCommand = null;
  let deleting = false;

  const root = container.querySelector(".sc-page");
  const dirtyBanner = container.querySelector("[data-dirty]");
  const status = container.querySelector("[data-status]");
  const error = container.querySelector("[data-error]");
  const retry = container.querySelector("[data-retry]");

  const enabledSwitch = container.querySelector('[data-field="enabled"]');
  const providerSelect = container.querySelector("#sc-provider");
  const openAiCard = container.querySelector('[data-card="openai"]');
  const indexTtsCard = container.querySelector('[data-card="indextts"]');
  const openAiBase = container.querySelector("#sc-openai-base");
  const openAiModel = container.querySelector("#sc-openai-model");
  const openAiVoice = container.querySelector("#sc-openai-voice");
  const keyInput = container.querySelector("#sc-key");
  const keyState = container.querySelector("[data-key-state]");
  const clearKeyButton = container.querySelector("[data-clear-key]");
  const indexTtsBase = container.querySelector("#sc-indextts-base");
  const voiceSelect = container.querySelector("[data-voice-select]");
  const deleteVoiceButton = container.querySelector("[data-delete-voice]");
  const deleteConfirm = container.querySelector("[data-delete-confirm]");
  const deleteText = container.querySelector("[data-delete-text]");
  const deleteCancel = container.querySelector("[data-delete-cancel]");
  const deleteConfirmButton = container.querySelector("[data-delete-confirm-btn]");
  const voiceNameInput = container.querySelector("#sc-voice-name");
  const importVoiceButton = container.querySelector("[data-import-voice]");
  const rateInput = container.querySelector("#sc-rate");
  const rateValue = container.querySelector("[data-rate-value]");
  const volumeInput = container.querySelector("#sc-volume");
  const volumeValue = container.querySelector("[data-volume-value]");
  const autoReadSwitch = container.querySelector('[data-field="autoReadEnabled"]');
  const doNotDisturbSwitch = container.querySelector('[data-field="doNotDisturb"]');
  const limitInput = container.querySelector("#sc-limit");
  const previewButton = container.querySelector("[data-preview]");
  const stopPreviewButton = container.querySelector("[data-stop-preview]");
  const saveButton = container.querySelector("[data-save]");

  function setMode(nextMode) {
    mode = nextMode;
    renderMode();
  }

  function renderMode() {
    root.dataset.state = mode;
    const interactive = mode === "ready";
    for (const control of container.querySelectorAll("input, select, button[data-save], button[data-preview], button[data-stop-preview], button[data-clear-key], button[data-import-voice], button[data-delete-voice], button[data-delete-confirm-btn], button[data-delete-cancel]"))
      control.disabled = !interactive;
    // API Key 输入框永不因读取失败而禁用：用户必须仍能输入。
    keyInput.disabled = mode === "busy";
    // 删除确认态不受整体禁用影响：确认是页面内即时控件。
    if (deleting) {
      deleteConfirmButton.disabled = false;
      deleteCancel.disabled = false;
    }
    retry.hidden = mode === "loading" || mode === "busy" || !(localError || (snapshot && snapshot.errorText));
    retry.textContent = retryCommand ? "重试此更改" : "重试读取";
    retry.disabled = mode === "loading" || mode === "busy";
  }

  function renderDirty() {
    dirtyBanner.hidden = !dirty;
  }

  function renderProviderCards() {
    if (!snapshot) return;
    openAiCard.hidden = !snapshot.isOpenAiCompatible;
    indexTtsCard.hidden = !snapshot.isIndexTts;
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
    providerSelect.value = snapshot.provider;

    openAiBase.value = snapshot.openAiBaseUrl;
    openAiModel.value = snapshot.openAiModel;
    openAiVoice.value = snapshot.openAiVoice;
    keyState.textContent = snapshot.keyStateText;

    indexTtsBase.value = snapshot.indexTtsBaseUrl;
    renderVoices();

    voiceNameInput.value = snapshot.voiceName;

    rateInput.value = String(clampRate(snapshot.rate));
    rateValue.textContent = formatRate(snapshot.rate);
    volumeInput.value = String(clampVolume(snapshot.volume));
    volumeValue.textContent = formatVolume(snapshot.volume);

    enabledSwitch.checked = snapshot.enabled;
    autoReadSwitch.checked = snapshot.autoReadEnabled;
    doNotDisturbSwitch.checked = snapshot.doNotDisturb;
    limitInput.value = String(snapshot.autoReadLimit);

    renderProviderCards();
  }

  function renderVoices() {
    if (!snapshot) return;
    const previous = voiceSelect.value;
    voiceSelect.replaceChildren();
    const placeholder = document.createElement("option");
    placeholder.value = "";
    placeholder.textContent = "未选择音色";
    voiceSelect.append(placeholder);
    for (const voice of snapshot.voices) {
      const option = document.createElement("option");
      option.value = voice.id;
      option.textContent = voice.name;
      voiceSelect.append(option);
    }
    const selected = snapshot.voices.some(v => v.id === snapshot.selectedVoiceId) ? snapshot.selectedVoiceId : "";
    voiceSelect.value = selected;
    if (selected !== previous && deleting) exitDeleteConfirm();
    deleteVoiceButton.hidden = selected === "";
    deleteVoiceButton.disabled = mode !== "ready";
  }

  function renderDeleteConfirm() {
    if (!snapshot || !deleting) return;
    const voice = snapshot.voices.find(v => v.id === snapshot.selectedVoiceId);
    if (!voice) { exitDeleteConfirm(); return; }
    deleteText.textContent = `确认删除“${voice.name}”及其本机副本？原始导入文件不受影响。`;
    deleteVoiceButton.hidden = true;
    deleteConfirm.hidden = false;
  }

  function enterDeleteConfirm() {
    if (mode !== "ready" || !snapshot || !snapshot.selectedVoiceId) return;
    deleting = true;
    renderDeleteConfirm();
  }

  function exitDeleteConfirm() {
    deleting = false;
    deleteConfirm.hidden = true;
    deleteVoiceButton.hidden = !snapshot || snapshot.selectedVoiceId === "";
  }

  function presentFailure(message, command = null) {
    localError = message;
    retryCommand = command;
    if (snapshot) renderSnapshot();
    else error.textContent = message;
    setMode("ready");
  }

  async function readAuthoritativeSnapshot() {
    const result = await context.request("speech.get");
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
    if (deleting) exitDeleteConfirm();
    try {
      const current = await readAuthoritativeSnapshot();
      if (disposed) return;
      snapshot = current;
      renderSnapshot();
      setMode("ready");
    } catch {
      if (disposed) return;
      localError = "语音朗读设置读取失败，请重试。";
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
      if (type === "speech.save") dirty = false;
      if (type === "speech.clearKey") keyInput.value = "";
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

  // 草稿/选择类命令：不占用全局 busy，避免连续输入（如拖动滑块）时后续事件被丢弃。
  async function applyDraftCommand(type, payload = {}) {
    if (disposed) return;
    localError = "";
    try {
      const result = await context.request(type, payload);
      const updated = normalizeSnapshot(result);
      if (!updated) throw new Error("SETTINGS_INVALID_SNAPSHOT");
      if (disposed) return;
      snapshot = updated;
      renderSnapshot();
      renderDirty();
    } catch (caught) {
      if (disposed) return;
      const failedSnapshot = normalizeSnapshot(caught?.details);
      if (failedSnapshot) {
        snapshot = failedSnapshot;
        renderSnapshot();
        renderDirty();
        localError = failedSnapshot.errorText || "";
        error.textContent = localError || snapshot.errorText;
      } else {
        error.textContent = "操作未完成，已回读当前状态。你可以重试。";
      }
    }
  }

  // 四类立即副作用（试听 / 清除密钥 / 导入 / 删除）与普通草稿字段视觉区分：
  // 这些按钮带 .sc-immediate 样式（左侧强调条），与草稿输入框明显不同；保存按钮为填充主色。

  providerSelect.addEventListener("change", () => {
    if (mode !== "ready") return;
    const value = providerSelect.value;
    if (value !== snapshot?.provider) {
      markDirty();
      void applyDraftCommand("speech.setDraft", { field: "provider", value });
    }
  });

  openAiBase.addEventListener("change", () => {
    if (mode !== "ready") return;
    const value = openAiBase.value;
    if (value !== snapshot?.openAiBaseUrl) {
      markDirty();
      void applyDraftCommand("speech.setDraft", { field: "openAiBaseUrl", value });
    }
  });

  openAiModel.addEventListener("change", () => {
    if (mode !== "ready") return;
    const value = openAiModel.value;
    if (value !== snapshot?.openAiModel) {
      markDirty();
      void applyDraftCommand("speech.setDraft", { field: "openAiModel", value });
    }
  });

  openAiVoice.addEventListener("change", () => {
    if (mode !== "ready") return;
    const value = openAiVoice.value;
    if (value !== snapshot?.openAiVoice) {
      markDirty();
      void applyDraftCommand("speech.setDraft", { field: "openAiVoice", value });
    }
  });

  // 密钥只写不读：输入即提交到 pending，但值从不回显到 DOM 文本或其它控件。
  keyInput.addEventListener("input", () => {
    if (mode !== "ready") return;
    markDirty();
    void applyDraftCommand("speech.setDraft", { field: "apiKey", value: keyInput.value });
  });

  clearKeyButton.addEventListener("click", () => {
    if (mode !== "ready") return;
    void sendCommand("speech.clearKey", {});
  });

  indexTtsBase.addEventListener("change", () => {
    if (mode !== "ready") return;
    const value = indexTtsBase.value;
    if (value !== snapshot?.indexTtsBaseUrl) {
      markDirty();
      void applyDraftCommand("speech.setDraft", { field: "indexTtsBaseUrl", value });
    }
  });

  voiceSelect.addEventListener("change", () => {
    if (mode !== "ready") return;
    const value = voiceSelect.value;
    if (value === snapshot?.selectedVoiceId) return;
    if (value === "") {
      deleteVoiceButton.hidden = true;
      if (deleting) exitDeleteConfirm();
      return;
    }
    markDirty();
    if (deleting) exitDeleteConfirm();
    void applyDraftCommand("speech.selectVoice", { voiceId: value });
  });

  deleteVoiceButton.addEventListener("click", () => {
    if (mode !== "ready" || !snapshot?.selectedVoiceId) return;
    // 页面内联二段式确认：不使用 window.confirm / alert。
    enterDeleteConfirm();
  });

  deleteCancel.addEventListener("click", () => {
    if (mode !== "ready") return;
    exitDeleteConfirm();
  });

  deleteConfirmButton.addEventListener("click", () => {
    if (!snapshot?.selectedVoiceId) return;
    const voiceId = snapshot.selectedVoiceId;
    deleting = false;
    deleteConfirm.hidden = true;
    void sendCommand("speech.deleteVoice", { voiceId });
  });

  voiceNameInput.addEventListener("change", () => {
    if (mode !== "ready") return;
    const value = voiceNameInput.value;
    if (value !== snapshot?.voiceName) {
      markDirty();
      void applyDraftCommand("speech.setDraft", { field: "voiceName", value });
    }
  });

  importVoiceButton.addEventListener("click", () => {
    if (mode !== "ready") return;
    // 文件选取由宿主原生 picker 完成，命令不带路径；此处只发信令。
    void sendCommand("speech.importVoice", {});
  });

  enabledSwitch.addEventListener("change", () => {
    if (mode !== "ready") return;
    const value = enabledSwitch.checked;
    if (value !== snapshot?.enabled) {
      markDirty();
      void applyDraftCommand("speech.setDraft", { field: "enabled", value });
    }
  });

  autoReadSwitch.addEventListener("change", () => {
    if (mode !== "ready") return;
    const value = autoReadSwitch.checked;
    if (value !== snapshot?.autoReadEnabled) {
      markDirty();
      void applyDraftCommand("speech.setDraft", { field: "autoReadEnabled", value });
    }
  });

  doNotDisturbSwitch.addEventListener("change", () => {
    if (mode !== "ready") return;
    const value = doNotDisturbSwitch.checked;
    if (value !== snapshot?.doNotDisturb) {
      markDirty();
      void applyDraftCommand("speech.setDraft", { field: "doNotDisturb", value });
    }
  });

  rateInput.addEventListener("input", () => {
    if (mode !== "ready") return;
    const value = Number(rateInput.value);
    rateValue.textContent = formatRate(value);
    markDirty();
    void applyDraftCommand("speech.setDraft", { field: "rate", value });
  });

  volumeInput.addEventListener("input", () => {
    if (mode !== "ready") return;
    const value = Number(volumeInput.value);
    volumeValue.textContent = formatVolume(value);
    markDirty();
    void applyDraftCommand("speech.setDraft", { field: "volume", value });
  });

  limitInput.addEventListener("change", () => {
    if (mode !== "ready") return;
    const value = limitInput.value;
    if (value !== String(snapshot?.autoReadLimit)) {
      markDirty();
      void applyDraftCommand("speech.setDraft", { field: "autoReadLimit", value });
    }
  });

  previewButton.addEventListener("click", () => {
    if (mode !== "ready") return;
    void sendCommand("speech.preview", {});
  });

  stopPreviewButton.addEventListener("click", () => {
    if (mode !== "ready") return;
    void sendCommand("speech.stopPreview", {});
  });

  saveButton.addEventListener("click", () => {
    if (mode !== "ready") return;
    void sendCommand("speech.save", {});
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
      // 注意：apiKey 不写入页面状态（只写不读，避免把密钥暂存到内存快照）。
      draft: {
        enabled: enabledSwitch.checked,
        provider: providerSelect.value,
        openAiBaseUrl: openAiBase.value,
        openAiModel: openAiModel.value,
        openAiVoice: openAiVoice.value,
        indexTtsBaseUrl: indexTtsBase.value,
        voiceName: voiceNameInput.value,
        autoReadEnabled: autoReadSwitch.checked,
        doNotDisturb: doNotDisturbSwitch.checked,
        autoReadLimit: limitInput.value,
        rate: Number(rateInput.value),
        volume: Number(volumeInput.value),
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
    styleError = "语音朗读页面样式加载失败，请重新打开页面。";
    error.textContent = styleError;
  }
  void refreshSnapshot(true);
  return dispose;
}
