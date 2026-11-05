const labels = {
  "memory.feature": "记忆",
  "memory.enabled": "启用记忆",
  "memory.enabledHint": "开启后，模型可在聊天中建议记忆；只有你确认的记录才会用于后续聊天。关闭后记忆不再用于回复，已保存记录仍保留。",
  "memory.instant": "更改立即生效。",
  "memory.review": "待你确认",
  "memory.candidates": "记忆候选",
  "memory.editCandidate": "修改候选",
  "memory.save": "保存修改",
  "memory.approve": "确认记住",
  "memory.reject": "不记这条",
  "memory.replace": "更正所选记忆",
  "memory.chooseReplacement": "选择要更正的记忆",
  "memory.useReplacement": "用作候选的更正对象",
  "memory.confirmed": "已保存的记忆",
  "memory.editMemory": "修改这条记忆",
  "memory.disable": "暂不用于聊天",
  "memory.enable": "用于聊天",
  "memory.delete": "删除",
  "memory.maintenance": "数据管理",
  "memory.refresh": "刷新",
  "memory.deleteAll": "删除所有用户数据",
  "memory.deleteGroup": "确认删除",
  "memory.deletePrompt": "删除所有用户数据无法撤销，不含专注与羁绊历史。继续？",
  "memory.confirmDelete": "确认删除",
  "memory.cancel": "取消",
  "memory.unavailable": "此页面暂时不可用。",
  "memory.empty": "这里还没有保存的记忆。",
  "memory.replacement": "更正",
  "memory.disabled": "暂不用于聊天",
  "memory.failed": "操作结果尚未确认。已尝试重新读取当前状态，请核对后再操作。"
};
const t = key => labels[key] || key;

// 统一 Web 设置：对话与记忆页。
// 命令（经 context.request 调用，pageId 由 root 自动注入）：
//   memory.get / setEnabled / refresh / selectCandidate / selectMemory /
//   approveCandidate / rejectCandidate / editCandidate /
//   enableMemory / disableMemory / editMemory / deleteMemory /
//   selectReplacement / deleteAll
const OWNER = "conversation-memory";

