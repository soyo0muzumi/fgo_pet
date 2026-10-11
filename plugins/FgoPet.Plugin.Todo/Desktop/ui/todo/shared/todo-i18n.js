(() => {
 "use strict";
 const english = {
  "todo.today":"Today", "todo.todayTasks":"Today’s remembered things", "todo.remember":"Remember something", "todo.add":"Add",
  "todo.viewAll":"View all remembered things", "todo.empty":"Nothing for today. Take it at your own pace.",
  "todo.reason.inProgress":"In progress", "todo.reason.dueToday":"Due today", "todo.reason.overdue":"Overdue", "todo.reason.addedToday":"Added today",
  "todo.complete":"Complete {title}", "todo.steps":"Steps {completed}/{total}", "todo.confirm":"Confirm completion of {title}",
  "todo.incomplete":"Some steps are unfinished. Complete anyway?", "todo.cancel":"Cancel", "todo.completeAnyway":"Complete anyway",
  "todo.completeFailed":"Could not complete. Refresh and try again.", "todo.changed":"This item has changed. Refresh and check it.",
  "todo.readFailed":"Could not load remembered things. Close and try again.", "todo.updated":"Remembered things updated.",
  "todo.openInApp":"Open this page in FGO Pet.", "todo.titleRequired":"Write a title first.", "todo.addFailed":"Could not add. Check the title and try again.",
  "todo.workspaceUnavailable":"Remembered things could not be opened."
 };
 const t = (key, values = {}) => (english[key] || key).replace(/\{(\w+)\}/g, (_, name) => String(values[name] ?? ""));
 window.MarginTodoI18n = { t };
 document.querySelectorAll("[data-i18n]").forEach(node => node.textContent = t(node.dataset.i18n));
 for (const attribute of ["aria-label", "placeholder"])
  document.querySelectorAll(`[data-i18n-${attribute}]`).forEach(node => node.setAttribute(attribute, t(node.getAttribute(`data-i18n-${attribute}`))));
})();
