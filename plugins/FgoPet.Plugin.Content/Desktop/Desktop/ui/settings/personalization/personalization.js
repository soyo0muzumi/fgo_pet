const SCALE_OPTIONS = [0.5, 0.6, 0.75];
const THEMES = ["System", "FgoLight", "FgoDark"];

const MARKUP = `
  <div class="pref-page" data-state="loading">
    <section class="pref-card pref-theme-card" aria-labelledby="pref-theme-heading">
      <div class="pref-card-heading">
        <div>
          <h2 id="pref-theme-heading">主题</h2>
          <p>立即应用界面外观。跟随系统会随系统深浅外观变化。</p>
        </div>
      </div>
      <fieldset class="pref-theme-grid" aria-describedby="pref-theme-effective">
        <legend class="pref-visually-hidden">选择应用主题</legend>
        <label class="pref-theme-choice" data-theme-choice="System">
          <input type="radio" name="pref-theme" value="System" aria-label="跟随系统主题">
          <span class="pref-theme-swatch pref-theme-swatch--system" aria-hidden="true"><i></i><i></i></span>
          <span class="pref-theme-name">跟随系统</span>
          <span class="pref-theme-description">自动匹配系统外观</span>
        </label>
        <label class="pref-theme-choice" data-theme-choice="FgoLight">
          <input type="radio" name="pref-theme" value="FgoLight" aria-label="FGO Light 浅色主题">
          <span class="pref-theme-swatch pref-theme-swatch--light" aria-hidden="true"><i></i><i></i></span>
          <span class="pref-theme-name">FGO Light</span>
          <span class="pref-theme-description">浅色表面与紫色强调</span>
        </label>
        <label class="pref-theme-choice" data-theme-choice="FgoDark">
          <input type="radio" name="pref-theme" value="FgoDark" aria-label="FGO Dark 深色主题">
          <span class="pref-theme-swatch pref-theme-swatch--dark" aria-hidden="true"><i></i><i></i></span>
          <span class="pref-theme-name">FGO Dark</span>
          <span class="pref-theme-description">深色表面与柔和紫色</span>
        </label>
      </fieldset>
      <p class="pref-theme-effective" id="pref-theme-effective" data-theme-status></p>
    </section>

    <section class="pref-card pref-role-card" aria-labelledby="pref-role-heading">
      <div class="pref-avatar" data-role-avatar aria-hidden="true">
        <img data-role-image alt="">
        <span class="pref-avatar-fallback"><i></i><b></b></span>
      </div>
      <div class="pref-role-copy">
        <h2 id="pref-role-heading">当前角色</h2>
        <p class="pref-role-name" data-role-name>正在读取…</p>
        <p class="pref-role-caption">当前使用的角色与立绘外观</p>
      </div>
      <div class="pref-role-action">
        <button class="pref-button" type="button" data-role-change>更换角色</button>
        <p class="pref-role-migration" id="pref-role-migration" data-role-migration></p>
      </div>
    </section>

    <section class="pref-card" aria-labelledby="pref-scale-heading">
      <div class="pref-card-heading">
        <div>
          <h2 id="pref-scale-heading">桌宠显示</h2>
          <p>选择后立即调整桌宠大小；角色未激活时，会在下次激活后生效。</p>
        </div>
      </div>
      <fieldset class="pref-scale-fieldset">
        <legend>桌宠缩放</legend>
        <div class="pref-scale-options">
          <label class="pref-scale-choice"><input type="radio" name="pref-scale" value="0.5" data-field="scale"><span>50%</span></label>
          <label class="pref-scale-choice"><input type="radio" name="pref-scale" value="0.6" data-field="scale"><span>60%</span></label>
          <label class="pref-scale-choice"><input type="radio" name="pref-scale" value="0.75" data-field="scale"><span>75%</span></label>
        </div>
      </fieldset>
    </section>

    <section class="pref-card" aria-labelledby="pref-behavior-heading">
      <div class="pref-card-heading">
        <div>
          <h2 id="pref-behavior-heading">窗口行为</h2>
          <p>控制桌宠窗口的显示方式。</p>
        </div>
      </div>
      <label class="pref-switch-row">
        <span class="pref-switch-copy"><span class="pref-switch-title">桌宠窗口始终置顶</span><span class="pref-switch-description">在其他窗口前显示桌宠</span></span>
        <input type="checkbox" data-field="topmost" aria-label="桌宠窗口始终置顶">
        <span class="pref-switch-control" aria-hidden="true"><i></i></span>
      </label>
      <label class="pref-switch-row">
        <span class="pref-switch-copy"><span class="pref-switch-title">展开面板无操作时自动收起</span><span class="pref-switch-description">一段时间无操作后收起展开面板</span></span>
        <input type="checkbox" data-field="autoCollapseExpandedPanel" aria-label="展开面板无操作时自动收起">
        <span class="pref-switch-control" aria-hidden="true"><i></i></span>
      </label>
    </section>

    <section class="pref-card pref-reset-card" aria-labelledby="pref-reset-heading">
      <div>
        <h2 id="pref-reset-heading">恢复设置</h2>
        <p>将缩放恢复为 50%，并开启置顶和自动收起。主题和当前角色会保留。</p>
      </div>
      <button class="pref-button" type="button" data-reset>恢复默认</button>
    </section>

    <div class="pref-feedback" aria-live="polite" aria-atomic="true">
      <p class="pref-status" role="status" data-status></p>
      <p class="pref-error" role="alert" aria-live="assertive" data-error></p>
      <button class="pref-button pref-retry" type="button" data-retry hidden>重试</button>
    </div>
  </div>`;

