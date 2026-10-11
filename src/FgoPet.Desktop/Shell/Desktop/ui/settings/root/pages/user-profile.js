const english = {
  "profile.title": "Your name",
  "profile.name": "Display name",
  "profile.reload": "Reload",
  "profile.cancel": "Cancel changes",
  "profile.reset": "Restore defaults",
  "profile.save": "Save",
  "profile.loading": "Loading your name…",
  "profile.draft": "Your unsaved changes were kept.",
  "profile.loadFailed": "Your name could not be loaded. Try again.",
  "profile.saved": "Your name was saved.",
  "profile.saveFailed": "The change could not be saved. Your input was kept.",
  "profile.cancelled": "Changes cancelled.",
  "profile.resetDone": "Your name was reset.",
  "profile.resetFailed": "The reset failed. Your input was kept.",
  "profile.explanation": "This is your global display name. Each character's form of address is managed separately in its character settings."
};
const t = key => english[key] || key;

export function mount(container, context) {
  let disposed = false;
  let loaded = false;
  let savedName = "";
  let busy = false;
  container.innerHTML = `
    <form class="profile-panel" aria-label="${t("profile.title")}">
      <label for="profile-display-name">${t("profile.name")}</label>
      <input id="profile-display-name" class="profile-name" data-action="display-name" maxlength="80" autocomplete="off" disabled>
      <p class="profile-explanation"></p>
      <p class="profile-status" role="status" aria-live="polite"></p>
      <button class="profile-button" data-action="retry" type="button" hidden>${t("profile.reload")}</button>
      <div class="profile-actions">
        <button class="profile-button" data-action="cancel" type="button" disabled>${t("profile.cancel")}</button>
        <button class="profile-button" data-action="reset" type="button" disabled>${t("profile.reset")}</button>
        <button class="profile-button profile-button--primary" data-action="save" type="submit" disabled>${t("profile.save")}</button>
      </div>
    </form>`;

  const form = container.querySelector("form");
  const input = container.querySelector(".profile-name");
  const explanation = container.querySelector(".profile-explanation");
  const status = container.querySelector(".profile-status");
  const retry = container.querySelector('[data-action="retry"]');
  const cancel = container.querySelector('[data-action="cancel"]');
  const reset = container.querySelector('[data-action="reset"]');
  const save = container.querySelector('[data-action="save"]');

  function report(message, isError = false) {
    if (disposed) return;
    status.textContent = message;
    status.classList.toggle("is-error", isError);
  }

  function setBusy(value) {
    busy = value;
    input.disabled = busy || !loaded;
    cancel.disabled = reset.disabled = save.disabled = busy || !loaded;
    retry.disabled = busy;
  }

  function apply(snapshot) {
    loaded = true;
    savedName = typeof snapshot.displayName === "string" ? snapshot.displayName : "";
    input.value = savedName;
    explanation.textContent = t("profile.explanation");
  }

  function restoreDraft(draft, errorText) {
    input.value = draft;
    setBusy(false);
    report(errorText, true);
    input.focus({ preventScroll: true });
  }

  async function load() {
    retry.hidden = true;
    setBusy(true);
    report(t("profile.loading"));
    try {
      const snapshot = await context.request("getProfile");
      if (disposed) return;
      apply(snapshot);
      const draft = context.state;
      const restoredDraft = draft && draft.dirty === true && typeof draft.displayName === "string";
      if (restoredDraft) {
        input.value = draft.displayName;
        report(t("profile.draft"));
      }
      setBusy(false);
      if (!restoredDraft) {
        report("");
        input.focus({ preventScroll: true });
      }
    } catch {
      if (!disposed) {
        setBusy(false);
        retry.hidden = false;
        report(t("profile.loadFailed"), true);
        retry.focus({ preventScroll: true });
      }
    }
  }

  form.addEventListener("submit", async event => {
    event.preventDefault();
    if (disposed || busy || !loaded) return;
    const draft = input.value;
    setBusy(true);
    try {
      const snapshot = await context.request("saveProfile", { displayName: draft });
      if (disposed) return;
      apply(snapshot);
      setBusy(false);
      report(t("profile.saved"));
      input.focus({ preventScroll: true });
    } catch {
      if (!disposed) restoreDraft(draft, t("profile.saveFailed"));
    }
  });

  cancel.addEventListener("click", () => {
    if (disposed || busy || !loaded) return;
    input.value = savedName;
    report(t("profile.cancelled"));
    input.focus({ preventScroll: true });
  });

  retry.addEventListener("click", () => { if (!disposed && !busy) void load(); });

  reset.addEventListener("click", async () => {
    if (disposed || busy || !loaded) return;
    const draft = input.value;
    setBusy(true);
    try {
      const snapshot = await context.request("resetProfile");
      if (disposed) return;
      apply(snapshot);
      setBusy(false);
      report(t("profile.resetDone"));
      input.focus({ preventScroll: true });
    } catch {
      if (!disposed) restoreDraft(draft, t("profile.resetFailed"));
    }
  });

  input.addEventListener("keydown", event => {
    if (disposed) return;
    if (event.key === "Escape") {
      event.preventDefault();
      cancel.click();
    }
    if (event.key === "Enter" && (event.isComposing || event.keyCode === 229)) event.preventDefault();
  });

  if (context.signal) {
    if (context.signal.aborted) disposed = true;
    else context.signal.addEventListener("abort", () => { disposed = true; }, { once: true });
  }
  void load();
  return {
    getState: () => {
      if (!loaded) return context.state;
      return input.value !== savedName
        ? { baseline: savedName, displayName: input.value, dirty: true }
        : { baseline: savedName, displayName: input.value, dirty: false };
    },
    dispose: () => { disposed = true; },
  };
}
