const english = {
  "companion.theme": "Appearance",
  "companion.themeHint": "Applies immediately. System appearance follows Windows.",
  "companion.chooseTheme": "Choose appearance",
  "companion.systemTheme": "Use system appearance",
  "companion.system": "System",
  "companion.systemHint": "Follow Windows appearance",
  "companion.lightTheme": "FGO Light appearance",
  "companion.lightHint": "Light appearance",
  "companion.darkTheme": "FGO Dark appearance",
  "companion.darkHint": "Dark appearance",
  "companion.character": "Current character",
  "companion.loading": "Loading…",
  "companion.characterHint": "Your current character and appearance",
  "companion.changeCharacter": "Change character",
  "companion.display": "Character size",
  "companion.displayHint": "Changes apply immediately, or when your character is next activated.",
  "companion.scale": "Size",
  "companion.presence": "Presence",
  "companion.presenceHint": "Choose how your companion appears on the desktop.",
  "companion.topmost": "Always on top",
  "companion.topmostHint": "Keep your companion above other windows",
  "companion.collapse": "Collapse inactive panels",
  "companion.collapseHint": "Close expanded panels after a period of inactivity",
  "companion.reset": "Reset preferences",
  "companion.resetHint": "Restore 50% size, always on top and automatic panel collapse. Keep your appearance and character.",
  "companion.resetButton": "Restore defaults",
  "companion.retry": "Retry",
  "companion.charactersUnavailable": "Character settings are unavailable",
  "companion.charactersUnavailableHint": "Character settings are not available in this build.",
  "companion.retryChange": "Retry change",
  "companion.retryRead": "Reload",
  "companion.noCharacter": "No character selected",
  "companion.loadFailed": "Preferences could not be loaded. Try again.",
  "companion.unconfirmed": "The result could not be confirmed. Reload before trying again.",
  "companion.saveFailed": "The change could not be saved. Current preferences were reloaded; you can retry the change.",
  "companion.styleFailed": "The page style could not be loaded. Reopen the page.",
  "companion.more": "More preferences"
};
const t = key => english[key] || key;

const SCALE_OPTIONS = [0.5, 0.6, 0.75];
const THEMES = ["System", "FgoLight", "FgoDark"];

