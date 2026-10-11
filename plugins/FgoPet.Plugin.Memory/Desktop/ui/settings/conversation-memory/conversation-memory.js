const english = {
  "memory.feature": "Memory",
  "memory.enabled": "Enable memory",
  "memory.enabledHint": "The model may suggest memories during a conversation. Only memories you confirm are used in later conversations.",
  "memory.instant": "Changes apply immediately.",
  "memory.review": "Awaiting review",
  "memory.candidates": "Memory candidates",
  "memory.editCandidate": "Edit candidate",
  "memory.save": "Save edit",
  "memory.approve": "Confirm",
  "memory.reject": "Reject",
  "memory.replace": "Correct selected memory",
  "memory.confirmed": "Confirmed memories",
  "memory.editMemory": "Edit memory",
  "memory.disable": "Disable",
  "memory.enable": "Enable",
  "memory.delete": "Delete",
  "memory.maintenance": "Maintenance",
  "memory.refresh": "Refresh",
  "memory.deleteAll": "Delete all user data",
  "memory.deleteGroup": "Confirm deletion",
  "memory.deletePrompt": "Deleting all user data cannot be undone. Focus and bond history are excluded. Continue?",
  "memory.confirmDelete": "Confirm deletion",
  "memory.cancel": "Cancel",
  "memory.unavailable": "This page is temporarily unavailable.",
  "memory.empty": "No entries yet.",
  "memory.replacement": "Correction",
  "memory.disabled": "Disabled",
  "memory.failed": "The action did not complete. Current state was reloaded; you can retry."
};
const t = key => english[key] || key;

// 统一 Web 设置：对话与记忆页。
// 命令（经 context.request 调用，pageId 由 root 自动注入）：
//   memory.get / setEnabled / refresh / selectCandidate / selectMemory /
//   approveCandidate / rejectCandidate / editCandidate /
//   enableMemory / disableMemory / editMemory / deleteMemory /
//   selectReplacement / deleteAll
const OWNER = "conversation-memory";

const MARKUP = `
  <section class="cm-page" data-state="loading" aria-busy="true">
    <p class="cm-error" data-error role="alert" aria-live="assertive" hidden></p>

    <section class="cm-card" aria-label="${t("memory.feature")}">
      <label class="cm-switch-row">
        <span class="cm-switch-copy">
          <span class="cm-switch-title">${t("memory.enabled")}</span>
          <span class="cm-switch-description">${t("memory.enabledHint")}</span>
        </span>
        <input type="checkbox" id="cm-enabled" data-field="memoryEnabled" />
        <span class="cm-switch-control" aria-hidden="true"><i></i></span>
      </label>
      <p class="cm-note">${t("memory.instant")}</p>
    </section>

    <div class="cm-columns">
      <section class="cm-card" aria-label="${t("memory.review")}">
        <div class="cm-card-heading">
          <h2>${t("memory.review")}</h2>
          <p data-candidates-status role="status" aria-live="polite"></p>
        </div>
        <ul class="cm-list" data-candidates role="listbox" aria-label="${t("memory.candidates")}"></ul>
        <div class="cm-detail">
          <p class="cm-detail-text" data-candidate-details></p>
          <label class="cm-label" for="cm-candidate-edit">${t("memory.editCandidate")}</label>
          <textarea id="cm-candidate-edit" class="cm-input cm-textarea" data-candidate-edit rows="3"></textarea>
          <div class="cm-actions">
            <button type="button" class="cm-button" data-edit-candidate>${t("memory.save")}</button>
            <button type="button" class="cm-button cm-primary" data-approve-candidate>${t("memory.approve")}</button>
            <button type="button" class="cm-button" data-reject-candidate>${t("memory.reject")}</button>
            <button type="button" class="cm-button" data-select-replacement>${t("memory.replace")}</button>
          </div>
        </div>
      </section>

      <section class="cm-card" aria-label="${t("memory.confirmed")}">
        <div class="cm-card-heading">
          <h2>${t("memory.confirmed")}</h2>
          <p data-memories-status role="status" aria-live="polite"></p>
        </div>
        <ul class="cm-list" data-memories role="listbox" aria-label="${t("memory.confirmed")}"></ul>
        <div class="cm-detail">
          <p class="cm-detail-text" data-memory-details></p>
          <label class="cm-label" for="cm-memory-edit">${t("memory.editMemory")}</label>
          <textarea id="cm-memory-edit" class="cm-input cm-textarea" data-memory-edit rows="3"></textarea>
          <div class="cm-actions">
            <button type="button" class="cm-button" data-edit-memory>${t("memory.save")}</button>
            <button type="button" class="cm-button" data-disable-memory>${t("memory.disable")}</button>
            <button type="button" class="cm-button" data-enable-memory>${t("memory.enable")}</button>
            <button type="button" class="cm-button cm-danger" data-delete-memory>${t("memory.delete")}</button>
          </div>
        </div>
      </section>
    </div>

    <section class="cm-card" aria-label="${t("memory.maintenance")}">
      <div class="cm-actions">
        <button type="button" class="cm-button" data-refresh>${t("memory.refresh")}</button>
        <button type="button" class="cm-button cm-danger" data-delete-all>${t("memory.deleteAll")}</button>
      </div>
      <div class="cm-confirm" data-delete-confirm hidden role="group" aria-label="${t("memory.deleteGroup")}">
        <p>${t("memory.deletePrompt")}</p>
        <div class="cm-actions">
          <button type="button" class="cm-button cm-danger" data-delete-confirm-btn>${t("memory.confirmDelete")}</button>
          <button type="button" class="cm-button" data-delete-cancel>${t("memory.cancel")}</button>
        </div>
      </div>
      <p class="cm-status" data-status role="status" aria-live="polite"></p>
    </section>
  </section>
`;

