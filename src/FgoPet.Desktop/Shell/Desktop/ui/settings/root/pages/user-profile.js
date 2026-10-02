export function mount(container, context) {
  let disposed = false;
  let loaded = false;
  let savedName = "";
  let busy = false;
  container.innerHTML = `
    <form class="profile-panel" aria-label="用户资料">
      <label for="profile-display-name">显示名称</label>
      <input id="profile-display-name" class="profile-name" data-action="display-name" maxlength="80" autocomplete="off" disabled>
      <p class="profile-explanation"></p>
      <p class="profile-status" role="status" aria-live="polite"></p>
      <button class="profile-button" data-action="retry" type="button" hidden>重试读取</button>
      <div class="profile-actions">
        <button class="profile-button" data-action="cancel" type="button" disabled>取消修改</button>
        <button class="profile-button" data-action="reset" type="button" disabled>恢复默认</button>
        <button class="profile-button profile-button--primary" data-action="save" type="submit" disabled>保存</button>
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
    explanation.textContent = typeof snapshot.explanation === "string" ? snapshot.explanation : "";
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
    report("正在读取资料…");
    try {
      const snapshot = await context.request("getProfile");
      if (disposed) return;
      apply(snapshot);
      const draft = context.state;
      const restoredDraft = draft && draft.dirty === true && typeof draft.displayName === "string";
      if (restoredDraft) {
        input.value = draft.displayName;
        report("已保留未保存的修改");
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
        report("资料读取失败，请重试读取。", true);
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
      report("已保存用户资料");
      input.focus({ preventScroll: true });
    } catch {
      if (!disposed) restoreDraft(draft, "保存失败，输入已保留。");
    }
  });

  cancel.addEventListener("click", () => {
    if (disposed || busy || !loaded) return;
    input.value = savedName;
    report("已取消修改");
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
      report("已恢复默认用户资料");
      input.focus({ preventScroll: true });
    } catch {
      if (!disposed) restoreDraft(draft, "恢复失败，输入已保留。");
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