const MARKUP = `
  <div class="pref-page" data-state="loading">
    <section class="pref-card pref-theme-card" aria-labelledby="pref-theme-heading">
      <div class="pref-card-heading">
        <div>
          <h2 id="pref-theme-heading">${t("companion.theme")}</h2>
          <p>${t("companion.themeHint")}</p>
        </div>
      </div>
      <fieldset class="pref-theme-grid" aria-describedby="pref-theme-effective">
        <legend class="pref-visually-hidden">${t("companion.chooseTheme")}</legend>
        <label class="pref-theme-choice" data-theme-choice="System">
          <input type="radio" name="pref-theme" value="System" aria-label="${t("companion.systemTheme")}">
          <span class="pref-theme-swatch pref-theme-swatch--system" aria-hidden="true"><i></i><i></i></span>
          <span class="pref-theme-name">${t("companion.system")}</span>
          <span class="pref-theme-description">${t("companion.systemHint")}</span>
        </label>
        <label class="pref-theme-choice" data-theme-choice="FgoLight">
          <input type="radio" name="pref-theme" value="FgoLight" aria-label="${t("companion.lightTheme")}">
          <span class="pref-theme-swatch pref-theme-swatch--light" aria-hidden="true"><i></i><i></i></span>
          <span class="pref-theme-name">FGO Light</span>
          <span class="pref-theme-description">${t("companion.lightHint")}</span>
        </label>
        <label class="pref-theme-choice" data-theme-choice="FgoDark">
          <input type="radio" name="pref-theme" value="FgoDark" aria-label="${t("companion.darkTheme")}">
          <span class="pref-theme-swatch pref-theme-swatch--dark" aria-hidden="true"><i></i><i></i></span>
          <span class="pref-theme-name">FGO Dark</span>
          <span class="pref-theme-description">${t("companion.darkHint")}</span>
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
        <h2 id="pref-role-heading">${t("companion.character")}</h2>
        <p class="pref-role-name" data-role-name>${t("companion.loading")}</p>
        <p class="pref-role-caption">${t("companion.characterHint")}</p>
      </div>
      <div class="pref-role-action">
        <button class="pref-button" type="button" data-role-change>${t("companion.changeCharacter")}</button>
        <p class="pref-role-migration" id="pref-role-migration" data-role-migration></p>
      </div>
    </section>

    <section class="pref-card" aria-labelledby="pref-scale-heading">
      <div class="pref-card-heading">
        <div>
          <h2 id="pref-scale-heading">${t("companion.display")}</h2>
          <p>${t("companion.displayHint")}</p>
        </div>
      </div>
      <fieldset class="pref-scale-fieldset">
        <legend>${t("companion.scale")}</legend>
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
          <h2 id="pref-behavior-heading">${t("companion.presence")}</h2>
          <p>${t("companion.presenceHint")}</p>
        </div>
      </div>
      <label class="pref-switch-row">
        <span class="pref-switch-copy"><span class="pref-switch-title">${t("companion.topmost")}</span><span class="pref-switch-description">${t("companion.topmostHint")}</span></span>
        <input type="checkbox" data-field="topmost" aria-label="${t("companion.topmost")}">
        <span class="pref-switch-control" aria-hidden="true"><i></i></span>
      </label>
      <label class="pref-switch-row">
        <span class="pref-switch-copy"><span class="pref-switch-title">${t("companion.collapse")}</span><span class="pref-switch-description">${t("companion.collapseHint")}</span></span>
        <input type="checkbox" data-field="autoCollapseExpandedPanel" aria-label="${t("companion.collapse")}">
        <span class="pref-switch-control" aria-hidden="true"><i></i></span>
      </label>
    </section>

    <section class="pref-card pref-reset-card" aria-labelledby="pref-reset-heading">
      <div>
        <h2 id="pref-reset-heading">${t("companion.reset")}</h2>
        <p>${t("companion.resetHint")}</p>
      </div>
      <button class="pref-button" type="button" data-reset>${t("companion.resetButton")}</button>
    </section>

    <div class="pref-feedback" aria-live="polite" aria-atomic="true">
      <p class="pref-status" role="status" data-status></p>
      <p class="pref-error" role="alert" aria-live="assertive" data-error></p>
      <button class="pref-button pref-retry" type="button" data-retry hidden>${t("companion.retry")}</button>
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
  if (context?.overview) {
    const more = document.createElement("details");
    more.className = "companion-more";
    const summary = document.createElement("summary");
    summary.textContent = t("companion.more");
    const resetCard = container.querySelector(".pref-reset-card");
    resetCard.before(more);
    more.append(summary, resetCard);
  }

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
    roleButton.title = t("companion.charactersUnavailable");
    migrationNote.textContent = t("companion.charactersUnavailableHint");
  }

  function renderMode() {
    root.dataset.state = mode;
    const disabled = mode !== "ready";
    for (const control of container.querySelectorAll("input, [data-reset], [data-role-change]"))
      control.disabled = disabled || (control === roleButton && !canNavigate);
    retry.hidden = mode === "loading" || mode === "busy" || (!localError && !snapshot?.errorText);
    retry.textContent = retryCommand ? t("companion.retryChange") : t("companion.retryRead");
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

    const name = snapshot.currentRole?.displayName.trim() || t("companion.noCharacter");
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
      localError = t("companion.loadFailed");
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
      localError = t("companion.unconfirmed");
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
      const message = t("companion.saveFailed");
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
    styleError = t("companion.styleFailed");
    error.textContent = styleError;
  }
  void refreshSnapshot(true);
  return dispose;
}
