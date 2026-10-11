(() => {
  "use strict";
  const t = window.MarginTodoI18n.t;
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
    progress.textContent = `· ${snapshot.tasks.length}`;
    if (!snapshot.tasks.length) {
      const empty = document.createElement("li");
      empty.textContent = t("todo.empty");
      tasks.append(empty);
      return;
    }
    for (const item of snapshot.tasks) {
      const row = document.createElement("li");
      const box = document.createElement("input");
      box.type = "checkbox";
      box.setAttribute("aria-label", t("todo.complete", { title: item.title }));
      const label = document.createElement("div");
      const name = document.createElement("span");
      name.className = "task-title";
      name.textContent = item.title;
      label.append(name);
      row.dataset.reason = item.reason;
      const reason = document.createElement("span");
      reason.className = "task-reason";
      reason.textContent = t(`todo.reason.${item.reason}`);
      label.append(reason);
      if (item.totalSteps) {
        const meta = document.createElement("span");
        meta.className = "task-meta";
        meta.textContent = t("todo.steps", { completed: item.completedSteps, total: item.totalSteps });
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
          else status(t("todo.completeFailed"), true);
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
    confirmation.setAttribute("aria-label", t("todo.confirm", { title: item.title }));
    const question = document.createElement("span");
    question.textContent = t("todo.incomplete");
    const cancel = document.createElement("button");
    cancel.type = "button";
    cancel.textContent = t("todo.cancel");
    const confirm = document.createElement("button");
    confirm.type = "button";
    confirm.textContent = t("todo.completeAnyway");
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
        status(t("todo.changed"), true);
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
    } catch { status(t("todo.readFailed"), true); tasks.replaceChildren(); progress.textContent = t("todo.readFailed"); }
  }

  client = window.FgoPetTodoBridge.create("todo", message => {
    if (message.type === "todo.changed") {
      if (message.revision > latestRevision) { status(t("todo.updated")); void refresh(); }
    }
  });
  if (!client.available) { status(t("todo.openInApp"), true); return; }
  form.addEventListener("submit", async event => {
    event.preventDefault();
    const value = title.value.trim();
    if (!value) { status(t("todo.titleRequired"), true); title.focus(); return; }
    form.querySelector("button").disabled = true;
    try { await request("quickAdd", { title: value }); title.value = ""; await refresh(); title.focus(); }
    catch { status(t("todo.addFailed"), true); }
    finally { form.querySelector("button").disabled = false; }
  });
  manage.addEventListener("click", async () => {
    try { await request("openWorkspace"); }
    catch { status(t("todo.workspaceUnavailable"), true); }
  });
  window.addEventListener("pagehide", () => { closed = true; });
  client.ready();
  void refresh();
})();
