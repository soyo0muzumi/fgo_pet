(() => {
  "use strict";
  const bridge = window.chrome && window.chrome.webview;
  const status = document.getElementById("status");
  const input = document.getElementById("display-name");
  const explanation = document.getElementById("explanation");
  const save = document.getElementById("save");
  const cancel = document.getElementById("cancel");
  const reset = document.getElementById("reset");
  const pending = new Map();
  let serial = 0;
  let persisted = "";
  let closed = false;

  function message(text, error = false) {
    status.textContent = text;
    status.classList.toggle("error", error);
  }
  function request(type, payload = {}) {
    if (!bridge || closed) return Promise.reject(new Error("SETTINGS_UNAVAILABLE"));
    const requestId = `profile_${++serial}`;
    return new Promise((resolve, reject) => {
      pending.set(requestId, { resolve, reject });
      bridge.postMessage({ type, requestId, payload });
    });
  }
  function setEnabled(enabled) {
    input.disabled = save.disabled = cancel.disabled = reset.disabled = !enabled;
  }
  function apply(snapshot) {
    persisted = snapshot.displayName || "";
    input.value = persisted;
    explanation.textContent = snapshot.explanation || "";
    setEnabled(true);
  }
  if (!bridge) { message("此页面需要在 FGO Pet 中打开。", true); return; }
  bridge.addEventListener("message", event => {
    const response = event.data;
    if (!response || typeof response.type !== "string") return;
    if (response.type === "command.result") {
      const waiter = pending.get(response.requestId);
      if (!waiter) return;
      pending.delete(response.requestId);
      if (response.success) waiter.resolve(response.payload);
      else waiter.reject(new Error(response.errorCode || "SETTINGS_UNAVAILABLE"));
    } else if (response.type === "theme.changed") {
      for (const [key, value] of Object.entries(response.variables || {}))
        if (key.startsWith("--") && typeof value === "string")
          document.documentElement.style.setProperty(key, value);
      bridge.postMessage({ type: "theme.ack", version: response.version });
    }
  });
  document.getElementById("profile-form").addEventListener("submit", async event => {
    event.preventDefault();
    const draft = input.value;
    setEnabled(false);
    try { apply(await request("saveProfile", { displayName: draft })); message("已保存用户资料"); }
    catch { input.value = draft; setEnabled(true); message("保存失败，输入已保留。", true); }
  });
  cancel.addEventListener("click", () => { input.value = persisted; message("已取消修改"); input.focus(); });
  reset.addEventListener("click", async () => {
    const draft = input.value;
    setEnabled(false);
    try { apply(await request("resetProfile")); message("已恢复默认用户资料"); }
    catch { input.value = draft; setEnabled(true); message("恢复失败，输入已保留。", true); }
  });
  input.addEventListener("keydown", event => {
    if (event.key === "Escape") { event.preventDefault(); cancel.click(); }
    if (event.key === "Enter" && (event.isComposing || event.keyCode === 229)) event.preventDefault();
  });
  window.addEventListener("pagehide", () => { closed = true; pending.clear(); });
  bridge.postMessage({ type: "ready" });
  request("getProfile").then(snapshot => { apply(snapshot); message(""); })
    .catch(() => message("资料读取失败，请切换页面后重试。", true));
})();