function isRecord(value) {
  return value !== null && typeof value === "object" && !Array.isArray(value);
}

function isScale(value) {
  return SCALE_OPTIONS.includes(value);
}

function normalizeSnapshot(value) {
  const candidate = isRecord(value) && isRecord(value.snapshot) ? value.snapshot : value;
  if (!isRecord(candidate)
      || !isScale(candidate.scale)
      || !Array.isArray(candidate.scaleOptions)
      || candidate.scaleOptions.length !== SCALE_OPTIONS.length
      || !SCALE_OPTIONS.every((option, index) => candidate.scaleOptions[index] === option)
      || typeof candidate.topmost !== "boolean"
      || typeof candidate.autoCollapseExpandedPanel !== "boolean"
      || typeof candidate.statusText !== "string"
      || typeof candidate.errorText !== "string"
      || !isRecord(candidate.theme)
      || !THEMES.includes(candidate.theme.selected)
      || !["FgoLight", "FgoDark"].includes(candidate.theme.effective)
      || typeof candidate.theme.statusText !== "string") return null;

  const role = candidate.currentRole;
  if (role !== null && (!isRecord(role) || typeof role.displayName !== "string"
      || !(role.previewDataUrl === null || typeof role.previewDataUrl === "string"))) return null;

  return {
    scale: candidate.scale,
    scaleOptions: SCALE_OPTIONS.slice(),
    topmost: candidate.topmost,
    autoCollapseExpandedPanel: candidate.autoCollapseExpandedPanel,
    statusText: candidate.statusText,
    errorText: candidate.errorText,
    currentRole: role === null ? null : {
      displayName: role.displayName,
      previewDataUrl: role.previewDataUrl,
    },
    theme: {
      selected: candidate.theme.selected,
      effective: candidate.theme.effective,
      statusText: candidate.theme.statusText,
    },
  };
}

function safeImageDataUrl(value) {
  return typeof value === "string"
    && /^data:image\/(?:png|jpeg|webp|gif);base64,[A-Za-z0-9+/]+={0,2}$/i.test(value);
}

