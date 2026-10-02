// 统一 Web 设置：数据与隐私页。
// 命令（经 context.request 调用，pageId 由 root 自动注入）：
//   privacy.get / privacy.export / privacy.createBackup / privacy.restoreBackup / privacy.deleteAll
// 本页是最高危路径：删除与恢复都必须在页面内联二段式确认；文件位置一律由宿主原生 picker 决定，
// 页面不出现、也不传递任何本机路径。
const OWNER = "privacy";

const MARKUP = `
  <section class="pp-page" data-state="loading" aria-busy="true">
    <p class="pp-error" data-error role="alert" aria-live="assertive" hidden></p>

    <section class="pp-card" aria-label="导出用户数据">
      <h2>导出用户数据</h2>
      <p class="pp-note">导出对话、摘要、候选、已确认记忆与安全的内容元数据。API Key、完整 Prompt、原始剧情与角色包资产不会包含在导出中。</p>
      <div class="pp-actions">
        <button type="button" class="pp-button pp-primary" data-export>导出到所选文件…</button>
      </div>
      <p class="pp-status" data-export-status role="status" aria-live="polite"></p>
    </section>

    <section class="pp-card" aria-label="私有备份与恢复">
      <h2>私有备份与恢复</h2>
      <p class="pp-note">私有备份使用 .fgopetbackup 保存可恢复的业务数据、非敏感设置和角色包引用；不包含 API Key、Agent 凭据、原始角色包资产或 Prompt。</p>
      <div class="pp-actions">
        <button type="button" class="pp-button pp-primary" data-create-backup>创建私有备份…</button>
        <button type="button" class="pp-button pp-danger" data-restore-backup>恢复私有备份…</button>
      </div>
      <p class="pp-status" data-backup-status role="status" aria-live="polite"></p>

      <div class="pp-confirm" data-restore-confirm hidden role="group" aria-label="确认恢复私有备份">
        <p>备份校验后将关闭应用，请重新打开以完成恢复。恢复会替换当前业务数据；API Key 和 Agent 凭据不会恢复，进行中的 Agent 任务需要重新核对且不会自动重新派发。</p>
        <div class="pp-actions">
          <button type="button" class="pp-button pp-danger" data-restore-confirm-btn>确认恢复</button>
          <button type="button" class="pp-button" data-restore-cancel>取消</button>
        </div>
      </div>
    </section>

    <section class="pp-card" aria-label="删除全部用户数据">
      <h2 class="pp-danger-title">删除全部用户数据</h2>
      <p class="pp-note">删除全部对话、记忆、称呼设置和模型连接元数据。此操作不可撤销，且不影响专注与羁绊历史。</p>
      <div class="pp-actions">
        <button type="button" class="pp-button pp-danger" data-delete-all>删除全部用户数据</button>
      </div>
      <p class="pp-status" data-delete-status role="status" aria-live="polite"></p>

      <div class="pp-confirm" data-delete-confirm hidden role="group" aria-label="确认删除全部用户数据">
        <p>确定删除全部对话、记忆、称呼设置和模型连接吗？此操作不可撤销。</p>
        <div class="pp-actions">
          <button type="button" class="pp-button pp-danger" data-delete-confirm-btn>确认删除</button>
          <button type="button" class="pp-button" data-delete-cancel>取消</button>
        </div>
      </div>
    </section>
  </section>
`;

function isSnapshot(candidate) {
  return candidate !== null && typeof candidate === "object"
    && typeof candidate.exportStatusText === "string"
    && typeof candidate.backupStatusText === "string"
    && typeof candidate.deleteStatusText === "string"
    && typeof candidate.canExport === "boolean"
    && typeof candidate.canCreateBackup === "boolean"
    && typeof candidate.canRestoreBackup === "boolean";
}

function normalizeSnapshot(candidate) {
  if (!isSnapshot(candidate)) return null;
  return {
    exportStatusText: candidate.exportStatusText,
    backupStatusText: candidate.backupStatusText,
    deleteStatusText: candidate.deleteStatusText,
    canExport: candidate.canExport,
    canCreateBackup: candidate.canCreateBackup,
    canRestoreBackup: candidate.canRestoreBackup,
    isBusy: candidate.isBusy === true,
  };
}

