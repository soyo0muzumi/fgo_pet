const THEMES = ["System", "FgoLight", "FgoDark"];

const MARKUP = `
  <section class="theme-page" data-state="loading" aria-labelledby="theme-card-heading">
    <article class="theme-card">
      <header class="theme-card-heading">
        <div>
          <h2 id="theme-card-heading">界面外观</h2>
          <p>选择后立即应用，也可按 Windows 的浅色或深色设置自动切换。</p>
        </div>
      </header>
      <fieldset class="theme-options">
        <legend class="theme-visually-hidden">选择界面主题</legend>
        <label class="theme-choice" data-theme-choice="System">
          <input type="radio" name="theme-choice" value="System" aria-label="跟随 Windows 的浅色或深色设置">
          <span class="theme-swatch theme-swatch--system" aria-hidden="true">
            <span class="theme-system-mini theme-system-mini--light"><span class="theme-system-window"></span><small>浅色</small></span>
            <span class="theme-system-switch">↔</span>
            <span class="theme-system-mini theme-system-mini--dark"><span class="theme-system-window"></span><small>深色</small></span>
          </span>
          <span class="theme-choice-title">跟随 Windows</span>
          <span class="theme-choice-description">按 Windows 设置自动切换浅色或深色</span>
        </label>
        <label class="theme-choice" data-theme-choice="FgoLight">
          <input type="radio" name="theme-choice" value="FgoLight" aria-label="FGO Light 浅色主题">
          <span class="theme-swatch theme-swatch--light" aria-hidden="true"><i></i><i></i></span>
          <span class="theme-choice-title">浅色</span>
          <span class="theme-choice-description">浅灰色表面与紫色强调</span>
        </label>
        <label class="theme-choice" data-theme-choice="FgoDark">
          <input type="radio" name="theme-choice" value="FgoDark" aria-label="FGO Dark 深色主题">
          <span class="theme-swatch theme-swatch--dark" aria-hidden="true"><i></i><i></i></span>
          <span class="theme-choice-title">深色</span>
          <span class="theme-choice-description">深灰色表面与柔和紫色</span>
        </label>
      </fieldset>
      <p class="theme-effective" data-theme-effective></p>
      <p class="theme-status" data-theme-status role="status" aria-live="polite"></p>
      <p class="theme-error" data-theme-error role="alert" aria-live="assertive"></p>
      <button class="theme-retry" type="button" data-retry hidden>重试读取</button>
    </article>
  </section>`;

function isRecord(value) {
  return value !== null && typeof value === "object" && !Array.isArray(value);
}

function normalizeSnapshot(value) {
  if (!isRecord(value)
      || !THEMES.includes(value.selected)
      || !["FgoLight", "FgoDark"].includes(value.effective)
      || typeof value.statusText !== "string") return null;
  return {
    selected: value.selected,
    effective: value.effective,
    statusText: value.statusText,
  };
}

