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

    <section class="cm-card" aria-label="记忆功能">
      <label class="cm-switch-row">
        <span class="cm-switch-copy">
          <span class="cm-switch-title">启用记忆</span>
          <span class="cm-switch-description">启用后模型可以在对话中产生候选记忆；只有经过你确认的记忆才会进入后续对话。</span>
        </span>
        <input type="checkbox" id="cm-enabled" data-field="memoryEnabled" />
        <span class="cm-switch-control" aria-hidden="true"><i></i></span>
      </label>
      <p class="cm-note">此项即时生效，不等刷新。</p>
    </section>

    <div class="cm-columns">
      <section class="cm-card" aria-label="待审核候选">
        <div class="cm-card-heading">
          <h2>待审核候选</h2>
          <p data-candidates-status role="status" aria-live="polite"></p>
        </div>
        <ul class="cm-list" data-candidates role="listbox" aria-label="候选记忆"></ul>
        <div class="cm-detail">
          <p class="cm-detail-text" data-candidate-details></p>
          <label class="cm-label" for="cm-candidate-edit">编辑候选正文</label>
          <textarea id="cm-candidate-edit" class="cm-input cm-textarea" data-candidate-edit rows="3"></textarea>
          <div class="cm-actions">
            <button type="button" class="cm-button" data-edit-candidate>保存编辑</button>
            <button type="button" class="cm-button cm-primary" data-approve-candidate>确认</button>
            <button type="button" class="cm-button" data-reject-candidate>拒绝</button>
            <button type="button" class="cm-button" data-select-replacement>更正选中记忆</button>
          </div>
        </div>
      </section>

      <section class="cm-card" aria-label="已确认记忆">
        <div class="cm-card-heading">
          <h2>已确认记忆</h2>
          <p data-memories-status role="status" aria-live="polite"></p>
        </div>
        <ul class="cm-list" data-memories role="listbox" aria-label="已确认记忆"></ul>
        <div class="cm-detail">
          <p class="cm-detail-text" data-memory-details></p>
          <label class="cm-label" for="cm-memory-edit">编辑记忆正文</label>
          <textarea id="cm-memory-edit" class="cm-input cm-textarea" data-memory-edit rows="3"></textarea>
          <div class="cm-actions">
            <button type="button" class="cm-button" data-edit-memory>保存编辑</button>
            <button type="button" class="cm-button" data-disable-memory>停用</button>
            <button type="button" class="cm-button" data-enable-memory>启用</button>
            <button type="button" class="cm-button cm-danger" data-delete-memory>删除</button>
          </div>
        </div>
      </section>
    </div>

    <section class="cm-card" aria-label="维护">
      <div class="cm-actions">
        <button type="button" class="cm-button" data-refresh>刷新</button>
        <button type="button" class="cm-button cm-danger" data-delete-all>删除全部用户数据</button>
      </div>
      <div class="cm-confirm" data-delete-confirm hidden role="group" aria-label="删除全部确认">
        <p>删除全部用户数据不可撤销（不含专注与羁绊历史）。确认继续？</p>
        <div class="cm-actions">
          <button type="button" class="cm-button cm-danger" data-delete-confirm-btn>确认删除</button>
          <button type="button" class="cm-button" data-delete-cancel>取消</button>
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
      ? errorText : "这一页暂时无法使用。";
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
      empty.textContent = "暂无条目。";
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

      if (item.hasReplacement === true) button.append(badge("更正项", ""));
      if (item.isEnabled === false) button.append(badge("已停用", " cm-badge-muted"));
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

  async function send(type, payload = {}, failureText = "操作未完成，已回读当前状态。你可以重试。") {
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

  await send("memory.get", {}, "这一页暂时无法使用。");
  return dispose;
}