export async function mount(container, context) {
  container.innerHTML = MARKUP;
  const page = container.querySelector(".pp-page");
  const link = document.createElement("link");
  link.rel = "stylesheet";
  link.href = new URL("./privacy.css", import.meta.url).href;
  link.dataset.settingsOwner = OWNER;
  document.head.append(link);

  const errorEl = page.querySelector("[data-error]");
  const exportStatus = page.querySelector("[data-export-status]");
  const backupStatus = page.querySelector("[data-backup-status]");
  const deleteStatus = page.querySelector("[data-delete-status]");
  const exportButton = page.querySelector("[data-export]");
  const createBackupButton = page.querySelector("[data-create-backup]");
  const restoreButton = page.querySelector("[data-restore-backup]");
  const restoreConfirm = page.querySelector("[data-restore-confirm]");
  const deleteConfirm = page.querySelector("[data-delete-confirm]");

  let snapshot = null;
  let disposed = false;
  let unsubscribeTheme = null;

  // 禁用态的唯一出口：busy 优先，其次服务可用性。所有按钮都由这里裁决，
  // 避免「busy 结束无条件解禁」覆盖可用性判断。
  function syncDisabled() {
    const busy = page.getAttribute("aria-busy") === "true";
    for (const control of page.querySelectorAll("button")) control.disabled = busy;
    if (busy || snapshot === null) return;
    exportButton.disabled = !snapshot.canExport;
    createBackupButton.disabled = !snapshot.canCreateBackup;
    restoreButton.disabled = !snapshot.canRestoreBackup;
  }

  function setBusy(busy) {
    page.setAttribute("aria-busy", busy ? "true" : "false");
    syncDisabled();
  }

  function presentFailure(errorText) {
    errorEl.textContent = typeof errorText === "string" && errorText.length > 0
      ? errorText : "这一页暂时无法使用。";
    errorEl.hidden = false;
    page.dataset.state = "error";
    setBusy(false);
  }

  function render() {
    if (snapshot === null) return;
    // 状态文案来自后端（VM / 服务），一律用 textContent，绝不注入为 HTML。
    exportStatus.textContent = snapshot.exportStatusText;
    backupStatus.textContent = snapshot.backupStatusText;
    deleteStatus.textContent = snapshot.deleteStatusText;
    syncDisabled();
  }

  // 失败后回读权威快照：错误码（error.message）是给日志的，不能直接呈现给用户，
  // 页面只给可读的一句话，并用最新快照覆盖本地状态。
  async function reload() {
    try {
      const next = normalizeSnapshot(await context.request("privacy.get", {}));
      if (!disposed && next !== null) { snapshot = next; render(); }
    } catch { /* 回读失败时保留上一次快照；错误横幅已经给出提示。 */ }
  }

  async function send(type, failureText = "操作未完成，已回读当前状态。你可以重试。") {
    if (disposed) return null;
    setBusy(true);
    try {
      const next = normalizeSnapshot(await context.request(type, {}));
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

  exportButton.addEventListener("click", () => { void send("privacy.export"); });
  createBackupButton.addEventListener("click", () => { void send("privacy.createBackup"); });

  // 恢复与删除全部都是最高危操作：一律页面内联二段式确认，不用 window.confirm。
  restoreButton.addEventListener("click", () => {
    restoreConfirm.hidden = false;
    page.querySelector("[data-restore-confirm-btn]").focus();
  });
  page.querySelector("[data-restore-cancel]").addEventListener("click", () => { restoreConfirm.hidden = true; });
  page.querySelector("[data-restore-confirm-btn]").addEventListener("click", () => {
    restoreConfirm.hidden = true;
    void send("privacy.restoreBackup");
  });

  page.querySelector("[data-delete-all]").addEventListener("click", () => {
    deleteConfirm.hidden = false;
    page.querySelector("[data-delete-confirm-btn]").focus();
  });
  page.querySelector("[data-delete-cancel]").addEventListener("click", () => { deleteConfirm.hidden = true; });
  page.querySelector("[data-delete-confirm-btn]").addEventListener("click", () => {
    deleteConfirm.hidden = true;
    void send("privacy.deleteAll");
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
    for (const control of page.querySelectorAll("button")) control.disabled = true;
    page.dataset.state = "disposed";
  }

  await send("privacy.get", "这一页暂时无法使用。");
  return dispose;
}
