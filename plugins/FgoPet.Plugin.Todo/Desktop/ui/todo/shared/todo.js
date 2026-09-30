(() => {
  "use strict";
  const tasks = document.getElementById("tasks");
  const notice = document.getElementById("notice");
  const progress = document.getElementById("progress");
  const form = document.getElementById("quick-add");
  const title = document.getElementById("title");
  const manage = document.getElementById("manage-all");
  let client = null;
  let latestRevision = -1;
  let closed = false;

  function status(message, error = false) {
    notice.textContent = message;
    notice.classList.toggle("error", error);
  }

  function request(type, payload = {}) {
    return client.request(type, payload);
  }

  function render(snapshot) {
    latestRevision = snapshot.revision;
    tasks.replaceChildren();
    progress.textContent = `${snapshot.completedToday} 项已完成 · ${snapshot.tasks.length} 项待办`;
    if (!snapshot.tasks.length) {
      const empty = document.createElement("li");
      empty.textContent = "今天没有待办，按自己的节奏来。";
      tasks.append(empty);
      return;
    }
    for (const item of snapshot.tasks) {
      const row = document.createElement("li");
      const box = document.createElement("input");
      box.type = "checkbox";
      box.setAttribute("aria-label", `完成 ${item.title}`);
      const label = document.createElement("div");
      const name = document.createElement("span");
      name.className = "task-title";
      name.textContent = item.title;
      label.append(name);
      if (item.totalSteps) {
        const meta = document.createElement("span");
        meta.className = "task-meta";
        meta.textContent = `步骤 ${item.completedSteps}/${item.totalSteps}`;
        label.append(meta);
      }
      box.addEventListener("change", async () => {
        box.disabled = true;
        try {
          await request("setCompletion", { id: item.id, etag: item.etag, completed: true });
          await refresh();
        } catch (error) {
          box.checked = false;
          if (error.message === "TODO_CONFIRM_INCOMPLETE_STEPS") showStepConfirmation(row, box, item);
          else status("完成失败，请刷新后重试。", true);
        } finally { box.disabled = false; }
      });
      row.append(box, label);
      tasks.append(row);
    }
  }

  function showStepConfirmation(row, box, item) {
    if (row.querySelector(".confirmation")) return;
    const confirmation = document.createElement("div");
    confirmation.className = "confirmation";
    confirmation.setAttribute("role", "group");
    confirmation.setAttribute("aria-label", `确认完成 ${item.title}`);
    const question = document.createElement("span");
    question.textContent = "还有未完成的步骤，仍要完成吗？";
    const cancel = document.createElement("button");
    cancel.type = "button";
    cancel.textContent = "取消";
    const confirm = document.createElement("button");
    confirm.type = "button";
    confirm.textContent = "仍要完成";
    cancel.addEventListener("click", () => { confirmation.remove(); box.focus(); });
    confirmation.addEventListener("keydown", event => {
      if (event.key === "Escape") { event.preventDefault(); cancel.click(); }
    });
    confirm.addEventListener("click", async () => {
      cancel.disabled = confirm.disabled = true;
      try {
        await request("setCompletion", { id: item.id, etag: item.etag, completed: true, confirmIncompleteSteps: true });
        await refresh();
      } catch {
        status("任务已变化，请刷新后核对。", true);
        confirmation.remove();
      } finally { cancel.disabled = confirm.disabled = false; }
    });
    confirmation.append(question, cancel, confirm);
    row.append(confirmation);
    cancel.focus();
  }

  async function refresh() {
    try {
      const snapshot = await request("getPeekSnapshot");
      if (closed || snapshot.revision < latestRevision) return;
      render(snapshot);
      status("");
    } catch { status("待办读取失败，请关闭后重试。", true); tasks.replaceChildren(); progress.textContent = "读取失败"; }
  }

  client = window.FgoPetTodoBridge.create("todo", message => {
    if (message.type === "todo.changed") {
      if (message.revision > latestRevision) { status("待办已更新"); void refresh(); }
    }
  });
  if (!client.available) { status("此页面需要在 FGO Pet 中打开。", true); return; }
  form.addEventListener("submit", async event => {
    event.preventDefault();
    const value = title.value.trim();
    if (!value) { status("请先填写待办标题。", true); title.focus(); return; }
    form.querySelector("button").disabled = true;
    try { await request("quickAdd", { title: value }); title.value = ""; await refresh(); title.focus(); }
    catch { status("新增失败，请核对标题后重试。", true); }
    finally { form.querySelector("button").disabled = false; }
  });
  manage.addEventListener("click", async () => {
    try { await request("openWorkspace"); }
    catch { status("完整待办暂时无法打开。", true); }
  });
  window.addEventListener("pagehide", () => { closed = true; });
  client.ready();
  void refresh();
})();