function bridgeErrorText(error) {
  switch (error && error.message) {
    case "WEB_SURFACE_CLOSED": return "设置窗口已关闭。";
    case "SETTINGS_PAGE_CHANGED": return "页面已切换，请重新选择主题。";
    case "SETTINGS_INVALID_INPUT": return "主题选项无效，请重新选择。";
    default: return "主题操作未能确认，请重试。";
  }
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
  stylesheet.href = new URL("./theme.css", import.meta.url).href;
  stylesheet.dataset.settingsOwner = "theme";
  container.innerHTML = MARKUP;

  let disposed = false;
  let mode = "loading";
  let snapshot = null;
  let localError = "";
  let themeRefreshPending = false;
  let refreshScheduled = false;
  let stylesheetFailed = false;
  const root = container.querySelector(".theme-page");
  const effective = container.querySelector("[data-theme-effective]");
  const status = container.querySelector("[data-theme-status]");
  const errorText = container.querySelector("[data-theme-error]");
  const retry = container.querySelector("[data-retry]");

  function render() {
    root.dataset.state = mode;
    const disabled = mode !== "ready";
    for (const input of container.querySelectorAll("input[name=theme-choice]"))
      input.disabled = disabled;
    retry.hidden = mode !== "read-error";

    if (snapshot) {
      for (const input of container.querySelectorAll("input[name=theme-choice]")) {
        input.checked = input.value === snapshot.selected;
        input.closest(".theme-choice")?.classList.toggle("is-selected", input.checked);
      }
      const label = snapshot.effective === "FgoDark" ? "深色（FGO Dark）" : "浅色（FGO Light）";
      effective.textContent = `当前生效：${label}`;
      status.textContent = snapshot.statusText;
    } else {
      effective.textContent = "";
      status.textContent = "";
    }
    errorText.textContent = localError;
  }

  async function readLatestSnapshot() {
    let current = null;
    let attempts = 0;
    do {
      themeRefreshPending = false;
      current = normalizeSnapshot(await context.request("theme.get"));
      if (!current) throw new Error("SETTINGS_INVALID_SNAPSHOT");
      attempts++;
    } while (themeRefreshPending && attempts < 3);
    return current;
  }

  function schedulePendingRefresh() {
    if (!themeRefreshPending || refreshScheduled || disposed || mode !== "ready") return;
    refreshScheduled = true;
    queueMicrotask(() => {
      refreshScheduled = false;
      if (themeRefreshPending && !disposed && mode === "ready") void refreshSnapshot();
    });
  }

  async function refreshSnapshot() {
    if (disposed || stylesheetFailed) return;
    if (mode === "busy") {
      themeRefreshPending = true;
      return;
    }

    mode = "busy";
    localError = "";
    render();
    try {
      const current = await readLatestSnapshot();
      if (disposed) return;
      snapshot = current;
      mode = "ready";
      localError = "";
    } catch {
      if (disposed) return;
      mode = "read-error";
      localError = "主题设置读取失败，请重试。";
    }
    render();
    schedulePendingRefresh();
  }

  async function selectTheme(value) {
    if (disposed || mode !== "ready" || !THEMES.includes(value)) return;
    mode = "busy";
    localError = "";
    themeRefreshPending = false;
    render();
    try {
      const selected = normalizeSnapshot(await context.request("theme.select", { theme: value }));
      if (!selected) throw new Error("SETTINGS_INVALID_SNAPSHOT");
      if (disposed) return;
      snapshot = themeRefreshPending ? await readLatestSnapshot() : selected;
      if (disposed) return;
      mode = "ready";
      localError = "";
    } catch (error) {
      if (disposed) return;
      localError = bridgeErrorText(error);
      try {
        snapshot = await readLatestSnapshot();
        if (disposed) return;
        mode = "ready";
      } catch {
        if (disposed) return;
        mode = "read-error";
        localError = "主题操作未能确认，读取当前主题也失败，请重试。";
      }
    }
    render();
    schedulePendingRefresh();
  }

  container.addEventListener("click", event => {
    const input = event.target;
    if (input instanceof HTMLInputElement && input.name === "theme-choice")
      void selectTheme(input.value);
  });

  retry.addEventListener("click", async () => {
    if (mode === "busy" || mode === "loading") return;
    if (stylesheetFailed) {
      mode = "busy";
      localError = "";
      retry.textContent = "正在重试…";
      render();
      stylesheet.href = new URL(`./theme.css?retry=${Date.now()}`, import.meta.url).href;
      stylesheetFailed = !(await waitForStylesheet(stylesheet, signal));
      if (disposed) return;
      if (stylesheetFailed) {
        mode = "read-error";
        localError = "主题页面样式加载失败，请重试。";
        retry.textContent = "重试";
        render();
        return;
      }
      mode = "read-error";
      localError = "";
      retry.textContent = "重试读取";
      render();
    }
    void refreshSnapshot();
  });

  const unsubscribeTheme = typeof context?.onThemeChanged === "function"
    ? context.onThemeChanged(() => {
      if (disposed || stylesheetFailed) return;
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
    stylesheetFailed = true;
    mode = "read-error";
    localError = "主题页面样式加载失败，请重试。";
    retry.textContent = "重试";
    render();
    return dispose;
  }

  void refreshSnapshot();
  return dispose;
}
