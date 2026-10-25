const MARKUP = `
  <div class="qq-page" data-state="loading">
    <p class="qq-lead">Continue a conversation in a private QQ chat. Keep FGO Pet open and this computer online.</p>
    <form data-credentials aria-label="QQ bot settings">
      <div class="qq-field">
        <label for="qq-app-id">App ID</label>
        <input id="qq-app-id" class="qq-input" name="appId" inputmode="numeric" autocomplete="off" maxlength="32" pattern="[0-9]+" required>
      </div>
      <div class="qq-field">
        <label for="qq-app-secret">App Secret</label>
        <input id="qq-app-secret" class="qq-input" name="appSecret" type="password" autocomplete="new-password" maxlength="4096" aria-describedby="qq-secret-state">
        <p class="qq-hint" id="qq-secret-state" data-secret-state></p>
      </div>
      <p class="qq-hint" data-dirty hidden>You have unsaved changes.</p>
      <div class="qq-actions"><button class="qq-button qq-button--primary" type="submit" data-save disabled>Save bot settings</button></div>
    </form>

    <section class="qq-section" aria-labelledby="qq-connection-heading">
      <h2 id="qq-connection-heading">Private chat</h2>
      <p class="qq-connection" role="status" aria-live="polite" data-connection>Loading…</p>
      <p class="qq-hint" data-paired></p>
      <p class="qq-hint" data-pairing-hint>Open a conversation in FGO Pet, then start pairing. Send the code to your bot from the QQ account you want to use.</p>
      <div class="qq-pairing" data-pairing hidden>
        <label for="qq-pairing-code">Send this code in your private chat with the bot</label>
        <output id="qq-pairing-code" data-pairing-code></output>
        <p class="qq-hint" data-pairing-expiry></p>
      </div>
      <div class="qq-actions">
        <button class="qq-button qq-button--primary" type="button" data-pair disabled>Start pairing</button>
        <button class="qq-button" type="button" data-toggle disabled>Enable</button>
      </div>
    </section>

    <section class="qq-section" aria-labelledby="qq-conversation-heading">
      <h2 id="qq-conversation-heading">Conversation</h2>
      <p class="qq-hint" data-binding>No desktop conversation linked yet.</p>
      <button class="qq-button" type="button" data-bind disabled>Use current conversation</button>
    </section>

    <div class="qq-feedback">
      <p class="qq-hint" role="status" aria-live="polite" data-feedback></p>
      <p class="qq-error" role="alert" data-error hidden></p>
      <button class="qq-button" type="button" data-retry hidden>Reload</button>
    </div>
    <div class="qq-reset">
      <button class="qq-button" type="button" data-reset disabled>Clear bot settings & pairing</button>
      <div class="qq-confirm" data-reset-confirm hidden>
        <p>This disconnects QQ and clears the saved App Secret and account pairing. Your conversation stays in FGO Pet.</p>
        <div class="qq-actions">
          <button class="qq-button qq-button--danger" type="button" data-reset-accept>Clear settings</button>
          <button class="qq-button" type="button" data-reset-cancel>Cancel</button>
        </div>
      </div>
    </div>
  </div>`;

const STATES = {
  unconfigured: "Bot settings needed", disabled: "Disconnected", disconnected: "Disconnected",
  unpaired: "Ready to pair", pairing: "Waiting for your pairing code",
  connecting: "Connecting…", reconnecting: "Reconnecting…", connected: "Connected",
  ready: "Connected", faulted: "Could not connect", stopped: "Disconnected",
};

function snapshotFrom(value) {
  if (!value || typeof value !== "object" || Array.isArray(value)
      || typeof value.appId !== "string" || typeof value.hasSecret !== "boolean"
      || typeof value.enabled !== "boolean" || typeof value.isPaired !== "boolean"
      || typeof value.state !== "string"
      || (value.errorCode != null && typeof value.errorCode !== "string")
      || (value.pairingCode != null && typeof value.pairingCode !== "string")
      || (value.pairingExpiresAt != null && typeof value.pairingExpiresAt !== "string")
      || (value.binding != null && (typeof value.binding !== "object" || Array.isArray(value.binding))))
    throw new Error("QQ_SETTINGS_UNAVAILABLE");
  return value;
}

