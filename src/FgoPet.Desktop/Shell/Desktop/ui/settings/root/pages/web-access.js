const MARKUP = `
  <div class="web-access-page settings-page-body" aria-busy="true">
    <p class="web-access-lead">Let Mash find public information and read pages in Chrome. Enable each service when you want to use it.</p>
    <section class="web-access-card settings-section" aria-labelledby="web-search-heading">
      <div class="web-access-heading settings-section-heading"><h2 id="web-search-heading">Web search</h2><span class="web-access-badge" data-search-state>Loading…</span></div>
      <p class="web-access-hint">Search text is sent to DuckDuckGo. No API key is needed.</p>
      <p class="web-access-status" data-search-detail role="status" aria-live="polite"></p>
      <p class="web-access-hint" data-search-checked></p>
      <div class="web-access-actions settings-actions">
        <button class="web-access-button settings-primary" data-search-toggle type="button" disabled>Enable search</button>
        <button class="web-access-button settings-secondary" data-search-test type="button" disabled>Test search</button>
      </div>
      <p class="web-access-hint">Test search sends a fixed, non-personal query and checks that the search service responds.</p>
    </section>
    <section class="web-access-card settings-section" aria-labelledby="web-browser-heading">
      <div class="web-access-heading settings-section-heading"><h2 id="web-browser-heading">Chrome</h2><span class="web-access-badge" data-browser-state>Loading…</span></div>
      <p class="web-access-hint">Allow Mash to read page text and, when needed, one rendered image or viewport per task in your connected Chrome session, including signed-in tabs. Text and screenshots may be sent to your configured model. With the local Buddy route, screenshots may be saved, re-encoded, forwarded or retried upstream; stopping cannot recall an image already sent. Keep Chrome open, enable remote debugging in Chrome, and approve its connection prompt when requested.</p>
      <p class="web-access-status" data-browser-detail role="status" aria-live="polite"></p>
      <p class="web-access-hint" data-browser-checked></p>
      <div class="web-access-actions settings-actions">
        <button class="web-access-button settings-primary" data-browser-toggle type="button" disabled>Enable browser</button>
        <button class="web-access-button settings-secondary" data-browser-test type="button" disabled>Test connection</button>
        <button class="web-access-button settings-secondary" data-browser-reconnect type="button" disabled>Reconnect</button>
      </div>
      <p class="web-access-hint" data-visual-state></p>
      <details class="web-access-advanced settings-advanced">
        <summary>Advanced connection setup</summary>
        <p class="web-access-hint">The included browser component is used by default. Change these paths only when using a separately prepared component.</p>
        <form data-browser-form aria-label="Chrome connection setup">
          <label class="settings-label" for="web-browser-node">Node executable</label>
          <input id="web-browser-node" class="web-access-input settings-input" name="nodePath" maxlength="4096" autocomplete="off" spellcheck="false" required disabled>
          <label class="settings-label" for="web-browser-server">Browser connector</label>
          <input id="web-browser-server" class="web-access-input settings-input" name="serverPath" maxlength="4096" autocomplete="off" spellcheck="false" required disabled>
          <label class="settings-label" for="web-browser-profile">Chrome profile directory <span class="web-access-hint">(optional)</span></label>
          <input id="web-browser-profile" class="web-access-input settings-input" name="userDataDirectory" maxlength="4096" autocomplete="off" spellcheck="false" disabled>
          <p class="web-access-hint" data-dirty hidden>You have unsaved changes.</p>
          <div class="web-access-actions settings-actions">
            <button class="web-access-button settings-secondary" type="submit" data-browser-save disabled>Save setup</button>
            <button class="web-access-button settings-secondary" type="button" data-browser-bundled disabled>Use included component & enable</button>
          </div>
        </form>
      </details>
    </section>
    <div class="web-access-feedback">
      <p class="web-access-status" data-feedback role="status" aria-live="polite"></p>
      <p class="web-access-error" data-error role="alert" hidden></p>
      <button class="web-access-button settings-secondary" data-reload type="button">Refresh status</button>
    </div>
  </div>`;