const MARKUP = `
  <section class="cm-page settings-page-body" data-state="loading" aria-busy="true">
    <p class="cm-error settings-status is-error" data-error role="alert" aria-live="assertive" hidden></p>

    <div class="memory-head"><p>关于你的记录。可以纠正、停用或删除。</p>
      <button type="button" class="settings-secondary" data-memory-settings aria-expanded="false">⚙ 记忆设置</button></div>
    <section class="memory-settings" data-memory-settings-panel hidden>
      <label class="cm-switch-row settings-row"><span class="cm-switch-copy settings-row-copy">
        <span class="settings-label">启用记忆</span><span class="settings-help">${t("memory.enabledHint")}</span></span>
        <input type="checkbox" role="switch" id="cm-enabled" data-field="memoryEnabled" /></label>
      <p class="settings-help">${t("memory.instant")}</p>
    </section>
    <nav class="cm-tabs" aria-label="记忆视图">
      <button type="button" class="memory-attention" data-memory-view="candidates" aria-pressed="false"><span>✉</span><span><strong><span data-pending-count>0</span> 条记忆等你确认</strong><small>确认具体候选后，才会加入已保存列表</small></span><span>查看 ›</span></button>
      <button type="button" class="settings-secondary" data-memory-view="confirmed" aria-pressed="true" hidden>‹ 返回已保存记忆</button>
      <button type="button" class="settings-secondary" data-refresh>刷新</button>
    </nav>
    <p class="settings-status" data-edit-status role="status" aria-live="polite" hidden>切换页面时会保留草稿，选择“保存修改”后才会应用。</p>
    <div class="cm-confirm" data-stale-draft hidden><p data-current-text></p><button type="button" class="settings-secondary" data-rebase-draft>核对后保留草稿，使用当前版本</button></div>
    <div class="cm-columns">
      <section class="cm-card settings-section" data-memory-pane="candidates" aria-label="${t("memory.review")}" hidden>
        <div class="cm-card-heading settings-section-copy">
          <h2 class="settings-section-heading">${t("memory.review")}</h2>
          <div class="cm-candidate-pager"><button type="button" class="settings-secondary" data-candidate-prev aria-label="上一条候选">‹</button><span data-candidate-progress></span><button type="button" class="settings-secondary" data-candidate-next aria-label="下一条候选">›</button></div><p data-candidates-status role="status" aria-live="polite"></p>
        </div>
        <ul class="cm-list" data-candidates role="listbox" aria-label="${t("memory.candidates")}"></ul>
        <div class="cm-detail" data-candidate-editor hidden>
          <p class="cm-detail-text" data-candidate-details></p>
          <p class="cm-review-hint">这是待确认的候选，不是已经保存的事实。请先核对内容。</p>
          <details class="cm-candidate-options"><summary>修改候选或更正已有记忆</summary>
            <label class="cm-label settings-label" for="cm-candidate-edit">${t("memory.editCandidate")}</label>
            <textarea id="cm-candidate-edit" class="cm-input cm-textarea settings-input" data-candidate-edit rows="3"></textarea>
            <p class="settings-help" data-replacement-note></p>
            <div class="cm-actions settings-actions">
              <button type="button" class="cm-button settings-secondary" data-edit-candidate>${t("memory.save")}</button>
              <button type="button" class="cm-button settings-secondary" data-pick-replacement>${t("memory.chooseReplacement")}</button>
              <button type="button" class="cm-button settings-secondary" data-select-replacement>${t("memory.replace")}</button>
            </div>
          </details>
          <div class="cm-actions settings-actions">
            <button type="button" class="cm-button cm-primary settings-secondary settings-primary" data-approve-candidate>${t("memory.approve")}</button>
            <button type="button" class="cm-button settings-secondary" data-reject-candidate>${t("memory.reject")}</button>
            <button type="button" class="cm-button settings-secondary" data-candidate-later>稍后再看</button>
          </div>
        </div>
      </section>

      <section class="cm-card settings-section" data-memory-pane="confirmed" aria-label="${t("memory.confirmed")}">
        <div class="cm-card-heading settings-section-copy">
          <h3 class="settings-section-heading">已保存的记忆 <span data-saved-count>0</span></h3>
          <p data-memories-status role="status" aria-live="polite"></p>
        </div>
        <ul class="cm-list" data-memories role="listbox" aria-label="${t("memory.confirmed")}"></ul>
        <div class="cm-detail" data-memory-editor hidden>
          <p class="cm-detail-text" data-memory-details></p>
          <label class="cm-label settings-label" for="cm-memory-edit">${t("memory.editMemory")}</label>
          <textarea id="cm-memory-edit" class="cm-input cm-textarea settings-input" data-memory-edit rows="3"></textarea>
          <div class="cm-actions settings-actions">
            <button type="button" class="cm-button settings-secondary" data-edit-memory>${t("memory.save")}</button>
            <button type="button" class="cm-button settings-secondary" data-disable-memory>${t("memory.disable")}</button>
            <button type="button" class="cm-button settings-secondary" data-enable-memory>${t("memory.enable")}</button>
            <button type="button" class="cm-button cm-danger settings-secondary settings-danger" data-delete-memory>${t("memory.delete")}</button>
            <button type="button" class="cm-button settings-secondary" data-use-replacement hidden>${t("memory.useReplacement")}</button>
          </div>
        </div>
      </section>
    </div>
    <details class="memory-help"><summary>记忆与想法有什么不同？</summary><p>记忆保存关于你的记录，可能不准确或已经过时；她的想法是逐渐形成的判断，也可能需要纠正。两者分别管理，不会因为切换页面互相转存。</p></details>

    <p class="cm-status settings-status" data-status role="status" aria-live="polite"></p>
    <details class="settings-advanced cm-maintenance">
      <summary>${t("memory.maintenance")}</summary>
    <section class="cm-card settings-section" aria-label="${t("memory.maintenance")}">
      <div class="cm-actions settings-actions">
        <button type="button" class="cm-button cm-danger settings-secondary settings-danger" data-delete-all>${t("memory.deleteAll")}</button>
      </div>
      <div class="cm-confirm" data-delete-confirm hidden role="group" aria-label="${t("memory.deleteGroup")}">
        <p>${t("memory.deletePrompt")}</p>
        <div class="cm-actions settings-actions">
          <button type="button" class="cm-button cm-danger settings-secondary settings-danger" data-delete-confirm-btn>${t("memory.confirmDelete")}</button>
          <button type="button" class="cm-button settings-secondary" data-delete-cancel>${t("memory.cancel")}</button>
        </div>
      </div>
    </section>
    </details>
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
  // Scope is used only to discard drafts when the host changes companion.
  return {
    memoryEnabled: candidate.memoryEnabled,
    scope: String(candidate.activeServantId ?? ""),
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

  page.querySelector("[data-memory-settings-panel]").append(page.querySelector("[data-refresh]"), page.querySelector(".cm-maintenance"));
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
  page.querySelector("[data-memory-settings-panel]").hidden = context.state?.settingsOpen !== true;
  page.querySelector("[data-memory-settings]").setAttribute("aria-expanded", String(context.state?.settingsOpen === true));

  let snapshot = null;
  let disposed = false;
  let unsubscribeTheme = null;
  let lastDirty;
  let view = context.state?.view === "candidates" ? "candidates" : "confirmed";
  let draftScope = context.state?.scope;
  let memoryEditorId = context.state?.memoryEditorId || "";
  const editDrafts = new Map();
  const editBases = new Map(Array.isArray(context.state?.editBases) ? context.state.editBases : []);
  for (const entry of Array.isArray(context.state?.edits) ? context.state.edits : []) {
    if (Array.isArray(entry) && typeof entry[0] === "string" && typeof entry[1] === "string")
      editDrafts.set(entry[0], entry[1]);
  }

  function renderView() {
    for (const button of page.querySelectorAll("[data-memory-view]")) {
      button.setAttribute("aria-pressed", String(button.dataset.memoryView === view));
      button.hidden = button.dataset.memoryView === "confirmed" ? view === "confirmed" : view === "candidates" || !snapshot?.candidates.length;
    }
    for (const panel of page.querySelectorAll("[data-memory-pane]")) panel.hidden = panel.dataset.memoryPane !== view;
    page.querySelector("[data-edit-status]").hidden = editDrafts.size === 0;
    const dirty = editDrafts.size > 0;
    if (dirty !== lastDirty) { lastDirty = dirty; context.onDirtyChanged?.(dirty); }
  }
  for (const button of page.querySelectorAll("[data-memory-view]")) button.addEventListener("click", async () => {
    if (disposed || page.getAttribute("aria-busy") === "true") return;
    view = button.dataset.memoryView;
    renderView();
    if (view === "candidates" && snapshot?.candidates.length && !snapshot.selectedCandidateId)
      await send("memory.selectCandidate", { candidateId: snapshot.candidates[0].id });
  });
  for (const [selector, step] of [["[data-candidate-prev]", -1], ["[data-candidate-next]", 1]])
    page.querySelector(selector).addEventListener("click", () => {
      if (!snapshot?.candidates.length) return;
      const index = Math.max(0, snapshot.candidates.findIndex(item => item.id === snapshot.selectedCandidateId));
      const next = (index + step + snapshot.candidates.length) % snapshot.candidates.length;
      void send("memory.selectCandidate", { candidateId: snapshot.candidates[next].id });
    });
  function preserveEdit(kind, input, id) {
    if (disposed || !id) return;
    const items = kind === "candidate" ? snapshot.candidates : snapshot.storedMemories;
    const saved = items.find(item => item.id === id)?.text;
    const key = `${kind}:${id}`;
    if (input.value.trim() === saved) { editDrafts.delete(key); editBases.delete(key); }
    else {
      if (!editBases.has(key)) { const item = items.find(item => item.id === id); editBases.set(key, kind === "memory" ? item?.version : item?.text); }
      editDrafts.set(key, input.value);
    }
    renderView(); syncDisabled();
  }
  candidateEdit.addEventListener("input", () => preserveEdit("candidate", candidateEdit, snapshot?.selectedCandidateId));
  memoryEdit.addEventListener("input", () => preserveEdit("memory", memoryEdit, snapshot?.selectedMemoryId));
  function renderEdit(kind, input, id, text) {
    const key = `${kind}:${id}`;
    input.value = editDrafts.has(key) ? editDrafts.get(key) : text;
  }

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
    // The immediate owner setting is serialized with the page request in flight.
    enabledInput.disabled = busy || snapshot === null;
    if (busy || snapshot === null) return;

    const hasCandidate = snapshot.selectedCandidateId.length > 0;
    for (const button of page.querySelectorAll(
      "[data-edit-candidate],[data-approve-candidate],[data-reject-candidate],[data-pick-replacement]")) {
      button.disabled = !hasCandidate;
    }
    const hasMemory = snapshot.selectedMemoryId.length > 0;
    page.querySelector("[data-select-replacement]").disabled = !hasCandidate || !hasMemory;
    page.querySelector("[data-use-replacement]").disabled = !hasCandidate || !hasMemory;
    for (const button of page.querySelectorAll(
      "[data-edit-memory],[data-enable-memory],[data-disable-memory],[data-delete-memory]")) {
      button.disabled = !hasMemory;
    }
    for (const [kind, id, items] of [["candidate", snapshot.selectedCandidateId, snapshot.candidates], ["memory", snapshot.selectedMemoryId, snapshot.storedMemories]]) {
      const key = `${kind}:${id}`, item = items.find(item => item.id === id);
      const stale = editDrafts.has(key) && editBases.get(key) !== (kind === "memory" ? item?.version : item?.text);
      page.querySelector(kind === "memory" ? "[data-edit-memory]" : "[data-edit-candidate]").disabled = !id || stale;
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
      empty.className = "cm-empty settings-empty";
      empty.textContent = t("memory.empty");
      list.append(empty);
      return;
    }
    const visible = list === candidatesList ? items.filter(item => item.id === (snapshot.selectedCandidateId || items[0]?.id)) : items;
    for (const item of visible) {
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
      if (Number.isFinite(item.version) && item.isEnabled) button.append(badge("已保存 · 第 " + item.version + " 版", " cm-badge-quiet"));

      button.addEventListener("click", () => onActivate(item.id));
      row.append(button);
      if (list === memoriesList) {
        const symbol = document.createElement("span"); symbol.className = "cm-memory-symbol"; symbol.setAttribute("aria-hidden", "true");
        symbol.innerHTML = '<svg viewBox="0 0 20 20"><path d="M4 4.5h4.5A1.5 1.5 0 0 1 10 6v10a1.5 1.5 0 0 0-1.5-1.5H4zM16 4.5h-4.5A1.5 1.5 0 0 0 10 6v10a1.5 1.5 0 0 1 1.5-1.5H16z"/></svg>';
        row.prepend(symbol);
        const actions = document.createElement("div"); actions.className = "cm-row-actions";
        const paths = { edit: "M13 3l4 4L7 17l-4 1 1-4z", toggle: item.isEnabled ? "M5 4v12M13 4v12" : "M5 4l10 6-10 6z", del: "M3 5h14M7 5V3h6v2M5 5l1 13h8l1-13" };
        for (const [action, label] of [["edit", "修改"], ["toggle", item.isEnabled ? "暂不用于聊天" : "用于聊天"], ["del", "删除"]]) {
          const control = document.createElement("button"); control.type = "button"; control.setAttribute("aria-label", label); control.title = label;
          // Fixed local icon paths only. Memory text always uses textContent.
          control.innerHTML = `<svg viewBox="0 0 20 20" aria-hidden="true"><path d="${paths[action]}"/></svg>`;
          control.onclick = async () => {
            if (action === "toggle") { await send(item.isEnabled ? "memory.disableMemory" : "memory.enableMemory", { memoryId: item.id }); return; }
            memoryEditorId = item.id;
            const next = await send("memory.selectMemory", { memoryId: item.id });
            if (!next) return;
            if (action === "del") page.querySelector("[data-delete-memory]").click();
            else memoryEdit.focus();
          };
          actions.append(control);
        }
        row.append(actions);
      }
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
    if (draftScope !== snapshot.scope) { editDrafts.clear(); editBases.clear(); memoryEditorId = ""; page.querySelector("[data-single-delete]")?.remove(); }
    draftScope = snapshot.scope;
    page.querySelector("[data-pending-count]").textContent = String(snapshot.candidates.length);
    page.querySelector("[data-saved-count]").textContent = String(snapshot.storedMemories.length);
    const availableDrafts = new Set([
      ...snapshot.candidates.map(item => `candidate:${item.id}`),
      ...snapshot.storedMemories.map(item => `memory:${item.id}`),
    ]);
    for (const key of editDrafts.keys()) if (!availableDrafts.has(key)) { editDrafts.delete(key); editBases.delete(key); }
    // Review actions refresh the native lists and clear selection. Only persisted
    // list text acknowledges a saved edit; the selected editor also echoes failed drafts.
    for (const [kind, items] of [["candidate", snapshot.candidates], ["memory", snapshot.storedMemories]]) {
      for (const item of items) {
        const key = `${kind}:${item.id}`;
        if (editDrafts.get(key)?.trim() === item.text) { editDrafts.delete(key); editBases.delete(key); }
      }
    }
    enabledInput.checked = snapshot.memoryEnabled;
    const index = Math.max(0, snapshot.candidates.findIndex(item => item.id === snapshot.selectedCandidateId));
    page.querySelector("[data-candidate-progress]").textContent = snapshot.candidates.length ? `${index + 1} / ${snapshot.candidates.length}` : "没有待确认的候选";
    candidatesStatus.hidden = /^候选 \d+ 条$/.test(snapshot.candidatesStatusText);
    memoriesStatus.hidden = /^已确认记忆 \d+ 条$/.test(snapshot.storedMemoriesStatusText);
    statusEl.hidden = snapshot.statusText === `${snapshot.candidatesStatusText} · ${snapshot.storedMemoriesStatusText}` && !/失败|选择角色/.test(snapshot.statusText);
    candidatesStatus.textContent = snapshot.candidatesStatusText;
    memoriesStatus.textContent = snapshot.storedMemoriesStatusText;
    statusEl.textContent = snapshot.statusText;
    candidateDetails.textContent = snapshot.candidateDetails;
    memoryDetails.textContent = snapshot.memoryDetails;
    renderEdit("candidate", candidateEdit, snapshot.selectedCandidateId, snapshot.candidateEditText);
    renderEdit("memory", memoryEdit, snapshot.selectedMemoryId, snapshot.memoryEditText);
    page.querySelector("[data-candidate-editor]").hidden = !snapshot.selectedCandidateId;
    page.querySelector("[data-memory-editor]").hidden = !snapshot.selectedMemoryId || memoryEditorId !== snapshot.selectedMemoryId;
    page.querySelector("[data-use-replacement]").hidden = !snapshot.selectedCandidateId;
    const replacement = snapshot.storedMemories.find(item => item.id === snapshot.selectedMemoryId);
    const candidate = snapshot.candidates.find(item => item.id === snapshot.selectedCandidateId);
    page.querySelector("[data-replacement-note]").textContent = candidate?.hasReplacement
      ? "此候选附有更正对象。确认前，请核对原记录与新内容。"
      : replacement
      ? `要更正的记忆：${replacement.text}`
      : "如需更正已有记忆，先在已保存列表选择更正对象；候选仍保持选中。";

    renderList(candidatesList, snapshot.candidates, snapshot.selectedCandidateId, id =>
      send("memory.selectCandidate", { candidateId: id }));
    renderList(memoriesList, snapshot.storedMemories, snapshot.selectedMemoryId, id => { memoryEditorId = id; return send("memory.selectMemory", { memoryId: id }); });

    const stale = [...editDrafts.keys()].find(key => {
      const [kind, id] = key.split(":");
      const item = (kind === "memory" ? snapshot.storedMemories : snapshot.candidates).find(item => item.id === id);
      return item && editBases.get(key) !== (kind === "memory" ? item.version : item.text);
    });
    page.querySelector("[data-stale-draft]").hidden = !stale;
    if (stale) {
      const [kind, id] = stale.split(":");
      const item = (kind === "memory" ? snapshot.storedMemories : snapshot.candidates).find(item => item.id === id);
      page.querySelector("[data-current-text]").textContent = `版本已变化，草稿尚未提交。当前内容：${item.text}`;
      page.querySelector("[data-rebase-draft]").onclick = () => { editBases.set(stale, kind === "memory" ? item.version : item.text); render(); };
    }
    renderView();
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
    if (disposed || page.getAttribute("aria-busy") === "true" && snapshot !== null) return null;
    setBusy(true);
    try {
      const next = normalizeSnapshot(await context.request(type, payload));
      if (disposed) return null;
      if (next === null) { presentFailure(failureText); return null; }
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

  page.querySelector("[data-memory-settings]").addEventListener("click", event => {
    const panel = page.querySelector("[data-memory-settings-panel]"); panel.hidden = !panel.hidden;
    event.currentTarget.setAttribute("aria-expanded", String(!panel.hidden));
  });
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
  page.querySelector("[data-candidate-later]").addEventListener("click", () => { view = "confirmed"; renderView(); });
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
  page.querySelector("[data-pick-replacement]").addEventListener("click", () => {
    if (disposed || !snapshot?.selectedCandidateId) return;
    view = "confirmed";
    renderView();
    memoriesList.querySelector("button")?.focus();
  });
  page.querySelector("[data-use-replacement]").addEventListener("click", async () => {
    if (!snapshot?.selectedCandidateId || !snapshot?.selectedMemoryId) return;
    const next = await send("memory.selectReplacement", { candidateId: snapshot.selectedCandidateId });
    if (disposed || !next) return;
    view = "candidates";
    renderView();
    page.querySelector("[data-approve-candidate]").focus();
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
    if (!snapshot?.selectedMemoryId || page.querySelector("[data-single-delete]")) return;
    const memoryId = snapshot.selectedMemoryId, scope = snapshot.scope;
    const box = document.createElement("div"); box.className = "cm-confirm"; box.dataset.singleDelete = "";
    const text = document.createElement("p"); text.textContent = "删除这条记忆？此操作无法撤销，原聊天记录会保留。";
    const yes = document.createElement("button"); yes.type = "button"; yes.className = "settings-secondary settings-danger"; yes.textContent = "确认删除";
    const no = document.createElement("button"); no.type = "button"; no.className = "settings-secondary"; no.textContent = "取消";
    yes.onclick = () => { box.remove(); if (snapshot?.scope === scope) void send("memory.deleteMemory", { memoryId }); };
    no.onclick = () => box.remove(); box.append(text, yes, no); page.querySelector("[data-memory-editor]").append(box); no.focus();
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
  dispose.getState = () => ({ view, settingsOpen: !page.querySelector("[data-memory-settings-panel]").hidden, scope: draftScope, memoryEditorId, edits: [...editDrafts], editBases: [...editBases], dirty: editDrafts.size > 0 });

  renderView();
  await send("memory.get", {}, t("memory.unavailable"));
  return dispose;
}