function errorText(code) {
  if (!code) return "";
  if (code === "SETTINGS_INVALID_INPUT") return "Check your App ID and App Secret, then try again.";
  if (code.includes("SCOPE") || code.includes("CONVERSATION")) return "Open a conversation in FGO Pet, then try again.";
  if (code.includes("CREDENTIAL") || code.includes("SECRET") || code.includes("AUTH")) return "Check the saved App ID and App Secret, then try again.";
  if (code.includes("PAIR")) return "Pair your QQ account using a new code, then try again.";
  return "QQ could not complete the action. Check the bot settings and connection, then try again.";
}

export async function mount(container, context) {
  const style = document.createElement("link");
  style.rel = "stylesheet";
  style.href = new URL("./qq.css", import.meta.url).href;
  document.head.append(style);
  container.innerHTML = MARKUP;
  const root = container.querySelector(".qq-page");
  const find = selector => root.querySelector(selector);
  const form = find("[data-credentials]");
  const appId = form.elements.appId;
  const appSecret = form.elements.appSecret;
  const save = find("[data-save]");
  const pair = find("[data-pair]");
  const toggle = find("[data-toggle]");
  const bind = find("[data-bind]");
  const reset = find("[data-reset]");
  const confirm = find("[data-reset-confirm]");
  let snapshot = null, disposed = false, busy = false, reading = false, dirty = false;
  let readFailed = false, actionError = "", feedback = "", generation = 0, timer = null;

  function render() {
    if (disposed) return;
    const available = !!snapshot && !readFailed;
    root.dataset.state = busy ? "busy" : readFailed ? "read-error" : snapshot ? "ready" : "loading";
    root.setAttribute("aria-busy", String(busy));
    if (snapshot && !dirty) appId.value = snapshot.appId;
    find("[data-dirty]").hidden = !dirty;
    find("[data-secret-state]").textContent = snapshot?.hasSecret
      ? "App Secret saved. Leave blank to keep it for this App ID."
      : "Enter the App Secret from your QQ bot settings.";
    find("[data-connection]").textContent = readFailed ? "Status unavailable"
      : snapshot ? (STATES[snapshot.state.toLowerCase()] || "Not connected") : "Loading…";
    find("[data-paired]").textContent = snapshot?.isPaired ? "Your QQ account is paired." : "No QQ account paired.";
    find("[data-pairing-hint]").hidden = snapshot?.isPaired === true;
    find("[data-binding]").textContent = snapshot?.binding
      ? "QQ uses the linked desktop conversation. Opening another conversation does not change this link. To switch, open it in FGO Pet and choose Use current conversation."
      : "Open a desktop conversation before pairing. QQ will continue that conversation.";
    const expires = Date.parse(snapshot?.pairingExpiresAt || "");
    const activeCode = !!snapshot?.pairingCode && Number.isFinite(expires) && expires > Date.now();
    find("[data-pairing]").hidden = !activeCode;
    find("[data-pairing-code]").textContent = activeCode ? snapshot.pairingCode : "";
    find("[data-pairing-expiry]").textContent = activeCode
      ? `Expires at ${new Date(expires).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" })}. Keep this code private.` : "";
    pair.textContent = snapshot?.pairingCode ? "New pairing code" : "Start pairing";
    const canDisconnect = !!snapshot && (snapshot.enabled || !!snapshot.pairingCode);
    toggle.textContent = canDisconnect ? "Disconnect" : "Enable";
    appId.disabled = busy;
    appSecret.disabled = busy;
    const hasDraftSecret = appSecret.value.trim().length > 0;
    const canKeepSecret = snapshot?.hasSecret && appId.value.trim() === snapshot.appId;
    save.disabled = !available || busy || !dirty || !/^[0-9]{1,32}$/.test(appId.value.trim()) || (!hasDraftSecret && !canKeepSecret);
    pair.disabled = !available || busy || dirty || !snapshot.hasSecret || snapshot.isPaired;
    toggle.disabled = !available || busy || (!canDisconnect && (dirty || !snapshot.isPaired));
    bind.disabled = !available || busy || dirty || !snapshot.isPaired;
    reset.disabled = !available || busy;
    find("[data-reset-accept]").disabled = !available || busy;
    find("[data-reset-cancel]").disabled = busy;
    find("[data-retry]").hidden = !readFailed;
    find("[data-retry]").disabled = busy || reading;
    find("[data-feedback]").textContent = feedback;
    const error = readFailed ? "QQ settings could not be loaded. Try again." : actionError || errorText(snapshot?.errorCode);
    find("[data-error]").textContent = error;
    find("[data-error]").hidden = !error;
  }

  function schedule() {
    window.clearTimeout(timer);
    if (!disposed) timer = window.setTimeout(() => { void refresh(); }, 2500);
  }

  async function refresh() {
    if (disposed) return;
    if (busy || reading) { schedule(); return; }
    reading = true;
    const requestedGeneration = generation;
    try {
      const next = snapshotFrom(await context.request("qq.get"));
      if (!disposed && !busy && requestedGeneration === generation) {
        snapshot = next;
        readFailed = false;
      }
    } catch {
      if (!disposed && requestedGeneration === generation) readFailed = true;
    } finally {
      reading = false;
      render();
      schedule();
    }
  }

  async function act(command, payload = {}) {
    if (disposed || busy || !snapshot || readFailed) return;
    busy = true;
    generation++;
    actionError = "";
    feedback = "";
    render();
    try {
      const next = snapshotFrom(await context.request(command, payload));
      if (disposed) return;
      snapshot = next;
      if (!next.errorCode) {
        if (command === "qq.configure" || command === "qq.reset") {
          appSecret.value = "";
          dirty = false;
        }
        confirm.hidden = true;
        feedback = command === "qq.configure" ? "Bot settings saved."
          : command === "qq.bindCurrentConversation" ? "QQ now uses the current desktop conversation."
          : command === "qq.reset" ? "Bot settings and pairing cleared." : "";
      }
    } catch (error) {
      if (!disposed) actionError = errorText(typeof error?.message === "string" ? error.message : "QQ_SETTINGS_UNAVAILABLE");
    } finally {
      busy = false;
      render();
      schedule();
    }
  }

  form.addEventListener("input", () => { dirty = true; feedback = ""; render(); });
  form.addEventListener("submit", event => {
    event.preventDefault();
    if (!save.disabled) void act("qq.configure", { appId: appId.value.trim(), appSecret: appSecret.value || null });
  });
  pair.addEventListener("click", () => { if (!pair.disabled) void act("qq.beginPairing"); });
  toggle.addEventListener("click", () => {
    if (!toggle.disabled) void act("qq.setEnabled", { enabled: !(snapshot.enabled || !!snapshot.pairingCode) });
  });
  bind.addEventListener("click", () => { if (!bind.disabled) void act("qq.bindCurrentConversation"); });
  reset.addEventListener("click", () => { if (!reset.disabled) confirm.hidden = false; });
  find("[data-reset-accept]").addEventListener("click", () => {
    if (!find("[data-reset-accept]").disabled) void act("qq.reset");
  });
  find("[data-reset-cancel]").addEventListener("click", () => { confirm.hidden = true; });
  find("[data-retry]").addEventListener("click", () => { void refresh(); });

  function dispose() {
    if (disposed) return;
    disposed = true;
    generation++;
    window.clearTimeout(timer);
    appSecret.value = "";
    find("[data-pairing-code]").textContent = "";
    for (const control of root.querySelectorAll("input,button")) control.disabled = true;
    context.signal?.removeEventListener("abort", dispose);
    style.remove();
  }
  context.signal?.addEventListener("abort", dispose, { once: true });
  if (context.signal?.aborted) dispose();
  render();
  await refresh();
  return dispose;
}
