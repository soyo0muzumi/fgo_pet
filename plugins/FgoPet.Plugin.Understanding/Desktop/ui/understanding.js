export async function mount(container, context) {
  const style = document.createElement("link");
  style.rel = "stylesheet"; style.href = "pages/understanding.css"; document.head.append(style);
  container.innerHTML = `<section class="understanding-page"><p role="alert" data-error hidden></p>
    <p>理解条目是带来源和适用条件的派生判断。用户批准保存不代表事实认证。自动复盘尚未启用；此页操作不会发起模型请求。</p>
    <form data-policy><label>积累模式 <select name="mode"><option value="off">OFF：仅手动预览</option><option value="proposals">PROPOSALS：保存候选，逐条批准</option><option value="bounded">BOUNDED：经宿主策略允许的低影响条目可暂定生效</option></select></label>
      <label><input type="checkbox" name="enabled">启用理解读取与复核</label>
      <label><input type="checkbox" name="readSources">允许读取当前角色、当前项目的原始消息</label>
      <label><input type="checkbox" name="externalProcessing">允许获授权的复盘将来源发给当前模型（本阶段不发起）</label>
      <label><input type="checkbox" name="saveCandidates">允许保存新候选；关闭后预览只留在内存</label>
      <label><input type="checkbox" name="useInDialogue">允许有效条目参与后续对话</label>
      <p>降级会停止未审核暂定条目生效；重新开启不会批量恢复。撤销来源或对话使用许可会停止上下文读取。删除保留不含正文的哈希撤销标记，防止旧备份恢复它。</p>
      <button type="submit">保存权限与模式</button></form>
    <nav aria-label="理解视图"><button type="button" data-view="accumulated" aria-pressed="true">已积累</button><button type="button" data-view="pending" aria-pressed="false">待批准</button><button type="button" data-refresh>刷新</button></nav>
    <p data-status role="status"></p><div data-entries></div></section>`;
  const root = container.querySelector(".understanding-page"), form = root.querySelector("form");
  let snapshot, view = "accumulated", disposed = false, busy = false;
  const error = root.querySelector("[data-error]");
  const fields = ["enabled", "readSources", "externalProcessing", "saveCandidates", "useInDialogue"];
  function controls(disabled) { for (const el of root.querySelectorAll("button,input,select,textarea")) el.disabled = disabled; }
  async function send(type, payload = {}) {
    if (disposed || busy) return;
    busy = true; controls(true); error.hidden = true;
    try { const next = await context.request(type, payload); if (!disposed) { snapshot = next; render(); } }
    catch { if (!disposed) { error.hidden = false; error.textContent = "状态已变化或操作未完成，请刷新后按当前版本重试。"; } }
    finally { busy = false; if (!disposed) controls(false); }
  }
  function add(parent, tag, text) { const el = document.createElement(tag); el.textContent = text; parent.append(el); return el; }
  function render() {
    if (!snapshot?.state?.policy) return;
    const policy = snapshot.state.policy;
    form.elements.mode.value = policy.mode;
    for (const name of fields) form.elements[name].checked = policy[name] === true;
    root.querySelector("[data-status]").textContent = `模式：${policy.mode}；版本：${snapshot.state.revision}；角色：${snapshot.scope?.servantId || "当前角色"}；项目：${snapshot.scope?.projectId || "角色通用"}。`;
    const list = root.querySelector("[data-entries]"); list.replaceChildren();
    const items = snapshot[view] || [];
    if (!items.length) add(list, "p", "当前没有条目。");
    for (const item of items) {
      const card = document.createElement("article"); list.append(card);
      add(card, "h3", `${item.domain} / ${item.kind} · v${item.revision}`);
      add(card, "p", item.content); add(card, "p", `条件：${item.conditions}`);
      add(card, "p", `复核：${item.review}；生效：${item.effect}；生命周期：${item.life}；依据：${item.basisKind}`);
      add(card, "p", `观察：${item.observedAt}；活跃截止：${item.activeUntil}`);
      if (item.correction) add(card, "p", `当前纠正（优先）：${item.correction}`);
      const history = document.createElement("details"); card.append(history); add(history, "summary", "最近变更（至多20版）");
      for (const prior of snapshot.changes?.[item.id] || []) add(history, "p", `v${prior.revision}：${prior.review} / ${prior.effect} / ${prior.life}${prior.correction ? `；纠正：${prior.correction}` : ""}`);
      for (const source of item.sources || []) add(card, "p", `来源：${source.kind}，${source.occurredAtUtc}，消息 ${source.messageId}，版本 ${source.fingerprint}`);
      if (!item.coverage?.length) add(card, "p", "已读覆盖：未附带证明；来源版本有效不代表已读取全部原文。");
      for (const coverage of item.coverage || []) add(card, "p", `已读覆盖：消息 ${coverage.source.messageId}，从第 ${coverage.startScalar} 个字符起读取 ${coverage.includedScalars} / ${coverage.totalScalars} 个字符；${coverage.complete ? "完整" : "部分"}。`);
      const edit = add(card, "textarea", item.correction || ""); edit.setAttribute("aria-label", "本条纠正"); edit.maxLength = 2000;
      const actions = item.review === "rejected" ? ["delete"] : view === "pending" ? ["approve", "reject", "correct", "delete"] : ["suspend", "resolve", "restore", "correct", "delete"];
      const labels = { approve: "批准此版本", reject: "拒绝", suspend: "暂停", resolve: "标记已解决", restore: "复核恢复", correct: "保存纠正", delete: "删除" };
      for (const action of actions) {
        const button = add(card, "button", labels[action]); button.type = "button";
        button.addEventListener("click", () => {
          if (action === "delete") {
            button.disabled = true;
            const confirmation = add(card, "button", "确认删除此条及全部历史版本"); confirmation.type = "button";
            confirmation.addEventListener("click", () => void review(action), { once: true });
            return;
          }
          void review(action);
        });
        async function review(action) {
          await send("understanding.review", { operationId: crypto.randomUUID(), id: item.id, revision: item.revision, action,
            ...(action === "correct" ? { correction: edit.value } : {}) });
        }
      }
    }
  }
  form.addEventListener("submit", event => {
    event.preventDefault(); if (!snapshot) return;
    const policy = { mode: form.elements.mode.value };
    for (const name of fields) policy[name] = form.elements[name].checked;
    void send("understanding.configure", { revision: snapshot.state.revision, policy });
  });
  for (const button of root.querySelectorAll("[data-view]")) button.addEventListener("click", () => {
    view = button.dataset.view;
    for (const tab of root.querySelectorAll("[data-view]")) tab.setAttribute("aria-pressed", String(tab === button));
    render();
  });
  root.querySelector("[data-refresh]").addEventListener("click", () => void send("understanding.get"));
  const dispose = () => { disposed = true; controls(true); style.remove(); };
  context.signal.addEventListener("abort", dispose, { once: true });
  await send("understanding.get"); return dispose;
}