function isSnapshot(candidate) {
  return candidate !== null && typeof candidate === "object"
    && typeof candidate.memoryEnabled === "boolean"
    && Array.isArray(candidate.candidates) && Array.isArray(candidate.storedMemories)
    && typeof candidate.statusText === "string";
}

function normalizeSnapshot(candidate) {
  if (!isSnapshot(candidate)) return null;
  // 只保留真正参与渲染的字段：角色态由后端给的三处状态文案表达
  // （VM 在无角色时统一给「请先选择角色。」），页面不需要原始的 activeServantId。
  return {
    memoryEnabled: candidate.memoryEnabled,
    candidates: candidate.candidates.map(item => ({
      id: String(item?.id ?? ""),
      text: String(item?.text ?? ""),
      hasReplacement: item?.hasReplacement === true,
    })),
    selectedCandidateId: typeof candidate.selectedCandidateId === "string" ? candidate.selectedCandidateId : "",
    candidateEditText: typeof candidate.candidateEditText === "string" ? candidate.candidateEditText : "",
    candidateDetails: typeof candidate.candidateDetails === "string" ? candidate.candidateDetails : "",
    storedMemories: candidate.storedMemories.map(item => ({
      id: String(item?.id ?? ""),
      text: String(item?.text ?? ""),
      isEnabled: item?.isEnabled === true,
      version: Number(item?.version ?? 1),
    })),
    selectedMemoryId: typeof candidate.selectedMemoryId === "string" ? candidate.selectedMemoryId : "",
    memoryEditText: typeof candidate.memoryEditText === "string" ? candidate.memoryEditText : "",
    memoryDetails: typeof candidate.memoryDetails === "string" ? candidate.memoryDetails : "",
    statusText: candidate.statusText,
    candidatesStatusText: typeof candidate.candidatesStatusText === "string" ? candidate.candidatesStatusText : "",
    storedMemoriesStatusText: typeof candidate.storedMemoriesStatusText === "string" ? candidate.storedMemoriesStatusText : "",
    isBusy: candidate.isBusy === true,
  };
}