const ERROR_TEXT = {
  WEB_SEARCH_NOT_CONFIGURED: "Enable search to use it. No account or API key is required.",
  WEB_SEARCH_NOT_ENABLED: "Search is turned off.",
  WEB_SEARCH_PROVIDER_CHANGED: "Enable search again to authorize DuckDuckGo for this setup.",
  WEB_SEARCH_CONFIG_INVALID: "The saved search settings are invalid. Enable search to create a new grant.",
  WEB_SEARCH_CONFIG_UNREADABLE: "The search settings could not be read. Check access to the app's local data, then refresh.",
  WEB_SEARCH_CONFIG_WRITE_FAILED: "The search setting could not be saved. Check access to the app's local data and try again.",
  WEB_SEARCH_CONFIG_CHANGED: "Search settings changed in another window. Refresh before trying again.",
  WEB_SEARCH_RUNTIME_MISSING: "The search component is missing. Repair or replace this test package, then refresh.",
  WEB_SEARCH_RUNTIME_INVALID: "The search component could not start. Repair or replace this test package.",
  WEB_SEARCH_RUNTIME_FAILED: "The search component could not run. Repair or replace this test package, then test again.",
  WEB_SEARCH_NETWORK_FAILED: "Search could not reach DuckDuckGo. Check your network or proxy, then try again.",
  WEB_SEARCH_TIMEOUT: "The search request timed out. Check your connection and try again later.",
  WEB_SEARCH_RATE_LIMITED: "DuckDuckGo is limiting requests. Wait before trying again.",
  WEB_SEARCH_BLOCKED: "DuckDuckGo blocked this request. Check your connection and try again later.",
  WEB_SEARCH_RESPONSE_INVALID: "The search service returned an unexpected response. Try again later.",
  WEB_SEARCH_AUTHORIZATION_CHANGED: "Search access changed during the test. Refresh its status.",
  WEB_SEARCH_STOPPED: "Search is not running. Restart the app and try again.",
  BROWSER_NOT_CONFIGURED: "Enable browser to use the included connector, or choose a component under Advanced connection setup.",
  BROWSER_NOT_ENABLED: "Browser access is turned off.",
  BROWSER_SETTINGS_INVALID: "The saved browser setup is invalid. Review Advanced connection setup or use the included component.",
  BROWSER_SETTINGS_UNREADABLE: "The browser settings could not be read. Check access to the app's local data, then refresh.",
  BROWSER_SETTINGS_WRITE_FAILED: "Browser settings could not be saved. Your input has been kept.",
  BROWSER_SETTINGS_CHANGED: "Browser settings changed elsewhere. Refresh before saving again.",
  BROWSER_DEPENDENCY_MISSING: "The browser component is missing. Repair the test package or choose valid paths under Advanced connection setup.",
  BROWSER_PROFILE_MISSING: "The selected Chrome profile directory is missing. Correct or clear it under Advanced connection setup.",
  BROWSER_SETUP_REQUIRED: "Check the browser component and profile under Advanced connection setup.",
  BROWSER_CONNECTION_FAILED: "Chrome could not be reached. Keep Chrome open, enable remote debugging, approve its connection prompt, then reconnect.",
  BROWSER_CONNECTION_TIMEOUT: "Chrome did not respond in time. Check its connection prompt and try reconnecting.",
  BROWSER_CONNECTION_CLOSED: "The Chrome connection closed. Reconnect to continue.",
  BROWSER_SERVER_VERSION_UNSUPPORTED: "The browser connector version does not match this app. Use the component supplied with this package.",
  BROWSER_SERVER_TOOLS_MISSING: "The browser connector is incomplete. Repair or replace the browser component.",
  BROWSER_AUTHORIZATION_CHANGED: "Browser access changed during the test. Refresh its status.",
  BROWSER_STOPPED: "The browser service is not running. Restart the app and try again.",
  SETTINGS_INVALID_INPUT: "Check the connection paths and try again.",
  SETTINGS_TIMEOUT: "The operation took too long. Refresh the status before trying again.",
  WEB_ACCESS_SETTINGS_UNAVAILABLE: "Web access settings could not be loaded or saved. Refresh and try again.",
};

function explanation(code, fallback) {
  return code ? ERROR_TEXT[code] || fallback : "";
}

