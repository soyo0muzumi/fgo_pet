(() => {
  "use strict";
  const list = document.getElementById("workspace-tasks");
  const notice = document.getElementById("notice");
  const undoZone = document.getElementById("undo-zone");
  const summary = document.getElementById("summary");
  const addForm = document.getElementById("new-task");
  const addTitle = document.getElementById("new-title");
  const addDescription = document.getElementById("new-description");
  const search = document.getElementById("task-search");
  const tabs = Object.fromEntries(["active", "today", "inbox", "upcoming", "all", "completed"]
    .map(key => [key, document.getElementById(`${key}-tab`)]));
  const viewNames = { active: "进行中", today: "今天", inbox: "收件箱", upcoming: "即将到来", all: "全部", completed: "已完成" };
  const currentView = document.getElementById("current-view");
  let client = null;
  let latestRevision = -1;
  let snapshot = { active: [], today: [], inbox: [], upcoming: [], all: [], completed: [] };
  let tab = "active";
  let expandedId = null;
  let draft = null;
  let stepDraft = null;
  let closed = false;
  let undoTimer = null;
  let focusedItemId = null;
  let composing = false;
  let refreshAfterComposition = false;
  let refreshRequest = 0;
  let mobileStage = "list";

  function endComposition() {
    composing = false;
    if (refreshAfterComposition) { refreshAfterComposition = false; void refresh(); }
  }

  function offerUndo(token) {
    if (undoTimer !== null) clearTimeout(undoTimer);
    undoZone.replaceChildren();
    const label = document.createElement("span");
    label.textContent = "已完成任务。";
    const control = button("撤销完成", async () => {
      control.disabled = true;
      try { await request("undoCompletion", { undoToken: token }); undoZone.replaceChildren(); await refresh(); }
      catch { status("无法撤销：任务可能已变化或 8 秒期限已过。", true); undoZone.replaceChildren(); }
    });
    undoZone.append(label, control);
    undoTimer = setTimeout(() => { undoZone.replaceChildren(); undoTimer = null; }, 8000);
  }

  function status(text, error = false) {
    notice.textContent = text;
    notice.classList.toggle("error", error);
  }

  function request(type, payload = {}) {
    return client.request(type, payload);
  }

  function button(text, action, className) {
    const control = document.createElement("button");
    control.type = "button";
    control.textContent = text;
    if (className) control.className = className;
    control.addEventListener("click", action);
    return control;
  }

  function allItems() { return [...snapshot.active, ...snapshot.completed]; }
  function findTask(id) { return allItems().find(item => item.id === id); }

  function render() {
    document.body.dataset.mobileStage = mobileStage;
    currentView.textContent = viewNames[tab];
    for (const [key, control] of Object.entries(tabs))
      control.setAttribute("aria-pressed", String(tab === key));
    addForm.hidden = tab === "completed";
    summary.textContent = `${snapshot.active.length} 项进行中 · ${snapshot.completed.length} 项已完成`;
    list.replaceChildren();
    const term = search.value.trim().toLocaleLowerCase();
    const items = snapshot[tab].filter(item => !term ||
      [item.title, item.description || "", ...item.steps.map(step => step.title)]
        .some(value => value.toLocaleLowerCase().includes(term)));
    if (mobileStage === "detail" && !items.some(item => item.id === expandedId)) {
      mobileStage = "list";
      document.body.dataset.mobileStage = mobileStage;
    }
    if (!items.length) {
      const empty = document.createElement("li");
      empty.className = "empty-state";
      empty.textContent = term ? "没有匹配的待办。" : tab === "completed" ? "还没有已完成的待办。" : "这里还没有待办。";
      list.append(empty);
      return;
    }
    for (const item of items) list.append(renderTask(item));
  }

  function renderTask(item) {
    const row = document.createElement("li");
    row.dataset.id = item.id;
    row.classList.toggle("is-expanded", expandedId === item.id);
    row.classList.toggle("is-highlighted", focusedItemId === item.id);
    const heading = document.createElement("div");
    heading.className = "task-heading";
    const title = document.createElement("h2");
    title.textContent = item.title;
    const toggle = button(expandedId === item.id ? "收起" : "详情", () => {
      expandedId = expandedId === item.id ? null : item.id;
      mobileStage = expandedId ? "detail" : "list";
      render();
      list.querySelector(`[data-id="${CSS.escape(item.id)}"] .task-heading button`)?.focus();
    });
    toggle.setAttribute("aria-expanded", String(expandedId === item.id));
    heading.append(title, toggle);
    row.append(heading);
    if (item.steps.length) {
      const progress = document.createElement("span");
      progress.className = "task-meta";
      progress.textContent = `${item.steps.filter(step => step.isCompleted).length}/${item.steps.length} 步`;
      row.append(progress);
    }
    if (expandedId !== item.id) return row;
    if (item.description) {
      const description = document.createElement("p");
      description.className = "task-description";
      description.textContent = item.description;
      row.append(description);
    }
    renderSteps(row, item);
    if (draft?.id === item.id) renderEditor(row, item);
    else renderActions(row, item);
    return row;
  }

  function orderedSteps(steps) {
    return steps.map((step, index) => ({ id: step.id, title: step.title, order: index,
      isCompleted: step.isCompleted }));
  }

  async function saveSteps(item, steps, etag = item.etag, onSuccess = () => {}) {
    try {
      await request("updateTask", { id: item.id, etag, title: item.title,
        description: item.description, steps: orderedSteps(steps) });
      onSuccess();
      await refresh();
    } catch { status("步骤保存失败，任务可能已变化；当前输入已保留。", true); }
  }

  function renderSteps(row, item) {
    const editable = item.status !== "completed" && !item.isProtected && !draft;
    if (item.steps.length) {
      const steps = document.createElement("ol");
      steps.className = "step-list";
      for (const [index, step] of item.steps.entries()) {
        const entry = document.createElement("li");
        entry.dataset.stepId = step.id;
        if (stepDraft?.taskId === item.id && stepDraft.stepId === step.id) {
          renderStepEditor(entry, item, step);
        } else {
          const label = document.createElement("label");
          const check = document.createElement("input");
          check.type = "checkbox";
          check.checked = step.isCompleted;
          check.disabled = !editable;
          check.setAttribute("aria-label", `完成步骤 ${step.title}`);
          check.addEventListener("change", () => void saveSteps(item,
            item.steps.map(current => current.id === step.id
              ? { ...current, isCompleted: check.checked } : current)));
          label.append(check, document.createTextNode(step.title));
          entry.append(label);
          if (editable) {
            const actions = document.createElement("span");
            actions.className = "step-actions";
            actions.append(button("编辑", () => {
              stepDraft = { taskId: item.id, stepId: step.id, etag: item.etag, title: step.title };
              render();
              list.querySelector('.step-editor input')?.focus();
            }));
            const remove = button("删除", () => {
              actions.replaceChildren(button("取消", () => render()),
                button("确认删除", () => void saveSteps(item,
                  item.steps.filter(current => current.id !== step.id))));
            });
            actions.append(remove);
            const up = button("上移", () => moveStep(item, index, -1));
            up.disabled = index === 0;
            const down = button("下移", () => moveStep(item, index, 1));
            down.disabled = index === item.steps.length - 1;
            actions.append(up, down);
            entry.append(actions);
          }
        }
        steps.append(entry);
      }
      row.append(steps);
    }
    if (editable && item.steps.length < 20 && !stepDraft) {
      const form = document.createElement("form");
      form.className = "step-editor";
      const label = document.createElement("label");
      label.textContent = "新增步骤";
      const input = document.createElement("input");
      input.maxLength = 200;
      label.append(input);
      const add = document.createElement("button");
      add.type = "submit";
      add.textContent = "新增步骤";
      form.append(label, add);
      form.addEventListener("submit", event => {
        event.preventDefault();
        const title = input.value.trim();
        if (!title) { status("请输入步骤名称。", true); input.focus(); return; }
        add.disabled = true;
        const id = `step-${crypto.randomUUID().replaceAll("-", "")}`;
        void saveSteps(item, [...item.steps, { id, title, order: item.steps.length, isCompleted: false }],
          item.etag, () => { input.value = ""; }).finally(() => { add.disabled = false; });
      });
      row.append(form);
    }
  }

  function renderStepEditor(entry, item, step) {
    const form = document.createElement("form");
    form.className = "step-editor";
    const input = document.createElement("input");
    input.maxLength = 200;
    input.value = stepDraft.title;
    const save = document.createElement("button");
    save.type = "submit";
    save.textContent = "保存步骤";
    form.append(input, save, button("取消", () => { stepDraft = null; render(); }));
    input.addEventListener("input", () => { stepDraft.title = input.value; });
    input.addEventListener("compositionstart", () => { composing = true; });
    input.addEventListener("compositionend", endComposition);
    input.addEventListener("keydown", event => {
      if (event.key === "Escape") { event.preventDefault(); stepDraft = null; render(); }
      if (event.key === "Enter" && (composing || event.isComposing || event.keyCode === 229)) event.preventDefault();
    });
    form.addEventListener("submit", event => {
      event.preventDefault();
      stepDraft.title = input.value;
      if (!stepDraft.title.trim()) { status("请输入步骤名称；草稿已保留。", true); return; }
      save.disabled = true;
      void saveSteps(item, item.steps.map(current => current.id === step.id
        ? { ...current, title: stepDraft.title } : current), stepDraft.etag,
        () => { stepDraft = null; }).finally(() => { save.disabled = false; });
    });
    entry.append(form);
  }

  function moveStep(item, index, delta) {
    const steps = [...item.steps];
    [steps[index], steps[index + delta]] = [steps[index + delta], steps[index]];
    void saveSteps(item, steps);
  }

  function renderActions(row, item) {
    const actions = document.createElement("div");
    actions.className = "task-actions";
    if (item.status === "completed") {
      actions.append(button("恢复", () => void setCompletion(row, item, false)));
    } else {
      const complete = button("完成", () => void setCompletion(row, item, true));
      complete.disabled = item.isProtected;
      actions.append(complete);
      const edit = button("编辑", () => {
        draft = { id: item.id, etag: item.etag, title: item.title, description: item.description || "" };
        render();
        row = list.querySelector(`[data-id="${CSS.escape(item.id)}"]`);
        row?.querySelector(".task-editor input")?.focus();
      });
      edit.disabled = item.isProtected;
      actions.append(edit);
    }
    const remove = button("删除", () => showDeleteConfirmation(row, item));
    remove.disabled = item.isProtected;
    actions.append(remove);
    row.append(actions);
  }

  function renderEditor(row, item) {
    const form = document.createElement("form");
    form.className = "task-editor";
    const titleLabel = document.createElement("label");
    titleLabel.textContent = "标题";
    const title = document.createElement("input");
    title.maxLength = 500;
    title.value = draft.title;
    const descriptionLabel = document.createElement("label");
    descriptionLabel.textContent = "备注";
    const description = document.createElement("textarea");
    description.maxLength = 4000;
    description.rows = 3;
    description.value = draft.description;
    titleLabel.append(title);
    descriptionLabel.append(description);
    const actions = document.createElement("div");
    actions.className = "task-actions";
    const save = document.createElement("button");
    save.type = "submit";
    save.textContent = "保存";
    actions.append(save, button("取消", () => { draft = null; render(); }));
    form.append(titleLabel, descriptionLabel, actions);
    title.addEventListener("input", () => { draft.title = title.value; });
    description.addEventListener("input", () => { draft.description = description.value; });
    title.addEventListener("compositionstart", () => { composing = true; });
    title.addEventListener("compositionend", endComposition);
    title.addEventListener("keydown", event => {
      if (event.key === "Escape") { event.preventDefault(); draft = null; render(); }
      if (event.key === "Enter" && (composing || event.isComposing || event.keyCode === 229)) event.preventDefault();
    });
    form.addEventListener("submit", async event => {
      event.preventDefault();
      draft.title = title.value;
      draft.description = description.value;
      if (!draft.title.trim()) { status("请输入标题；草稿已保留。", true); title.focus(); return; }
      save.disabled = true;
      try {
        await request("updateTask", { id: item.id, etag: draft.etag,
          title: draft.title, description: draft.description });
        draft = null;
        status("已保存");
        await refresh();
      } catch {
        status("保存失败，草稿已保留；任务可能已变化。", true);
        save.disabled = false;
      }
    });
    row.append(form);
  }

  function showDeleteConfirmation(row, item) {
    if (row.querySelector(".inline-confirmation")) return;
    const confirmation = document.createElement("div");
    confirmation.className = "inline-confirmation";
    confirmation.setAttribute("role", "group");
    confirmation.setAttribute("aria-label", `删除 ${item.title}`);
    const question = document.createElement("span");
    question.textContent = `确定删除“${item.title}”？`;
    const cancel = button("取消", () => confirmation.remove());
    const confirm = button("删除", async () => {
      confirm.disabled = true;
      try { await request("deleteTask", { id: item.id, etag: item.etag }); await refresh(); }
      catch { status("删除失败，任务可能已变化。", true); confirmation.remove(); }
    });
    confirmation.addEventListener("keydown", event => {
      if (event.key === "Escape") { event.preventDefault(); cancel.click(); }
    });
    confirmation.append(question, cancel, confirm);
    row.append(confirmation);
    cancel.focus();
  }

  async function setCompletion(row, item, completed, confirmed = false) {
    try {
      const result = await request("setCompletion", { id: item.id, etag: item.etag,
        completed, confirmIncompleteSteps: confirmed });
      status(completed ? "已完成" : "已恢复");
      if (completed && result.undoToken) offerUndo(result.undoToken);
      await refresh();
    } catch (error) {
      if (completed && !confirmed && error.message === "TODO_CONFIRM_INCOMPLETE_STEPS") {
        const confirmation = document.createElement("div");
        confirmation.className = "inline-confirmation";
        const question = document.createElement("span");
        question.textContent = "还有未完成的步骤，仍要完成吗？";
        const cancel = button("取消", () => confirmation.remove());
        const confirm = button("仍要完成", () => void setCompletion(row, item, true, true));
        confirmation.append(question, cancel, confirm);
        row.append(confirmation);
        cancel.focus();
      } else status("操作失败，任务可能已变化；请刷新后核对。", true);
    }
  }

  async function refresh() {
    if (composing) { refreshAfterComposition = true; return; }
    const requestNumber = ++refreshRequest;
    try {
      const next = await request("queryTasks");
      if (closed || requestNumber !== refreshRequest || next.revision < latestRevision) return;
      latestRevision = next.revision;
      snapshot = { active: next.active, today: next.today, inbox: next.inbox,
        upcoming: next.upcoming, all: next.all, completed: next.completed };
      if (focusedItemId) {
        const target = findTask(focusedItemId);
        if (target) { tab = target.status === "completed" ? "completed" : "active"; mobileStage = "detail"; }
      }
      render();
      if (focusedItemId) list.querySelector(`[data-id="${CSS.escape(focusedItemId)}"]`)?.scrollIntoView({ block: "nearest" });
      if (!draft && !stepDraft) status("");
    } catch {
      if (requestNumber !== refreshRequest) return;
      snapshot = { active: [], today: [], inbox: [], upcoming: [], all: [], completed: [] };
      latestRevision = -1;
      render();
      status("待办读取失败，请关闭后重试；旧列表已清空。", true);
    }
  }

  client = window.FgoPetTodoBridge.create("workspace", message => {
    if (message.type === "todo.changed") {
      if (message.revision > latestRevision) void refresh();
    } else if (message.type === "workspace.navigate") {
      if (message.itemId) {
        focusedItemId = message.itemId;
        expandedId = message.itemId;
        const target = findTask(expandedId);
        if (target) { tab = target.status === "completed" ? "completed" : "active"; mobileStage = "detail"; }
        render();
        list.querySelector(`[data-id="${CSS.escape(expandedId)}"]`)?.scrollIntoView({ block: "nearest" });
      } else if (message.kind === "NewItem") { tab = "active"; mobileStage = "list"; render(); addTitle.focus(); }
    }
  });
  if (!client.available) { status("此页面需要在 FGO Pet 中打开。", true); return; }
  for (const [key, control] of Object.entries(tabs)) control.addEventListener("click", () => {
    focusedItemId = null;
    tab = key;
    mobileStage = "list";
    render();
  });
  document.getElementById("back-to-navigation").addEventListener("click", () => {
    mobileStage = "navigation";
    render();
    tabs[tab].focus();
  });
  document.getElementById("back-to-list").addEventListener("click", () => {
    mobileStage = "list";
    render();
    list.querySelector(`[data-id="${CSS.escape(expandedId || "")}"] .task-heading button`)?.focus();
  });
  search.addEventListener("input", render);
  addForm.addEventListener("submit", async event => {
    event.preventDefault();
    const title = addTitle.value.trim();
    if (!title) { status("请输入标题；草稿已保留。", true); addTitle.focus(); return; }
    const save = addForm.querySelector("button[type=submit]");
    save.disabled = true;
    try {
      const created = await request("createTask", { title, description: addDescription.value });
      addTitle.value = addDescription.value = "";
      expandedId = created.id;
      tab = "active";
      mobileStage = "detail";
      await refresh();
      list.querySelector(`[data-id="${CSS.escape(created.id)}"] .task-heading button`)?.focus();
    } catch { status("新增失败，输入已保留。", true); }
    finally { save.disabled = false; }
  });
  window.addEventListener("pagehide", () => { closed = true; if (undoTimer !== null) clearTimeout(undoTimer); });
  client.ready();
  void refresh();
})();