export async function mount(container, context) {
  container.innerHTML = MARKUP;
  const page = container.querySelector(".cm-page");
  const link = document.createElement("link");
  link.rel = "stylesheet";
  link.href = new URL("./conversation-memory.css", import.meta.url).href;
  link.dataset.settingsOwner = OWNER;
  document.head.append(link);

  const errorEl = page.querySelector("[data-error]");
  const statusEl = page.querySelector("[data-status]");
  const enabledInput = page.querySelector("#cm-enabled");
  const candidatesList = page.querySelector("[data-candidates]");
  const memoriesList = page.querySelector("[data-memories]");
  const candidatesStatus = page.querySelector("[data-candidates-status]");
  const memoriesStatus = page.querySelector("[data-memories-status]");
  const candidateDetails = page.querySelector("[data-candidate-details]");
  const memoryDetails = page.querySelector("[data-memory-details]");
  const candidateEdit = page.querySelector("[data-candidate-edit]");
  const memoryEdit = page.querySelector("[data-memory-edit]");
  const confirmBox = page.querySelector("[data-delete-confirm]");

  let snapshot = null;
  let disposed = false;
  let unsubscribeTheme = null;

  function presentFailure(errorText) {
    errorEl.textContent = typeof errorText === "string" && errorText.length > 0
      ? errorText : t("memory.unavailable");
    errorEl.hidden = false;
    page.dataset.state = "error";
    setBusy(false);
  }

  // 禁用态的唯一出口：busy 优先，其次按当前选中项裁决。
  // 若把“busy 结束”写成无条件 enabled=true，会覆盖 render() 刚设的禁用态，
  // 导致未选中任何条目时按钮看起来仍可点。
  function syncDisabled() {
    const busy = page.getAttribute("aria-busy") === "true";
    for (const control of page.querySelectorAll("button, input, textarea")) control.disabled = busy;
    // 启用开关即使 busy 也保持可用：它是即时生效的独立开关。
    enabledInput.disabled = false;
    if (busy || snapshot === null) return;

    const hasCandidate = snapshot.selectedCandidateId.length > 0;
    for (const button of page.querySelectorAll(
      "[data-edit-candidate],[data-approve-candidate],[data-reject-candidate],[data-select-replacement]")) {
      button.disabled = !hasCandidate;
    }
    const hasMemory = snapshot.selectedMemoryId.length > 0;
    for (const button of page.querySelectorAll(
      "[data-edit-memory],[data-enable-memory],[data-disable-memory],[data-delete-memory]")) {
      button.disabled = !hasMemory;
    }
  }

  function setBusy(busy) {
    page.setAttribute("aria-busy", busy ? "true" : "false");
    syncDisabled();
  }

  // 列表项一律用 textContent 写入：记忆正文是不可信文本，绝不能走 innerHTML。
  function renderList(list, items, selectedId, onActivate) {
    list.replaceChildren();
    if (items.length === 0) {
      const empty = document.createElement("li");
      empty.className = "cm-empty";
      empty.textContent = t("memory.empty");
      list.append(empty);
      return;
    }
    for (const item of items) {
      const row = document.createElement("li");
      row.className = "cm-list-item" + (item.id === selectedId ? " is-selected" : "");
      row.setAttribute("role", "option");
      row.setAttribute("aria-selected", item.id === selectedId ? "true" : "false");

      const button = document.createElement("button");
      button.type = "button";
      button.className = "cm-list-button";
      button.textContent = item.text;

      if (item.hasReplacement === true) button.append(badge(t("memory.replacement"), ""));
      if (item.isEnabled === false) button.append(badge(t("memory.disabled"), " cm-badge-muted"));
      if (Number.isFinite(item.version)) button.append(badge("v" + item.version, " cm-badge-quiet"));

      button.addEventListener("click", () => onActivate(item.id));
      row.append(button);
      list.append(row);
    }
  }

  function badge(text, extra) {
    const span = document.createElement("span");
    span.className = "cm-badge" + extra;
    span.textContent = text;
    return span;
  }

  function render() {
    if (snapshot === null) return;
    enabledInput.checked = snapshot.memoryEnabled;
    candidatesStatus.textContent = snapshot.candidatesStatusText;
    memoriesStatus.textContent = snapshot.storedMemoriesStatusText;
    statusEl.textContent = snapshot.statusText;
    candidateDetails.textContent = snapshot.candidateDetails;
    memoryDetails.textContent = snapshot.memoryDetails;
    if (document.activeElement !== candidateEdit) candidateEdit.value = snapshot.candidateEditText;
    if (document.activeElement !== memoryEdit) memoryEdit.value = snapshot.memoryEditText;

    renderList(candidatesList, snapshot.candidates, snapshot.selectedCandidateId, id =>
      send("memory.selectCandidate", { candidateId: id }));
    renderList(memoriesList, snapshot.storedMemories, snapshot.selectedMemoryId, id =>
      send("memory.selectMemory", { memoryId: id }));

    syncDisabled();
  }

  // 失败后回读权威快照：错误码（error.message）是给日志的，不能直接呈现给用户，
  // 页面只给可读的一句话，并用最新快照覆盖本地状态。
  async function reload() {
    try {
      const next = normalizeSnapshot(await context.request("memory.get", {}));
      if (!disposed && next !== null) { snapshot = next; render(); }
    } catch { /* 回读失败时保留上一次快照；错误横幅已经给出提示。 */ }
  }

  async function send(type, payload = {}, failureText = t("memory.failed")) {
    if (disposed) return null;
    setBusy(true);
    try {
      const next = normalizeSnapshot(await context.request(type, payload));
      if (disposed || next === null) return null;
      snapshot = next;
      errorEl.hidden = true;
      page.dataset.state = "ready";
      render();
      return snapshot;
    } catch {
      if (disposed) return null;
      await reload();
      presentFailure(failureText);
      return null;
    } finally {
      if (!disposed) setBusy(false);
    }
  }

  enabledInput.addEventListener("change", () => {
    void send("memory.setEnabled", { value: enabledInput.checked });
  });

  page.querySelector("[data-refresh]").addEventListener("click", () => { void send("memory.refresh"); });

  page.querySelector("[data-approve-candidate]").addEventListener("click", () => {
    if (snapshot?.selectedCandidateId) void send("memory.approveCandidate", { candidateId: snapshot.selectedCandidateId });
  });
  page.querySelector("[data-reject-candidate]").addEventListener("click", () => {
    if (snapshot?.selectedCandidateId) void send("memory.rejectCandidate", { candidateId: snapshot.selectedCandidateId });
  });
  page.querySelector("[data-edit-candidate]").addEventListener("click", () => {
    if (snapshot?.selectedCandidateId) {
      void send("memory.editCandidate", { candidateId: snapshot.selectedCandidateId, text: candidateEdit.value });
    }
  });
  page.querySelector("[data-select-replacement]").addEventListener("click", () => {
    if (snapshot?.selectedCandidateId) {
      void send("memory.selectReplacement", { candidateId: snapshot.selectedCandidateId });
    }
  });

  page.querySelector("[data-edit-memory]").addEventListener("click", () => {
    if (snapshot?.selectedMemoryId) {
      void send("memory.editMemory", { memoryId: snapshot.selectedMemoryId, text: memoryEdit.value });
    }
  });
  page.querySelector("[data-enable-memory]").addEventListener("click", () => {
    if (snapshot?.selectedMemoryId) void send("memory.enableMemory", { memoryId: snapshot.selectedMemoryId });
  });
  page.querySelector("[data-disable-memory]").addEventListener("click", () => {
    if (snapshot?.selectedMemoryId) void send("memory.disableMemory", { memoryId: snapshot.selectedMemoryId });
  });
  page.querySelector("[data-delete-memory]").addEventListener("click", () => {
    if (snapshot?.selectedMemoryId) void send("memory.deleteMemory", { memoryId: snapshot.selectedMemoryId });
  });

  // 删除全部：内联二段式确认，不用 window.confirm。
  page.querySelector("[data-delete-all]").addEventListener("click", () => {
    confirmBox.hidden = false;
    page.querySelector("[data-delete-confirm-btn]").focus();
  });
  page.querySelector("[data-delete-cancel]").addEventListener("click", () => { confirmBox.hidden = true; });
  page.querySelector("[data-delete-confirm-btn]").addEventListener("click", () => {
    confirmBox.hidden = true;
    void send("memory.deleteAll");
  });

  if (typeof context.onThemeChanged === "function") {
    unsubscribeTheme = context.onThemeChanged(() => { /* palette comes from root.css variables */ });
  }

  context.signal.addEventListener("abort", dispose, { once: true });

  function dispose() {
    if (disposed) return;
    disposed = true;
    if (typeof unsubscribeTheme === "function") unsubscribeTheme();
    link.remove();
    for (const control of page.querySelectorAll("button, input, textarea")) control.disabled = true;
    page.dataset.state = "disposed";
  }

  await send("memory.get", {}, t("memory.unavailable"));
  return dispose;
}