function statusText(state, service) {
  if (!state) return "Loading…";
  if (state.errorCode && state.errorCode !== "WEB_SEARCH_NOT_ENABLED" && state.errorCode !== "BROWSER_NOT_ENABLED"
      && state.errorCode !== "WEB_SEARCH_NOT_CONFIGURED" && state.errorCode !== "BROWSER_NOT_CONFIGURED") return "Needs attention";
  if (!state.configured) return "Not configured";
  if (!state.enabled) return "Off";
  if (state.checkSucceeded === true) return service === "browser" ? "Connected at last test" : "Last test passed";
  return state.ready ? "Ready to test" : "Needs setup";
}

function readSnapshot(value) {
  if (!value || typeof value !== "object" || Array.isArray(value)) throw new Error("WEB_ACCESS_SETTINGS_UNAVAILABLE");
  for (const item of [value.search, value.browser]) {
    if (!item || typeof item !== "object" || typeof item.configured !== "boolean" || typeof item.enabled !== "boolean"
        || typeof item.ready !== "boolean" || (item.errorCode != null && typeof item.errorCode !== "string")
        || (item.checkSucceeded != null && typeof item.checkSucceeded !== "boolean")) throw new Error("WEB_ACCESS_SETTINGS_UNAVAILABLE");
  }
  if (typeof value.browser.nodePath !== "string" || typeof value.browser.serverPath !== "string"
      || (value.browser.userDataDirectory != null && typeof value.browser.userDataDirectory !== "string"))
    throw new Error("WEB_ACCESS_SETTINGS_UNAVAILABLE");
  return value;
}