function disposeResult(result) {
  if (typeof result === "function") result();
  else if (result && typeof result.dispose === "function") result.dispose();
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

export async function mount(container, context) {
  const signal = context?.signal;
  const stylesheet = document.createElement("link");
  stylesheet.rel = "stylesheet";
  stylesheet.href = new URL("./personalization.css", import.meta.url).href;
  stylesheet.dataset.settingsOwner = "personalization";
  container.innerHTML = MARKUP;

  let disposed = false;
  let mode = "loading";
  let snapshot = null;
  let localError = "";
  let styleError = "";
  let retryCommand = null;
  let themeRefreshPending = false;
  const root = container.querySelector(".pref-page");
  const status = container.querySelector("[data-status]");
  const error = container.querySelector("[data-error]");
  const retry = container.querySelector("[data-retry]");
  const roleButton = container.querySelector("[data-role-change]");
  const migrationNote = container.querySelector("[data-role-migration]");
  const canNavigate = typeof context?.canNavigate === "function" && context.canNavigate("RolePackages");
  roleButton.disabled = !canNavigate;
  roleButton.setAttribute("aria-describedby", "pref-role-migration");
  if (!canNavigate) {
    roleButton.title = "角色包页面尚未迁移";
    migrationNote.textContent = "角色包页面迁移后可更换角色。";
  }

  function renderMode() {
    root.dataset.state = mode;
    const disabled = mode !== "ready";
    for (const control of container.querySelectorAll("input, [data-reset], [data-role-change]"))
      control.disabled = disabled || (control === roleButton && !canNavigate);
    retry.hidden = mode === "loading" || mode === "busy" || (!localError && !snapshot?.errorText);
    retry.textContent = retryCommand ? "重试此更改" : "重试读取";
    retry.disabled = mode === "loading" || mode === "busy";
  }

  function renderSnapshot() {
    if (!snapshot) return;
    status.textContent = snapshot.statusText;
    error.textContent = localError || snapshot.errorText || styleError;

    for (const input of container.querySelectorAll('input[name="pref-scale"]'))
      input.checked = Number(input.value) === snapshot.scale;
    for (const input of container.querySelectorAll('input[name="pref-theme"]')) {
      input.checked = input.value === snapshot.theme.selected;
      input.closest(".pref-theme-choice").classList.toggle("is-selected", input.checked);
    }
    container.querySelector('[data-theme-status]').textContent = snapshot.theme.statusText;
    container.querySelector('[data-field="topmost"]').checked = snapshot.topmost;
    container.querySelector('[data-field="autoCollapseExpandedPanel"]').checked = snapshot.autoCollapseExpandedPanel;

    const name = snapshot.currentRole?.displayName.trim() || "尚未选择角色";
    container.querySelector("[data-role-name]").textContent = name;
    const image = container.querySelector("[data-role-image]");
    const fallback = container.querySelector(".pref-avatar-fallback");
    if (safeImageDataUrl(snapshot.currentRole?.previewDataUrl)) {
      image.src = snapshot.currentRole.previewDataUrl;
      image.hidden = false;
      fallback.hidden = true;
    } else {
      image.removeAttribute("src");
      image.hidden = true;
      fallback.hidden = false;
    }
  }

  function setMode(nextMode) {
    mode = nextMode;
    renderMode();
  }

  function presentFailure(message, command = null) {
    localError = message;
    retryCommand = command;
    if (snapshot) renderSnapshot();
    else error.textContent = message;
    setMode(snapshot ? "ready" : "read-error");
  }

  async function readAuthoritativeSnapshot() {
    let current = null;
    let attempts = 0;
    do {
      themeRefreshPending = false;
      current = normalizeSnapshot(await context.request("personalization.get"));
      if (!current) throw new Error("SETTINGS_INVALID_SNAPSHOT");
      attempts++;
    } while (themeRefreshPending && attempts < 3);
    return current;
  }

  async function refreshSnapshot(force = false) {
    if (disposed) return;
    if (mode === "busy" || (mode === "loading" && !force)) {
      themeRefreshPending = true;
      return;
    }
    setMode("busy");
    localError = "";
    retryCommand = null;
    try {
      const current = await readAuthoritativeSnapshot();
      if (disposed) return;
      snapshot = current;
      renderSnapshot();
      setMode("ready");
    } catch {
      if (disposed) return;
      localError = "个性化设置读取失败，请重试。";
      retryCommand = null;
      if (snapshot) renderSnapshot();
      else error.textContent = localError;
      setMode("read-error");
    }
  }

  async function resyncAfterCommand(message, failedCommand) {
    try {
      const current = await readAuthoritativeSnapshot();
      if (disposed) return;
      snapshot = current;
      localError = current.errorText || message;
      retryCommand = failedCommand;
      renderSnapshot();
      setMode("ready");
    } catch {
      if (disposed) return;
      localError = "操作结果未能确认，请先重试读取。";
      retryCommand = null;
      if (snapshot) renderSnapshot();
      else error.textContent = localError;
      setMode("read-error");
    }
  }

  async function sendCommand(type, payload = {}) {
    if (disposed || mode !== "ready") return;
    const command = { type, payload: { ...payload } };
    setMode("busy");
    localError = "";
    retryCommand = null;
    const previousSnapshot = snapshot;
    try {
      const result = await context.request(type, payload);
      const updated = normalizeSnapshot(result);
      if (!updated) throw new Error("SETTINGS_INVALID_SNAPSHOT");
      if (disposed) return;
      if (themeRefreshPending) {
        snapshot = previousSnapshot;
        await resyncAfterCommand("", null);
        return;
      }
      snapshot = updated;
      localError = "";
      retryCommand = null;
      renderSnapshot();
      setMode("ready");
    } catch (caught) {
      if (disposed) return;
      const message = "保存失败，当前设置已回读。你可以重试此更改。";
      if (themeRefreshPending) {
        await resyncAfterCommand(message, command);
        return;
      }
      const failedSnapshot = normalizeSnapshot(caught?.details);
      if (failedSnapshot) {
        snapshot = failedSnapshot;
        presentFailure(failedSnapshot.errorText || message, command);
        return;
      }
      snapshot = previousSnapshot;
      await resyncAfterCommand(message, command);
    }
  }

  container.addEventListener("change", event => {
    if (mode !== "ready") return;
    const target = event.target;
    if (target instanceof HTMLInputElement && target.name === "pref-scale") {
      const value = Number(target.value);
      if (isScale(value) && value !== snapshot?.scale)
        void sendCommand("personalization.set", { field: "scale", value });
    } else if (target instanceof HTMLInputElement && target.name === "pref-theme") {
      if (THEMES.includes(target.value) && target.value !== snapshot?.theme.selected)
        void sendCommand("personalization.setTheme", { theme: target.value });
    } else if (target instanceof HTMLInputElement && target.dataset.field) {
      if (["topmost", "autoCollapseExpandedPanel"].includes(target.dataset.field)
          && typeof target.checked === "boolean"
          && target.checked !== snapshot?.[target.dataset.field])
        void sendCommand("personalization.set", { field: target.dataset.field, value: target.checked });
    }
  });

  container.querySelector("[data-reset]").addEventListener("click", () => {
    void sendCommand("personalization.reset", {});
  });
  roleButton.addEventListener("click", () => {
    if (canNavigate && mode === "ready") void context.navigate("RolePackages");
  });
  retry.addEventListener("click", () => {
    if (mode === "busy" || mode === "loading") return;
    if (retryCommand) void sendCommand(retryCommand.type, retryCommand.payload);
    else void refreshSnapshot();
  });

  const unsubscribeTheme = typeof context?.onThemeChanged === "function"
    ? context.onThemeChanged(() => {
      if (disposed) return;
      if (mode === "busy" || mode === "loading") themeRefreshPending = true;
      else void refreshSnapshot();
    })
    : () => {};

  const dispose = () => {
    if (disposed) return;
    disposed = true;
    unsubscribeTheme();
    signal?.removeEventListener("abort", dispose);
    stylesheet.remove();
  };
  signal?.addEventListener("abort", dispose, { once: true });
  if (signal?.aborted) dispose();

  const stylesheetLoaded = await waitForStylesheet(stylesheet, signal);
  if (disposed) return dispose;
  if (!stylesheetLoaded) {
    styleError = "个性化页面样式加载失败，请重新打开页面。";
    error.textContent = styleError;
  }
  void refreshSnapshot(true);
  return dispose;
}
