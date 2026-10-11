const english = {
  "appearance.heading": "Appearance",
  "appearance.lead": "Changes apply immediately. Follow Windows to switch with your system.",
  "appearance.choose": "Choose appearance",
  "appearance.systemAria": "Follow Windows light or dark appearance",
  "appearance.system": "Follow Windows",
  "appearance.systemHint": "Switch with your Windows setting",
  "appearance.lightAria": "FGO Light appearance",
  "appearance.darkAria": "FGO Dark appearance",
  "appearance.lightHint": "Light appearance",
  "appearance.darkHint": "Dark appearance",
  "appearance.light": "Light",
  "appearance.dark": "Dark",
  "appearance.reload": "Reload",
  "appearance.closed": "Settings window closed.",
  "appearance.changed": "The page changed. Choose an appearance again.",
  "appearance.invalid": "Invalid appearance. Choose again.",
  "appearance.failed": "The appearance change could not be confirmed. Try again.",
  "appearance.readFailed": "Appearance settings could not be loaded. Try again.",
  "appearance.readAfterFailed": "The change and current appearance could not be confirmed. Try again.",
  "appearance.retrying": "Retrying…",
  "appearance.styleFailed": "The page style could not be loaded. Try again.",
  "appearance.retry": "Retry",
  "appearance.effectiveDark": "Dark (FGO Dark)",
  "appearance.effectiveLight": "Light (FGO Light)",
  "appearance.effective": "Current appearance: {name}"
};
const t = (key, values = {}) => (english[key] || key).replace(/\{(\w+)\}/g, (match, name) => values[name] === undefined ? match : String(values[name]));

const THEMES = ["System", "FgoLight", "FgoDark"];

const MARKUP = `
  <section class="theme-page" data-state="loading" aria-labelledby="theme-card-heading">
    <article class="theme-card">
      <header class="theme-card-heading">
        <div>
          <h2 id="theme-card-heading">${t("appearance.heading")}</h2>
          <p>${t("appearance.lead")}</p>
        </div>
      </header>
      <fieldset class="theme-options">
        <legend class="theme-visually-hidden">${t("appearance.choose")}</legend>
        <label class="theme-choice" data-theme-choice="System">
          <input type="radio" name="theme-choice" value="System" aria-label="${t("appearance.systemAria")}">
          <span class="theme-swatch theme-swatch--system" aria-hidden="true">
            <span class="theme-system-mini theme-system-mini--light"><span class="theme-system-window"></span><small>${t("appearance.light")}</small></span>
            <span class="theme-system-switch">↔</span>
            <span class="theme-system-mini theme-system-mini--dark"><span class="theme-system-window"></span><small>${t("appearance.dark")}</small></span>
          </span>
          <span class="theme-choice-title">${t("appearance.system")}</span>
          <span class="theme-choice-description">${t("appearance.systemHint")}</span>
        </label>
        <label class="theme-choice" data-theme-choice="FgoLight">
          <input type="radio" name="theme-choice" value="FgoLight" aria-label="${t("appearance.lightAria")}">
          <span class="theme-swatch theme-swatch--light" aria-hidden="true"><i></i><i></i></span>
          <span class="theme-choice-title">${t("appearance.light")}</span>
          <span class="theme-choice-description">${t("appearance.lightHint")}</span>
        </label>
        <label class="theme-choice" data-theme-choice="FgoDark">
          <input type="radio" name="theme-choice" value="FgoDark" aria-label="${t("appearance.darkAria")}">
          <span class="theme-swatch theme-swatch--dark" aria-hidden="true"><i></i><i></i></span>
          <span class="theme-choice-title">${t("appearance.dark")}</span>
          <span class="theme-choice-description">${t("appearance.darkHint")}</span>
        </label>
      </fieldset>
      <p class="theme-effective" data-theme-effective></p>
      <p class="theme-status" data-theme-status role="status" aria-live="polite"></p>
      <p class="theme-error" data-theme-error role="alert" aria-live="assertive"></p>
      <button class="theme-retry" type="button" data-retry hidden>${t("appearance.reload")}</button>
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
    case "WEB_SURFACE_CLOSED": return t("appearance.closed");
    case "SETTINGS_PAGE_CHANGED": return t("appearance.changed");
    case "SETTINGS_INVALID_INPUT": return t("appearance.invalid");
    default: return t("appearance.failed");
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
      const label = snapshot.effective === "FgoDark" ? t("appearance.effectiveDark") : t("appearance.effectiveLight");
      effective.textContent = t("appearance.effective", { name: label });
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
      localError = t("appearance.readFailed");
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
        localError = t("appearance.readAfterFailed");
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
      retry.textContent = t("appearance.retrying");
      render();
      stylesheet.href = new URL(`./theme.css?retry=${Date.now()}`, import.meta.url).href;
      stylesheetFailed = !(await waitForStylesheet(stylesheet, signal));
      if (disposed) return;
      if (stylesheetFailed) {
        mode = "read-error";
        localError = t("appearance.styleFailed");
        retry.textContent = t("appearance.retry");
        render();
        return;
      }
      mode = "read-error";
      localError = "";
      retry.textContent = t("appearance.reload");
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
    localError = t("appearance.styleFailed");
    retry.textContent = t("appearance.retry");
    render();
    return dispose;
  }

  void refreshSnapshot();
  return dispose;
}