export async function mount(container, context) {
  const style = document.createElement("link");
  style.rel = "stylesheet";
  style.href = new URL("./web-access.css", import.meta.url).href;
  document.head.append(style);
  container.innerHTML = MARKUP;
  const root = container.querySelector(".web-access-page");
  const find = selector => root.querySelector(selector);
  const form = find("[data-browser-form]");
  let snapshot = null, disposed = false, busy = false, dirty = false, feedback = "", actionError = "";
  const advanced = find(".web-access-advanced");
  advanced.open = context.state?.advancedOpen === true;

  function render() {
    if (disposed) return;
    root.setAttribute("aria-busy", String(busy));
    for (const control of root.querySelectorAll("button,input")) control.disabled = busy || !snapshot;
    find("[data-reload]").disabled = busy;
    for (const name of ["search", "browser"]) {
      const state = snapshot?.[name];
      const badge = find(`[data-${name}-state]`);
      badge.textContent = statusText(state, name);
      badge.dataset.tone = state?.checkSucceeded === true && !state.errorCode ? "good" : state?.errorCode && state.enabled ? "attention" : "neutral";
      find(`[data-${name}-detail]`).textContent = state ? explanation(state.errorCode,
        "The service could not complete the request. Refresh the status and try again.")
        || (state.ready && state.checkSucceeded == null ? "Configuration is ready. Run a test to check the current connection."
          : name === "browser" && state.checkSucceeded === true ? "Chrome responded at the last test. Reconnect if the session has since closed." : "") : "Loading settings…";
      const checked = Date.parse(state?.checkedAtUtc || "");
      find(`[data-${name}-checked]`).textContent = Number.isFinite(checked)
        ? `Last tested at ${new Date(checked).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit", second: "2-digit" })}.` : "";
      find(`[data-${name}-test]`).disabled = busy || !state?.ready || (name === "browser" && dirty);
      find(`[data-${name}-toggle]`).textContent = name === "browser" && state?.enabled && !state?.visualEnabled
        ? "Upgrade browser access" : state?.enabled ? "Turn off" : name === "search" ? "Enable search" : "Enable browser";
      if (name === "search" && state?.errorCode === "WEB_SEARCH_CONFIG_INVALID")
        find("[data-search-toggle]").textContent = "Repair settings & enable";
    }
    find("[data-browser-reconnect]").disabled = busy || !snapshot?.browser.ready || dirty;
    find("[data-browser-toggle]").disabled = busy || !snapshot || dirty;
    if (snapshot && !dirty) {
      form.elements.nodePath.value = snapshot.browser.nodePath;
      form.elements.serverPath.value = snapshot.browser.serverPath;
      form.elements.userDataDirectory.value = snapshot.browser.userDataDirectory || "";
    }
    find("[data-browser-save]").disabled = busy || !snapshot || !dirty;
    find("[data-dirty]").hidden = !dirty;
    find("[data-visual-state]").textContent = snapshot?.browser.enabled && !snapshot.browser.visualEnabled
      ? "This browser grant covers text only. Upgrade to include images; existing grants and changed connections are not expanded automatically."
      : "Browser access includes text and on-demand images. Chrome may ask you to approve its connection separately.";
    find("[data-feedback]").textContent = feedback;
    find("[data-error]").textContent = actionError;
    find("[data-error]").hidden = !actionError;
  }

  async function act(command, payload = {}) {
    if (disposed || busy) return;
    busy = true;
    actionError = "";
    feedback = command.endsWith("check") || command.endsWith("reconnect") ? "Checking connection…" : "";
    render();
    try {
      const next = readSnapshot(await context.request(command, payload));
      if (disposed) return;
      snapshot = next;
      if (command === "webAccess.browser.save" || command === "webAccess.browser.useBundled") dirty = false;
      feedback = command === "webAccess.browser.save" ? "Connection setup saved."
        : command === "webAccess.browser.useBundled" ? "Included browser component selected."
        : command.endsWith("setEnabled") ? "Setting saved."
        : command.endsWith("check") || command.endsWith("reconnect") ? "Connection check finished." : "";
    } catch (error) {
      if (!disposed) {
        actionError = explanation(error?.message, "The action could not be completed. Your input has been kept; refresh and try again.");
        feedback = "";
      }
    } finally {
      busy = false;
      render();
    }
  }

  find("[data-search-toggle]").addEventListener("click", () => {
    if (snapshot) void act("webAccess.search.setEnabled", { enabled: !snapshot.search.enabled });
  });
  find("[data-search-test]").addEventListener("click", () => { void act("webAccess.search.check"); });
  find("[data-browser-toggle]").addEventListener("click", () => {
    if (!snapshot || dirty) return;
    if (!snapshot.browser.configured) void act("webAccess.browser.useBundled");
    else void act("webAccess.browser.setEnabled", { enabled: !snapshot.browser.enabled || !snapshot.browser.visualEnabled });
  });
  find("[data-browser-test]").addEventListener("click", () => { void act("webAccess.browser.check"); });
  find("[data-browser-reconnect]").addEventListener("click", () => { void act("webAccess.browser.reconnect"); });
  find("[data-browser-bundled]").addEventListener("click", () => { void act("webAccess.browser.useBundled"); });
  find("[data-reload]").addEventListener("click", () => { void act("webAccess.get"); });
  form.addEventListener("input", () => { dirty = true; feedback = ""; render(); });
  form.addEventListener("submit", event => {
    event.preventDefault();
    if (dirty && !busy) void act("webAccess.browser.save", {
      nodePath: form.elements.nodePath.value.trim(), serverPath: form.elements.serverPath.value.trim(),
      userDataDirectory: form.elements.userDataDirectory.value.trim() || null,
    });
  });

  function dispose() {
    if (disposed) return;
    disposed = true;
    context.signal?.removeEventListener("abort", dispose);
    for (const control of root.querySelectorAll("button,input")) control.disabled = true;
    style.remove();
  }
  dispose.getState = () => ({
    advancedOpen: advanced.open,
    ...(dirty ? { nodePath: form.elements.nodePath.value, serverPath: form.elements.serverPath.value,
      userDataDirectory: form.elements.userDataDirectory.value, dirty: true } : {}),
  });
  context.signal?.addEventListener("abort", dispose, { once: true });
  if (context.signal?.aborted) dispose();
  render();
  await act("webAccess.get");
  if (!disposed && context.state?.dirty === true) {
    for (const field of ["nodePath", "serverPath", "userDataDirectory"])
      if (typeof context.state[field] === "string") form.elements[field].value = context.state[field];
    dirty = true;
    render();
  }
  return dispose;
}
