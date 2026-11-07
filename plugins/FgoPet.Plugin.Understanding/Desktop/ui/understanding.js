/* Selected Lucide v1.8.0 vector paths; https://lucide.dev
ISC License

Copyright (c) 2026 Lucide Icons and Contributors

Permission to use, copy, modify, and/or distribute this software for any
purpose with or without fee is hereby granted, provided that the above
copyright notice and this permission notice appear in all copies.

THE SOFTWARE IS PROVIDED "AS IS" AND THE AUTHOR DISCLAIMS ALL WARRANTIES
WITH REGARD TO THIS SOFTWARE INCLUDING ALL IMPLIED WARRANTIES OF
MERCHANTABILITY AND FITNESS. IN NO EVENT SHALL THE AUTHOR BE LIABLE FOR
ANY SPECIAL, DIRECT, INDIRECT, OR CONSEQUENTIAL DAMAGES OR ANY DAMAGES
WHATSOEVER RESULTING FROM LOSS OF USE, DATA OR PROFITS, WHETHER IN AN
ACTION OF CONTRACT, NEGLIGENCE OR OTHER TORTIOUS ACTION, ARISING OUT OF
OR IN CONNECTION WITH THE USE OR PERFORMANCE OF THIS SOFTWARE.

---

The following Lucide icons are derived from the Feather project:

airplay, alert-circle, alert-octagon, alert-triangle, aperture, arrow-down-circle, arrow-down-left, arrow-down-right, arrow-down, arrow-left-circle, arrow-left, arrow-right-circle, arrow-right, arrow-up-circle, arrow-up-left, arrow-up-right, arrow-up, at-sign, calendar, cast, check, chevron-down, chevron-left, chevron-right, chevron-up, chevrons-down, chevrons-left, chevrons-right, chevrons-up, circle, clipboard, clock, code, columns, command, compass, corner-down-left, corner-down-right, corner-left-down, corner-left-up, corner-right-down, corner-right-up, corner-up-left, corner-up-right, crosshair, database, divide-circle, divide-square, dollar-sign, download, external-link, feather, frown, hash, headphones, help-circle, info, italic, key, layout, life-buoy, link-2, link, loader, lock, log-in, log-out, maximize, meh, minimize, minimize-2, minus-circle, minus-square, minus, monitor, moon, more-horizontal, more-vertical, move, music, navigation-2, navigation, octagon, pause-circle, percent, plus-circle, plus-square, plus, power, radio, rss, search, server, share, shopping-bag, sidebar, smartphone, smile, square, table-2, tablet, target, terminal, trash-2, trash, triangle, tv, type, upload, x-circle, x-octagon, x-square, x, zoom-in, zoom-out

The MIT License (MIT) (for the icons listed above)

Copyright (c) 2013-present Cole Bemis

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
*/
// Local, decorative SVGs use the same icon family as the Margin reference Site.
const iconShapes = {
  brain: [
  ["path", { d: "M12 18V5" }],
  ["path", { d: "M15 13a4.17 4.17 0 0 1-3-4 4.17 4.17 0 0 1-3 4" }],
  ["path", { d: "M17.598 6.5A3 3 0 1 0 12 5a3 3 0 1 0-5.598 1.5" }],
  ["path", { d: "M17.997 5.125a4 4 0 0 1 2.526 5.77" }],
  ["path", { d: "M18 18a4 4 0 0 0 2-7.464" }],
  ["path", { d: "M19.967 17.483A4 4 0 1 1 12 18a4 4 0 1 1-7.967-.517" }],
  ["path", { d: "M6 18a4 4 0 0 1-2-7.464" }],
  ["path", { d: "M6.003 5.125a4 4 0 0 0-2.526 5.77" }]
],
  refresh: [
  ["path", { d: "M3 12a9 9 0 0 1 9-9 9.75 9.75 0 0 1 6.74 2.74L21 8" }],
  ["path", { d: "M21 3v5h-5" }],
  ["path", { d: "M21 12a9 9 0 0 1-9 9 9.75 9.75 0 0 1-6.74-2.74L3 16" }],
  ["path", { d: "M8 16H3v5" }]
],
  settings: [
  ["path", { d: "M14 17H5" }],
  ["path", { d: "M19 7h-9" }],
  ["circle", { cx: "17", cy: "17", r: "3" }],
  ["circle", { cx: "7", cy: "7", r: "3" }]
],
  monitor: [
  ["rect", { width: "20", height: "14", x: "2", y: "3", rx: "2" }],
  ["line", { x1: "8", x2: "16", y1: "21", y2: "21" }],
  ["line", { x1: "12", x2: "12", y1: "17", y2: "21" }]
],
  history: [
  ["path", { d: "M3 12a9 9 0 1 0 9-9 9.75 9.75 0 0 0-6.74 2.74L3 8" }],
  ["path", { d: "M3 3v5h5" }],
  ["path", { d: "M12 7v5l4 2" }]
],
  pencil: [
  [
    "path",
    {
      d: "M21.174 6.812a1 1 0 0 0-3.986-3.987L3.842 16.174a2 2 0 0 0-.5.83l-1.321 4.352a.5.5 0 0 0 .623.622l4.353-1.32a2 2 0 0 0 .83-.497z"
    }
  ],
  ["path", { d: "m15 5 4 4" }]
],
  check: [["path", { d: "M20 6 9 17l-5-5" }]],
  inbox: [
  ["polyline", { points: "22 12 16 12 14 15 10 15 8 12 2 12" }],
  [
    "path",
    {
      d: "M5.45 5.11 2 12v6a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2v-6l-3.45-6.89A2 2 0 0 0 16.76 4H7.24a2 2 0 0 0-1.79 1.11z"
    }
  ]
]
};
function icon(name) {
  // Only this fixed local table is rendered as markup. Model and source text use textContent.
  const shapes = iconShapes[name];
  if (!shapes) return "";
  return '<svg class="understanding-icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true" focusable="false">'
    + shapes.map(([tag, attributes]) => '<' + tag + ' ' + Object.entries(attributes).map(([key, value]) => key + '="' + value + '"').join(' ') + '/>').join('') + '</svg>';
}

// Drafts live only in this WebView's module lifetime, including navigation away and back.
const drafts = new Map();
const operations = new Map();
const domains = { user: "关于你", relationship: "我们之间", self: "她自己", world: "外面的事" };
const kinds = { currentInterest: "在意的", currentStance: "看法", openQuestion: "还在想的",
  selfReflection: "相处方式", externalObservation: "观察", interpretation: "想法",
  userFact: "关于你", stableIdentity: "身份认知", permission: "协作边界",
  uncertainty: "尚不确定", interactionTendency: "互动倾向", selfObservation: "自我观察", interactionPattern: "互动习惯" };

const activityStates = new Map([
  ["disabled", "后台思考已关闭。"], ["waiting_for_change", "等待已授权上下文中出现有意义的变化。"],
  ["started", "正在准备后台思考。"], ["dispatched", "后台思考正在进行中。"],
  ["completed", "后台思考已就绪。"], ["no_change", "上次检查未发现需要补充的内容。"],
  ["cancelled", "因上下文发生变化，上次后台思考已停止。"], ["timed_out", "上次后台思考超时。"],
  ["storage_unavailable", "因保存的设置不可用，后台思考已暂停。"],
  ["COMPANION_ACTIVITY_BUDGET_UNAVAILABLE", "因请求额度不可用或已用尽，后台思考已暂停。"]
]);

export async function mount(container, context) {
  const style = document.createElement("link");
  style.rel = "stylesheet"; style.href = "pages/understanding.css"; document.head.append(style);
  container.innerHTML = `<section class="understanding-page" aria-label="Understanding">
    <button type="button" class="thought-attention" data-pending-attention hidden><span>✉</span><span><strong>她有 <span data-pending-total>0</span> 件事想问问你</strong><small>这些想法还没有用于聊天，想先听听你的意思</small></span><span>看看 ›</span></button>
    <header class="understanding-intro">
      <div class="understanding-context"><p class="understanding-eyebrow">${icon("brain")}<span data-scope>当前角色</span></p>
        <button type="button" class="understanding-refresh understanding-quiet" data-refresh>${icon("refresh")}刷新</button></div>
    </header>
    <p class="understanding-feedback" role="alert" data-error hidden></p>
    <p class="understanding-feedback" role="status" aria-live="polite" data-status>正在读取她的想法…</p>
    <section class="understanding-list" aria-label="她的想法">
      <div class="thought-toolbar"><nav class="thought-groups" aria-label="想法分组" data-groups></nav>
        <button type="button" class="understanding-quiet" data-open-search aria-label="搜索想法">⌕</button>
        <button type="button" class="understanding-quiet" data-open-filter aria-label="筛选标签" aria-expanded="false">▦</button>
        <button type="button" class="understanding-quiet" data-open-settings aria-label="设置">⚙</button></div>
      <div class="thought-tags" data-tags hidden></div>
      <div class="understanding-browser-tools" data-list-tools hidden><nav class="understanding-tabs" aria-label="Understanding views">
        <button type="button" data-view="accumulated" aria-label="已保存" aria-pressed="true">想法 <span data-count="accumulated" aria-hidden="true">0</span></button>
        <button type="button" data-view="pending" aria-label="待你确认" aria-pressed="false">待你确认 <span data-count="pending" aria-hidden="true">0</span></button>
      </nav>
      <details class="understanding-search-tools" data-search-tools><summary>搜索</summary><div class="understanding-search-row">
        <label class="understanding-search"><span aria-hidden="true"><svg viewBox="0 0 20 20" fill="none"><circle cx="8.5" cy="8.5" r="5.5"></circle><path d="m13 13 4 4"></path></svg></span><input type="search" data-search aria-label="Search understanding" placeholder="搜索想法" autocomplete="off" /></label>
        <select data-margin-select="native" data-domain aria-label="Filter by topic"><option value="all">全部</option><option value="user">关于你</option><option value="relationship">与你相处</option><option value="self">她自己</option><option value="world">外面的事</option></select>
      </div><button type="button" class="understanding-quiet understanding-archive" data-archive>查看已收起</button></details></div>
      <p class="understanding-caption understanding-view-note" data-view-note hidden></p>
      <div class="understanding-entries" data-entries></div>
      <div class="understanding-archived" data-archived hidden></div>
    </section>
    <div class="understanding-health" data-review-health aria-label="整理状态" hidden><span data-review-summary></span><details class="understanding-details" data-review-details hidden><summary>详情</summary><div><p data-readiness></p><p data-review-diagnostics></p></div></details></div>
    <div class="understanding-scrim" data-settings-scrim hidden></div>
    <aside class="understanding-settings" data-settings role="dialog" aria-label="她的想法 · 设置" hidden>
    <header class="understanding-drawer-heading"><h2>她的想法 · 设置</h2><button type="button" class="understanding-quiet" data-close-settings aria-label="关闭设置">×</button></header><div class="understanding-setup">
      <section class="understanding-control-card" data-automation-settings><p class="understanding-caption" data-automation-note></p><div data-automation-form></div><div class="understanding-actions" data-automation-actions hidden></div></section>
      <section class="understanding-control-card"><div class="understanding-control-heading"><h3>用于回复</h3><span class="understanding-state" data-use-state></span></div>
      <p data-permission-note></p>
      <div class="understanding-actions" data-policy-actions></div>
      <div class="understanding-confirm" data-policy-confirm hidden></div></section>
      <details class="understanding-settings-section" data-awareness-settings><summary>${icon("monitor")}后台思考与活跃度</summary><section class="understanding-awareness" aria-label="后台思考与活跃度" data-awareness></section></details>
    </div></aside></section>`;
  const root = container.querySelector(".understanding-page");
  const find = selector => root.querySelector(selector);
  find(".understanding-setup").prepend(find(".understanding-intro"));
  find(".understanding-list").append(find("[data-archive]"), find("[data-archived]"));
  const restored = context.state || {};
  let kind = typeof restored.kind === "string" ? restored.kind : "all";
  let archiveOpen = restored.archiveOpen === true || restored.view === "archived";
  let snapshot, view = ["accumulated", "pending"].includes(restored.view) ? restored.view : "accumulated",
    search = typeof restored.search === "string" ? restored.search : "", domain = restored.domain in domains || restored.domain === "all" ? restored.domain : "self",
    disposed = false, busy = false, awarenessDraft = restored.awarenessDraft, windowChoices;
  const opened = new Set(Array.isArray(restored.opened) ? restored.opened.filter(key => typeof key === "string") : []);
  const disabled = new WeakMap();
  function add(parent, tag, text, className) {
    const el = document.createElement(tag); if (text != null) el.textContent = text;
    if (className) el.className = className; parent.append(el); return el;
  }
  function button(parent, text, click, className = "", glyph) {
    const el = add(parent, "button", text, className); el.type = "button";
    if (glyph) el.insertAdjacentHTML("afterbegin", icon(glyph));
    el.addEventListener("click", () => { if (!busy && !disposed) click(); }); return el;
  }
  function disclosure(parent, title, key, className = "understanding-details", glyph) {
    const el = add(parent, "details", null, className); el.open = opened.has(key);
    const summary = add(el, "summary", title);
    if (glyph) summary.insertAdjacentHTML("afterbegin", icon(glyph));
    el.addEventListener("toggle", () => { if (el.isConnected) el.open ? opened.add(key) : opened.delete(key); });
    return el;
  }
  function shortDate(value) {
    const date = new Date(value);
    return Number.isNaN(date.valueOf()) ? "Date unavailable" : new Intl.DateTimeFormat("zh-CN", { month: "short", day: "numeric", year: "numeric" }).format(date);
  }
  function stateLabel(item) {
    return item.review === "rejected" ? "已拒绝" : item.life === "resolved" ? "已解决" : item.life === "retracted" ? "已撤回" : item.life !== "active" ? "已暂停或过期"
      : item.effect === "pending" ? "待你确认" : (snapshot.effectiveIds || []).includes(item.id) ? "可能用于回复" : "当前不用于回复";
  }
  function feedback(text, failed = false) {
    const el = find(failed ? "[data-error]" : "[data-status]"); el.textContent = text; el.hidden = !text;
  }
  function scopeKey() { return JSON.stringify([snapshot?.scope?.servantId, snapshot?.scope?.projectId]); }
  function draftKey(item) { return scopeKey() + ":" + item.id; }
  function entryElement(item) { return [...root.querySelectorAll("[data-entry-id]")].find(el => el.dataset.entryId === item.id); }
  async function send(type, payload = {}, notice = "", receive) {
    if (disposed || busy) return false;
    busy = true; root.setAttribute("aria-busy", "true"); feedback("", true);
    find("[data-refresh]").classList.toggle("is-refreshing", type === "understanding.get");
    for (const el of root.querySelectorAll("button,input,textarea,select")) { disabled.set(el, el.disabled); el.disabled = true; }
    try {
      const next = await context.request(type, payload);
      if (disposed) return false;
      if (receive) receive(next);
      else {
        if (!next?.state?.policy || typeof next.contextToken !== "string") throw new Error("invalid snapshot");
        snapshot = next; render();
      }
      feedback(notice); return true;
    } catch {
      if (!disposed) {
        feedback("", false);
        feedback("操作结果尚未确认，草稿已保留。请刷新核对当前作用域和版本，再决定是否重试。", true);
        if (!snapshot) {
          find("[data-review-health]").hidden = false;
          find("[data-review-summary]").textContent = "暂时无法读取想法，请刷新重试。";
        }
      }
      return false;
    } finally {
      busy = false; root.removeAttribute("aria-busy");
      find("[data-refresh]").classList.remove("is-refreshing");
      if (!disposed) for (const el of root.querySelectorAll("button,input,textarea,select"))
        if (disabled.has(el)) el.disabled = disabled.get(el);
    }
  }
  async function review(item, action, correction, binding) {
    const frozen = binding || { contextToken: snapshot.contextToken, revision: item.revision };
    const key = JSON.stringify([frozen.contextToken, item.id, frozen.revision, action, correction]);
    if (!operations.has(key)) operations.set(key, crypto.randomUUID());
    const ok = await send("understanding.review", { contextToken: frozen.contextToken, operationId: operations.get(key),
      id: item.id, revision: frozen.revision, action, ...(action === "correct" ? { correction } : {}) },
      action === "correct" && item.effect === "pending" ? "纠正已保存。此想法仍待明确批准。"
        : action === "correct" ? "纠正已保存。" : action === "delete" ? "已忘掉此想法及修订历史，原聊天记录保留。"
        : action === "approve" ? "已批准此版本。是否用于回复仍取决于当前授权与有效状态。"
        : action === "suspend" ? "已暂停使用这条想法，记录仍保留。"
        : action === "resolve" ? "已标记为已解决，记录仍保留。"
        : action === "restore" ? "此版本已恢复，是否用于回复仍取决于当前有效状态。" : "想法已更新。");
    if (ok && (action === "correct" || action === "delete")) { drafts.delete(draftKey(item)); render(); }
  }
  function details(card, item, footer) {
    const key = draftKey(item) + ":sources";
    const body = add(card, "section", null, "understanding-details understanding-item-details");
    body.id = "thought-detail-" + crypto.randomUUID(); body.hidden = !opened.has(key);
    body.setAttribute("aria-label", "来源与变化");
    const toggle = button(footer, "来源与变化", () => {
      body.hidden = !body.hidden;
      if (body.hidden) opened.delete(key); else opened.add(key);
      toggle.setAttribute("aria-expanded", String(!body.hidden));
    }, "understanding-quiet understanding-detail-toggle", "history");
    toggle.dataset.detailToggle = ""; toggle.setAttribute("aria-controls", body.id);
    toggle.setAttribute("aria-expanded", String(!body.hidden));
    function section(title, name) {
      const group = add(body, "section", null, "understanding-detail-section");
      group.dataset.detailSection = name; add(group, "h4", title); return group;
    }
    const origin = section("形成依据", "origin");
    add(origin, "p", ({ expressed: "曾在她的回复中表达。", enacted: "可能来自选择中的倾向。",
      secondOrderInferred: "在整理对话时推测。", externallySuggested: "来自外部建议，仍需复核。" })[item.origin]
      || "已保存的理解；下方列出与它关联的对话来源。");
    if (item.status === "hypothesis") add(origin, "p", "隐约觉得 · 即使批准也不会进入回复上下文。", "understanding-caption");
    else if (item.status === "tentative") add(origin, "p", "当前暂定的想法，之后也可能改变。", "understanding-caption");
    if (item.conditions) add(section("适用条件", "conditions"), "p", item.conditions);
    const metadata = add(section("状态与时间", "state"), "dl", null, "understanding-metadata");
    for (const [name, value] of [["类别", `${kinds[item.kind] || "想法"} · ${domains[item.domain] || "理解"}`],
      ["当前状态", stateLabel(item)], ["记录时间", shortDate(item.observedAt)], ["当前版本", `第 ${item.revision} 版`]]) {
      add(metadata, "dt", name); add(metadata, "dd", value);
    }
    if (item.activeUntil && !item.activeUntil.startsWith("9999")) {
      add(metadata, "dt", "有效期至"); add(metadata, "dd", shortDate(item.activeUntil));
    }
    if (item.correction) {
      add(section("原来的想法", "original"), "p", item.content);
      add(section("你提出的纠正", "correction"), "p", item.correction);
    }
    const sources = item.sources || [];
    const evidence = section(sources.length ? `依据 ${sources.length} 条对话引用` : "没有可用的来源引用", "sources");
    for (const source of sources) add(evidence, "p", `${({ user: "你的消息", assistant: "她的消息", system: "系统上下文" })[source.kind] || "对话消息"} · ${shortDate(source.occurredAtUtc)}`, "understanding-source-reference");
    if (!item.coverage?.length) add(evidence, "p", "来源读取覆盖范围尚未确认。", "understanding-caption");
    if (item.coverage?.some(part => !part.complete)) add(evidence, "p", "部分来源只读取了片段。", "understanding-caption");
    // Revisions include approvals/lifecycle changes, not only changes of wording.
    const changes = []; let previousText = item.correction || item.content;
    for (const prior of snapshot.changes?.[item.id] || []) {
      const text = prior.correction || prior.content;
      if (text && text !== previousText) { changes.push({ ...prior, text }); previousText = text; }
    }
    if (changes.length) {
      const history = disclosure(body, `以前的说法（${changes.length}）`, key + ":wording", "understanding-history");
      for (const prior of changes) {
        const version = add(history, "div", null, "understanding-previous-version");
        add(version, "span", `第 ${prior.revision} 版`, "understanding-caption");
        add(version, "p", prior.text).dataset.previousWording = "";
      }
    }
    return body;
  }
  function editor(card, item) {
    const key = draftKey(item), draft = drafts.get(key); if (!draft?.open) return;
    const overlay = add(card, "div", null, "understanding-correction-overlay");
    overlay.setAttribute("role", "dialog"); overlay.setAttribute("aria-modal", "true"); overlay.setAttribute("aria-label", "哪里不太对？");
    const panel = add(overlay, "div", null, "understanding-editor");
    add(panel, "h2", "哪里不太对？");
    add(panel, "p", item.correction || item.content, "understanding-correction-lead");
    const options = add(panel, "div", null, "understanding-correction-options");
    let choice = "correct";
    for (const [value, text, hint] of [["delete", "完全想错了", "忘掉这条及修订历史，原聊天保留"], ["correct", "差一点，我来改改", "记录纠正，不会自动批准"], ["resolve", "以前适用，现在不是了", "标记已解决"], ["suspend", "暂时不想谈这个", "逐条收起，保留内容"]]) {
      if (item.effect === "pending" && ["resolve", "suspend"].includes(value)) continue;
      const label = add(options, "label"); const radio = add(label, "input"); radio.type = "radio"; radio.name = "correction-" + item.id;
      radio.value = value; radio.checked = value === choice; add(label, "span", text); add(label, "small", hint);
      radio.addEventListener("change", () => {
        choice = value; edit.hidden = value !== "correct";
        save.textContent = value === "delete" ? "忘掉这条" : value === "correct" ? "保存纠正" : "好的";
        save.disabled = stale || value === "correct" && !edit.value.trim();
      });
    }
    const id = "correction-" + crypto.randomUUID(); const label = add(panel, "label", "你的纠正"); label.htmlFor = id;
    const edit = add(panel, "textarea"); edit.id = id; edit.maxLength = 2000; edit.value = draft.text;
    edit.addEventListener("input", () => { draft.text = edit.value; save.disabled = stale || !edit.value.trim(); });
    const stale = draft.contextToken !== snapshot.contextToken || draft.revision !== item.revision;
    add(panel, "p", stale ? "作用域或版本已变化。草稿会保留，但不能提交到这个版本。请复制草稿或明确选择当前版本。"
      : item.effect === "pending" ? "保存纠正不等于批准此想法。" : "只保存纠正，原想法与依据会保留在历史中。", "understanding-caption");
    const actions = add(panel, "div", null, "understanding-actions");
    const save = button(actions, "保存纠正", () => void review(item, choice, choice === "correct" ? edit.value : undefined, draft), "understanding-primary", "check");
    save.disabled = stale || !edit.value.trim();
    button(actions, "取消编辑", () => {
      draft.open = false; render(); entryElement(item)?.querySelector("[data-correct]")?.focus();
      feedback("草稿已保留，选择“纠正一下”继续编辑。");
    });
    if (stale) button(actions, "使用当前版本", () => {
      draft.contextToken = snapshot.contextToken; draft.revision = item.revision; render(); entryElement(item)?.querySelector("textarea")?.focus();
    });
  }
  function renderEntry(item, target = find("[data-entries]")) {
    const card = add(target, "article", null, "understanding-card"); card.dataset.entryId = item.id;
    const pending = item.review === "unreviewed" && item.effect === "pending" && item.life === "active";
    card.classList.toggle("is-pending", pending);
    const heading = add(card, "div", null, "thought-head");
    const tag = add(heading, "span", kinds[item.kind] || "想法", "thought-tag"); tag.dataset.kind = item.kind;
    add(heading, "span", shortDate(item.observedAt), "thought-age");
    add(card, "p", item.correction || item.content, "understanding-body");
    if (item.conditions) { const conditions = add(card, "p", null, "understanding-conditions"); add(conditions, "span", "When · "); add(conditions, "span", item.conditions); }
    const footer = add(card, "div", null, "understanding-entry-footer");
    const use = add(footer, "span", stateLabel(item), "understanding-state");
    use.classList.toggle("is-available", (snapshot.effectiveIds || []).includes(item.id));
    const actions = add(footer, "div", null, "understanding-actions");
    if (pending && item.proposedOperation === "retract") add(card, "p", "Proposed withdrawal · approval will stop using the earlier understanding; its history will be kept.", "understanding-caption");
    if (pending) button(actions, item.proposedOperation === "retract" ? "批准撤回" : "批准此版本", () => {
      if (!snapshot.state.policy.enabled || !snapshot.state.policy.readSources) { localReviewConsent(card); return; }
      void review(item, "approve");
    }, "understanding-primary", "check").setAttribute("aria-label", item.proposedOperation === "retract" ? "批准撤回" : "批准此版本");
    const detail = details(card, item, footer);
    const corrections = add(detail, "div", null, "understanding-actions");
    if (item.review !== "rejected") button(corrections, "纠正一下", () => {
      if (!snapshot.state.policy.enabled || !snapshot.state.policy.readSources) { localReviewConsent(card); return; }
      const key = draftKey(item), prior = drafts.get(key);
      if (prior) prior.open = true;
      else drafts.set(key, { text: item.correction || item.content || "", contextToken: snapshot.contextToken, revision: item.revision, open: true });
      render(); entryElement(item)?.querySelector("textarea")?.focus();
    }, "understanding-quiet", "pencil").dataset.correct = "";
    if (pending) button(actions, "不保留这条", () => void review(item, "reject"), "understanding-quiet");
    else if (item.review === "approved" && item.life !== "active" && item.life !== "retracted")
      button(actions, "恢复此版本", () => void review(item, "restore")).dataset.restoreThought = "";
    const management = add(detail, "section", null, "understanding-detail-section");
    add(management, "h4", "管理这条想法");
    const moreActions = add(management, "div", null, "understanding-lifecycle-actions");
    function action(label, hint, click, style = "") {
      const row = add(moreActions, "div", null, "understanding-lifecycle-action");
      const control = button(row, label, click, style);
      const description = add(row, "p", hint, "understanding-caption");
      description.id = "thought-action-" + crypto.randomUUID(); control.setAttribute("aria-describedby", description.id);
      return control;
    }
    if (!pending && item.review !== "rejected" && item.life === "active") {
      action("暂停使用这条", "保留内容，移入已收起列表；恢复前不用于回复。", () => void review(item, "suspend")).dataset.suspendThought = "";
      action("标记为已解决", "这件事已告一段落；保留记录，不再作为当前想法使用。", () => void review(item, "resolve")).dataset.resolveThought = "";
    }
    const forget = action("删除这条想法", "删除本条及版本记录，原聊天保留。需要再次确认。", () => {
      if (card.querySelector("[data-forget]")) return;
      const confirm = add(card, "div", null, "understanding-confirm"); confirm.dataset.forget = "";
      add(confirm, "p", "删除这条想法及全部版本记录？原聊天记录会保留。");
      const row = add(confirm, "div", null, "understanding-actions");
      const binding = { contextToken: snapshot.contextToken, revision: item.revision };
      button(row, "确认删除", () => void review(item, "delete", undefined, binding), "understanding-danger");
      button(row, "取消", () => { confirm.remove(); forget.focus(); }).focus();
    }, "understanding-danger"); forget.dataset.deleteThought = "";
    editor(card, item);
  }
  function localReviewConsent(card) {
    if (card.querySelector("[data-local-review]")) return;
    const confirm = add(card, "div", null, "understanding-confirm"); confirm.dataset.localReview = "";
    const policy = snapshot.state.policy, display = snapshot.presentation || {};
    add(confirm, "p", `允许在本机复核 ${display.roleName || "此角色"} · ${display.projectName || "当前项目"} 的原聊天依据？批准只适用于此版本，纠正不会自动批准。`);
    add(confirm, "p", policy.useInDialogue
      ? `此前用于回复的授权也将恢复。符合条件的想法可能发送给 ${display.modelRecipient || "当前对话模型"}，待确认项除外。`
      : "此选择不向模型发送消息，也不授予用于回复的权限。");
    const frozen = { contextToken: snapshot.contextToken, revision: snapshot.state.revision, policy: { ...policy, enabled: true, readSources: true } };
    const row = add(confirm, "div", null, "understanding-actions");
    button(row, "允许本地复核", () => void send("understanding.configure", frozen, "已允许本地复核。请再次选择纠正或批准，核对此版本。"), "understanding-primary");
    button(row, "取消", () => { confirm.remove(); card.querySelector("[data-correct]")?.focus(); }).focus();
  }
  function policyAction(parent, text, purpose, change) {
    button(parent, text, () => {
      const confirm = find("[data-policy-confirm]"); confirm.replaceChildren(); confirm.hidden = false;
      add(confirm, "p", purpose);
      add(confirm, "p", `角色：${snapshot.presentation?.roleName || "当前角色"}。作用域：${snapshot.presentation?.projectName || "当前项目"}。模型接收方：${snapshot.presentation?.modelRecipient || "不可用"}。`);
      const frozen = { contextToken: snapshot.contextToken, revision: snapshot.state.revision, policy: change({ ...snapshot.state.policy }) };
      if (frozen.policy.enabled) {
        const uses = [["读取原聊天记录", frozen.policy.readSources], ["交给模型整理", frozen.policy.externalProcessing],
          ["保存新想法", frozen.policy.saveCandidates], ["在回复中使用符合条件的想法", frozen.policy.useInDialogue]];
        add(confirm, "p", `本次更改后允许：${uses.filter(([, allowed]) => allowed).map(([label]) => label).join("、") || "无"}。`);
      }
      const row = add(confirm, "div", null, "understanding-actions");
      button(row, "确认", () => void send("understanding.configure", frozen, "授权已更新。"), "understanding-primary");
      button(row, "取消", () => { confirm.hidden = true; }).focus();
    });
  }
  function automaticSetup() {
    const form = find("[data-automation-form]"); form.replaceChildren();
    const policy = snapshot.state.policy, service = snapshot.automation;
    const binding = { contextToken: snapshot.contextToken, revision: snapshot.state.revision, routeHash: service?.routeHash };
    add(form, "h3", "她怎么整理想法");
    const presets = add(form, "div", null, "understanding-presets");
    let selected = policy.enabled && policy.mode !== "off" ? policy.mode : "paused";
    const radios = [];
    for (const [value, title, description] of [
      ["paused", "暂停", "不再整理。已有的想法会留着，但不可能用于后续回复。"],
      ["proposals", "由我确认", "有新想法时先问你；批准版本后仍需满足其他条件，才可能用于回复。"],
      ["bounded", "自然成长", "仅符合既有有限自动生效规则的内容可自动保存，其余仍需批准。"]]) {
      const label = add(presets, "label"); const radio = add(label, "input"); radio.type = "radio";
      radio.name = "understanding-preset"; radio.value = value; radio.checked = selected === value;
      radio.disabled = !snapshot.presentation.automaticReviewAvailable && value !== "paused";
      const text = add(label, "span"); add(text, "strong", title); add(text, "small", description);
      radio.addEventListener("change", () => { selected = value; validate(); }); radios.push(radio);
    }
    add(form, "h3", "每日后台模型调用上限");
    const frequencies = add(form, "div", null, "understanding-frequency");
    const budget = service?.budget;
    const quota = add(form, "p", budget ? `今日模型调用 ${budget.used} / ${budget.limit}` : "尚无已批准的模型调用额度。", "understanding-caption");
    add(form, "h3", "帮她整理的模型");
    add(form, "p", snapshot.presentation.modelRecipient || "尚未配置模型", "understanding-caption");
    if (context.canNavigate?.("ModelConnection")) button(form, "去「模型与连接」", () => void context.navigate("ModelConnection"), "understanding-quiet");
    const advanced = add(form, "details", null, "understanding-advanced"); add(advanced, "summary", "高级选项");
    const choices = {};
    for (const [key, text] of [["readSources", "读取你们的聊天"], ["externalProcessing", "交给模型帮忙整理"], ["saveCandidates", "保存新想法"], ["useInDialogue", "在聊天里用上"]]) {
      const label = add(advanced, "label", null, "understanding-choice"); const box = add(label, "input"); box.type = "checkbox";
      box.checked = Boolean(policy[key]); add(label, "span", text); choices[key] = box;
    }
    const limitLabel = add(advanced, "label", "自定义每日上限 ");
    const limit = add(limitLabel, "input"); limit.type = "number"; limit.min = "1"; limit.max = "10000"; limit.step = "1";
    limit.setAttribute("aria-label", "每日后台模型调用上限"); limit.value = String(budget?.limit || 12);
    const freqButtons = [];
    for (const [value, label] of [[6, "偶尔"], [12, "适中"], [30, "经常"]]) {
      const pick = button(frequencies, label, () => { limit.value = String(value); validate(); });
      pick.dataset.limit = String(value); freqButtons.push(pick);
    }
    const preview = button(form, "现在让她想一想（只预览，不保存）", () => {}, "understanding-secondary");
    preview.disabled = true; preview.title = "当前设置桥接未提供手动预览命令。";
    const consent = add(form, "p", "", "understanding-caption");
    add(form, "p", `作用于 ${snapshot.presentation.roleName} · ${snapshot.presentation.projectName}。首次开启只整理开启后的新内容；同角色和项目的新对话可共享有效理解。读取、交给模型、保存与用于回复仍需分别选择。`, "understanding-caption");
    add(form, "p", "理解、独立分类和重试分别计数，失败或取消不退还额度。上限由所有角色和项目共用，已用次数不会清零；代理内部重试可能增加上游调用与费用。", "understanding-caption");
    find(".understanding-drawer-footer")?.remove();
    const row = add(find("[data-settings]"), "div", null, "understanding-actions understanding-drawer-footer");
    button(row, "取消", () => { automaticSetup(); toggleSettings(false); });
    const start = button(row, "好的", async () => {
      if (selected === "paused") {
        if (await send("understanding.configure", { contextToken: binding.contextToken, revision: binding.revision,
          policy: { ...policy, enabled: false } }, "已暂停。想法与已有授权选择保留。")) toggleSettings(false);
        return;
      }
      if (!limit.checkValidity()) return;
      const configured = { mode: selected, enabled: true,
        ...Object.fromEntries(Object.entries(choices).map(([key, box]) => [key, box.checked])) };
      // Dispatch grants keep the existing exact recipient and budget binding.
      if (await send("understanding.automation", { ...binding, dailyLimit: Number(limit.value), policy: configured }, "设置已保存。后台仍须满足当前授权与校验条件。")) toggleSettings(false);
    }, "understanding-primary");
    function validate() {
      for (const pick of freqButtons) pick.setAttribute("aria-pressed", String(pick.dataset.limit === limit.value));
      const missing = !choices.readSources.checked || !choices.externalProcessing.checked || !choices.saveCandidates.checked;
      consent.textContent = selected === "paused" ? "暂停会保留已有内容和授权选择；撤回授权请使用下方独立操作。"
        : missing ? "请展开高级选项，逐项确认读取、交给上述模型整理与保存新想法的权限；选择模式本身不会授权。"
        : `每日最多派发 ${limit.value} 次后台模型调用，不代表整理成功次数或费用。`;
      start.disabled = selected !== "paused" && (!binding.routeHash || missing || !limit.checkValidity());
    }
    for (const box of Object.values(choices)) box.addEventListener("change", validate);
    limit.addEventListener("input", validate); validate();
  }
  function renderAwareness() {
    const card = find("[data-awareness]"), status = snapshot.awareness;
    card.replaceChildren();
    if (!status) {
      awarenessDraft = null; windowChoices = null;
      const empty = add(card, "div", null, "understanding-empty");
      add(empty, "h4", "Awareness is not available here");
      add(empty, "p", "This companion or build has no connected awareness service.");
      return;
    }
    if (!awarenessDraft || awarenessDraft.contextToken !== status.contextToken || awarenessDraft.revision !== status.revision) {
      awarenessDraft = { contextToken: status.contextToken, revision: status.revision, options: {
        activityContextEnabled: status.options?.activityContextEnabled !== false,
        applicationsEnabled: status.options?.applicationsEnabled === true,
        backgroundEnabled: status.options?.backgroundEnabled === true,
        dailyRequestLimit: status.options?.dailyRequestLimit
      } };
      windowChoices = null;
    }
    const draft = awarenessDraft, available = status.available === true;
    const savedLimit = status.options?.dailyRequestLimit;
    const validLimit = Number.isInteger(savedLimit) && savedLimit >= 1 && savedLimit <= 24;
    // The request allowance is owned by the host, not this simplified form.
    draft.options.dailyRequestLimit = savedLimit;
    const recipient = add(card, "div", null, "understanding-recipient");
    add(recipient, "p", `接收模型：${status.modelRecipient || "未配置模型"}。`, "understanding-awareness-recipient");
    if (!available) add(card, "p", "当前上下文无法使用环境感知。请刷新以检查当前角色和范围。", "understanding-caption");
    function choice(parent, name, title, description) {
      const label = add(parent, "label", null, "understanding-awareness-option");
      const text = add(label, "span"); add(text, "strong", title); add(text, "span", description, "understanding-caption");
      const input = add(label, "input"); input.type = "checkbox"; input.checked = draft.options[name]; input.disabled = !available;
      input.dataset.awarenessOption = name;
      const toggle = add(label, "span", null, "understanding-switch"); toggle.setAttribute("aria-hidden", "true");
      input.addEventListener("change", () => { draft.options[name] = input.checked; });
    }
    const sources = add(card, "section", null, "understanding-settings-group"); add(sources, "h4", "信息来源");
    choice(sources, "activityContextEnabled", "使用活动上下文", "专注、提醒以及跨角色和项目共享的本地任务记录。");
    choice(sources, "applicationsEnabled", "使用应用概览", "应用名称、前台应用以及窗口数量。窗口标题和具体内容需要单独授权。");
    const windows = add(sources, "section", null, "understanding-awareness-windows"); add(windows, "h4", "窗口访问");
    for (const allowed of status.windows || []) {
      const row = add(windows, "div", null, "understanding-awareness-window");
      add(row, "p", allowed.title || "Untitled window"); add(row, "p", allowed.application || "Unknown application", "understanding-caption");
    }
    if (!status.windows?.length) add(windows, "p", "未允许访问任何窗口。可选择一个窗口以分享其标题与可见文本。", "understanding-caption");
    const windowActions = add(windows, "div", null, "understanding-actions");
    const choose = button(windowActions, "允许一个窗口", () => {
      const binding = { contextToken: status.contextToken, revision: status.revision };
      windowChoices = null; renderAwareness();
      void send("understanding.awareness.windows", { contextToken: binding.contextToken }, "Choose one window to allow background reading.", next => {
        if (next?.contextToken !== binding.contextToken || !Array.isArray(next.windows) ||
            snapshot.awareness?.contextToken !== binding.contextToken || snapshot.awareness?.revision !== binding.revision) throw new Error("stale window choices");
        windowChoices = { ...binding, windows: next.windows }; renderAwareness();
      });
    });
    choose.disabled = !available || !status.modelApprovalCurrent;
    if (available && !status.modelApprovalCurrent) add(windows, "p", "在允许窗口访问前，请先保存当前模型的选项。", "understanding-caption");
    if (status.windows?.length) {
      const clear = button(windowActions, "清除窗口访问", () => void send("understanding.awareness.clearWindows", {
        contextToken: status.contextToken, revision: status.revision
      }, "Window access cleared. Background window reading stopped."));
      clear.disabled = !available;
    }
    if (windowChoices) {
      const choices = add(windows, "div", null, "understanding-awareness-choices");
      add(choices, "p", "允许窗口后，角色可在后台重复读取其标题和可见文本，并与上述模型配合使用。窗口标题变更、窗口关闭、范围或模型变化或清除访问时，访问权限即终止。新的选择会覆盖先前的选择。");
      if (!windowChoices.windows.length) add(choices, "p", "没有符合条件的可用窗口。");
      const binding = windowChoices;
      for (const candidate of binding.windows) {
        const row = add(choices, "div", null, "understanding-awareness-window");
        add(row, "p", candidate.title || "Untitled window"); add(row, "p", candidate.application || "Unknown application", "understanding-caption");
        const allow = button(row, "允许此窗口", () => void send("understanding.awareness.allowWindow", {
          contextToken: binding.contextToken, revision: binding.revision, candidateToken: candidate.token
        }, "Window allowed for background reading in the current scope."));
        allow.disabled = !available || !status.modelApprovalCurrent;
      }
      button(choices, "关闭窗口选择", () => { windowChoices = null; renderAwareness(); });
    }
    const background = add(card, "section", null, "understanding-settings-group");
    choice(background, "backgroundEnabled", "后台思考", "在对话间隔中使用已授权的上下文发起额外模型请求。");
    const budget = add(background, "p", validLimit ? `每 24 小时最多可发起 ${savedLimit} 次额外模型请求。已保留已保存的配额。`
      : "保存的请求额度不可用。请刷新后再保存。", "understanding-caption understanding-background-status");
    budget.dataset.awarenessBudget = "";
    add(background, "p", !status.options?.backgroundEnabled ? "后台思考已关闭。" : !status.modelApprovalCurrent
      ? "后台思考已暂停，等待你批准当前模型。"
      : activityStates.get(status.activityStatus) || "后台思考暂不可用。刷新以检查其状态。", "understanding-caption");
    const saveBar = add(card, "div", null, "understanding-save-bar");
    const copy = add(saveBar, "div");
    const dirty = add(copy, "p", "", "understanding-draft-state"); dirty.setAttribute("role", "status");
    add(copy, "p", "保存后将对当前角色、范围和模型允许这些来源。更换模型需要重新批准。", "understanding-caption");
    const save = button(saveBar, "保存", () => void send("understanding.awareness.configure", {
      contextToken: draft.contextToken, revision: draft.revision, options: { ...draft.options, dailyRequestLimit: savedLimit }
    }, "Ambient awareness choices saved for the current model."), "understanding-primary", "check");
    save.setAttribute("aria-label", "Save & approve awareness");
    function validateChoices() {
      save.disabled = !available || !validLimit;
      const changed = Object.keys(draft.options).some(key => draft.options[key] !== status.options?.[key]);
      dirty.textContent = !validLimit ? "请刷新以核对已保存的配额。" : changed ? "有尚未保存的选择。" : !status.modelApprovalCurrent ? "需要对此模型进行批准。" : "选择已保存。";
    }
    for (const input of card.querySelectorAll("[data-awareness-option]")) input.addEventListener("change", validateChoices);
    validateChoices();
  }
  function entryGroups() {
    const all = [...(snapshot.accumulated || []), ...(snapshot.pending || [])];
    const archived = item => item.review === "rejected" || item.life !== "active";
    const pending = item => !archived(item) && item.effect === "pending" && item.review === "unreviewed";
    return { accumulated: all.filter(item => !archived(item) && !pending(item)), pending: all.filter(pending), archived: all.filter(archived) };
  }
  function setView(next) {
    if (busy || disposed || !snapshot || !["accumulated", "pending"].includes(next)) return;
    view = next; feedback(""); renderEntries();
  }
  function renderEntries() {
    const groups = entryGroups(), query = search.trim().toLocaleLowerCase();
    const tags = find("[data-tags]"); tags.replaceChildren();
    for (const key of ["all", ...new Set(groups[view].filter(item => domain === "all" || item.domain === domain).map(item => item.kind))]) {
      const chip = button(tags, key === "all" ? "全部" : kinds[key] || "想法", () => { kind = key; renderEntries(); });
      chip.setAttribute("aria-pressed", String(key === kind));
    }
    const groupNav = find("[data-groups]"); groupNav.replaceChildren();
    for (const [key, label] of [["self", "她自己"], ["user", "关于你"], ["relationship", "我们之间"],
      ...(groups[view].some(item => item.domain === "world") ? [["world", "外面的事"]] : [])]) {
      const tab = button(groupNav, label, () => { domain = key; kind = "all"; find("[data-domain]").value = key; renderEntries(); });
      tab.setAttribute("aria-pressed", String(domain === key));
    }
    find("[data-pending-attention]").hidden = view === "pending" || !groups.pending.length;
    find("[data-pending-total]").textContent = String(groups.pending.length);
    const items = groups[view].filter(item => (kind === "all" || item.kind === kind) && (domain === "all" || item.domain === domain) && (!query ||
      [item.content, item.correction, item.conditions, kinds[item.kind], domains[item.domain]].filter(Boolean).join(" ").toLocaleLowerCase().includes(query)));
    for (const tab of root.querySelectorAll("[data-view]")) {
      tab.setAttribute("aria-pressed", String(tab.dataset.view === view));
      tab.querySelector("[data-count]").textContent = String(groups[tab.dataset.view].length);
      tab.setAttribute("aria-description", `${groups[tab.dataset.view].length} items`);
      tab.hidden = tab.dataset.view !== "accumulated" || view === "accumulated";
      if (tab.dataset.view === "accumulated") tab.childNodes[0].textContent = "‹ 返回她的想法 ";
    }
    find("[data-list-tools]").hidden = view === "accumulated" && !find("[data-search-tools]").open;
    find("[data-search-tools] > summary").textContent = "搜索";
    find("[data-view-note]").hidden = view === "accumulated" || !groups[view].length;
    find("[data-view-note]").textContent = view === "pending" ? "待你批准此版本。批准不等于证实，也不保证用于回复。"
      : "已收起、解决、过期或拒绝的想法。";
    find("[data-archive]").textContent = `已收起的想法（${groups.archived.length}）`;
    find("[data-archive]").setAttribute("aria-expanded", String(archiveOpen));
    find("[data-archive]").setAttribute("aria-controls", "understanding-archived-items");
    const archivedItems = find("[data-archived]"); archivedItems.id = "understanding-archived-items";
    archivedItems.hidden = !archiveOpen; archivedItems.replaceChildren();
    if (archiveOpen) {
      if (!groups.archived.length) add(archivedItems, "p", "还没有已收起的想法。", "understanding-caption");
      for (const item of groups.archived) renderEntry(item, archivedItems);
    }
    find("[data-entries]").replaceChildren();
    if (!items.length) {
      const empty = add(find("[data-entries]"), "div", null, "understanding-empty");
      empty.insertAdjacentHTML("afterbegin", icon("inbox"));
      if (query || kind !== "all") {
        add(empty, "h3", "没有匹配的想法");
        add(empty, "p", "试试其他搜索内容，或清除筛选。");
        button(empty, "清除筛选", () => {
          search = ""; domain = "all"; find("[data-search]").value = ""; find("[data-domain]").value = "all";
          renderEntries(); find("[data-search-tools]").open = true; find("[data-search]").focus();
        });
      } else {
        add(empty, "h3", view === "pending" ? "暂时没有待确认的事" : view === "archived" ? "还没有已收起的想法" : "她还没开始整理这组想法");
        add(empty, "p", view === "pending" ? "需要你批准的想法会出现在这里。"
          : view === "archived" ? "已收起、解决或拒绝的想法会保留在这里。"
          : groups.pending.length ? "还有想法正在等你确认。"
          : "此角色在这一组中还没有保存的想法。");
      }
    }
    for (const item of items) renderEntry(item);
  }
  function render() {
    const policy = snapshot.state.policy, display = snapshot.presentation || {};
    find("[data-scope]").textContent = `${display.roleName || "当前角色"} · ${display.projectName || "当前项目"}`;
    renderAwareness(); renderEntries();
    const all = [...(snapshot.accumulated || []), ...(snapshot.pending || [])];
    const canUse = policy.enabled && policy.readSources && policy.useInDialogue;
    const useState = find("[data-use-state]"); useState.textContent = !policy.enabled ? "已暂停" : canUse ? "已允许" : "未开启";
    useState.classList.toggle("is-available", canUse);
    const automation = snapshot.automation, budget = automation?.budget, run = snapshot.lastRun;
    const automatic = policy.enabled && policy.mode !== "off" && policy.readSources && policy.externalProcessing && policy.saveCandidates;
    const reasons = {
      UNDERSTANDING_MODEL_UNAVAILABLE: "请先选择用于整理想法的模型。",
      UNDERSTANDING_BUDGET_UNAUTHORIZED: "请在设置中批准当前模型的后台调用额度。",
      UNDERSTANDING_BUDGET_LIMITED: "今日后台模型调用额度已用完。",
      UNDERSTANDING_CLOCK_ROLLBACK: "系统时钟回退，自动整理已暂停。",
      UNDERSTANDING_DAILY_STORE_UNAVAILABLE: "调用额度记录不可用，自动整理已暂停。"
    };
    const issue = automatic && automation?.code;
    const failed = run?.stage === "failed";
    const summary = !display.automaticReviewAvailable ? "此版本尚未接通自动整理。"
      : issue ? reasons[issue] || "自动整理暂时不可用。"
      : failed ? "上次整理失败，符合条件的任务将等待后再重试。"
      : automatic && run?.stage === "running" ? "正在整理新对话…" : "";
    find("[data-review-health]").hidden = !summary;
    find("[data-review-summary]").textContent = summary;
    find("[data-review-details]").hidden = !issue && !failed;
    find("[data-readiness]").textContent = !display.automaticReviewAvailable
      ? "此版本尚未接通自动整理，仍可复核已有想法。"
      : !automatic ? "自动整理未开启或已暂停。"
      : run?.stage === "waiting_foreground" ? "等待当前对话结束。"
      : snapshot.pendingSources > 0 ? "新对话正在等待下一次整理。"
      : "等待新对话。";
    const diagnostics = [];
    if (issue) diagnostics.push(`Service: ${issue}.`);
    if (run) diagnostics.push(`Last review: ${new Date(run.at).toLocaleString()} · ${run.newItems || 0} new impressions.${failed ? ` Code: ${run.code || "不可用"}.` : ""}`);
    find("[data-review-diagnostics]").textContent = diagnostics.join(" ");
    find("[data-automation-settings]").hidden = false;
    find("[data-automation-note]").textContent = `${automatic ? "自动整理新对话。" : "自动整理未开启或已暂停。"} ${budget ? `今日后台模型调用 ${budget.used} / ${budget.limit}；${new Date(budget.resetsAt).toLocaleString()} 重置。` : "尚无可用的调用额度。"} 刷新只读取状态。`;
    const automaticActions = find("[data-automation-actions]"); automaticActions.replaceChildren();
    automaticSetup();
    find("[data-permission-note]").textContent = !policy.enabled ? "理解已暂停，已保存的想法不会用于回复。"
      : !policy.readSources || !policy.useInDialogue ? "当前不会把已保存的想法用于回复。"
      : "满足条件的想法可能用于此角色的回复，待确认项除外。";
    const actions = find("[data-policy-actions]"); actions.replaceChildren();
    const hasAccess = policy.readSources || policy.externalProcessing || policy.saveCandidates || policy.useInDialogue || policy.mode !== "off";
    if (hasAccess && !policy.enabled) policyAction(actions, "恢复理解", "按原有授权选择恢复理解。如果之前允许用于回复，符合条件的想法可能再次用于回复。", p => ({ ...p, enabled: true }));
    else if (hasAccess && policy.enabled) {
      if (all.length && policy.readSources && !policy.useInDialogue) policyAction(actions, "用于回复", "允许将符合条件的已保存想法发送给当前对话模型并用于此角色的回复，待确认项除外。", p => ({ ...p, useInDialogue: true }));
      if (policy.useInDialogue) policyAction(actions, "停止用于回复", "停止把已保存的理解用于后续回复，保留其他授权和已保存内容。", p => ({ ...p, useInDialogue: false }));
      policyAction(actions, "暂停理解", "暂停本地复核和回复使用，保留已保存的想法与原有授权选择。", p => ({ ...p, enabled: false }));
    }
    if (hasAccess)
      policyAction(actions, "关闭并撤回授权", "关闭此作用域的理解，并撤回读取来源、模型处理、保存候选和用于回复的权限。已保存想法与原聊天记录保留。", () => ({ mode: "off", enabled: false, readSources: false, externalProcessing: false, saveCandidates: false, useInDialogue: false }));
    find("[data-policy-confirm]").hidden = true;
  }
  for (const tab of root.querySelectorAll("[data-view]")) tab.addEventListener("click", () => setView(tab.dataset.view));
  find("[data-search]").addEventListener("input", event => { search = event.target.value; if (snapshot) renderEntries(); });
  find("[data-domain]").addEventListener("change", event => { domain = event.target.value; if (snapshot) renderEntries(); });
  find("[data-archive]").addEventListener("click", () => {
    if (busy || disposed || !snapshot) return;
    archiveOpen = !archiveOpen; renderEntries();
    find("[data-archive]").focus({ preventScroll: true });
  });
  function toggleSettings(open) {
    const drawer = find("[data-settings]"); drawer.hidden = !open;
    drawer.setAttribute("role", "dialog"); drawer.setAttribute("aria-label", "她的想法 · 设置"); drawer.setAttribute("aria-modal", String(open));
    find("[data-settings-scrim]").hidden = !open;
    if (open) find("[data-close-settings]").focus(); else find("[data-open-settings]").focus();
  }
  find("[data-open-settings]").addEventListener("click", () => toggleSettings(true));
  find("[data-close-settings]").addEventListener("click", () => toggleSettings(false));
  find("[data-settings-scrim]").addEventListener("click", () => toggleSettings(false));
  find("[data-open-search]").addEventListener("click", () => { find("[data-search-tools]").open = !find("[data-search-tools]").open; find("[data-list-tools]").hidden = view === "accumulated" && !find("[data-search-tools]").open; if (find("[data-search-tools]").open) find("[data-search]").focus(); });
  find("[data-open-filter]").addEventListener("click", event => { const tags = find("[data-tags]"); tags.hidden = !tags.hidden; event.currentTarget.setAttribute("aria-expanded", String(!tags.hidden)); });
  find("[data-pending-attention]").addEventListener("click", () => { domain = "all"; kind = "all"; find("[data-domain]").value = "all"; setView("pending"); });
  const escape = event => {
    const correction = find(".understanding-correction-overlay");
    if (correction) {
      if (event.key === "Escape") {
        event.preventDefault(); for (const draft of drafts.values()) draft.open = false; render(); return;
      }
      if (event.key === "Tab") {
        const controls = [...correction.querySelectorAll("button:not(:disabled),input:not(:disabled),textarea:not(:disabled)")].filter(el => el.getBoundingClientRect().height > 0);
        if (event.shiftKey && document.activeElement === controls[0]) { event.preventDefault(); controls.at(-1)?.focus(); }
        else if (!event.shiftKey && document.activeElement === controls.at(-1)) { event.preventDefault(); controls[0]?.focus(); }
      }
      return;
    }
    const drawer = find("[data-settings]"); if (drawer.hidden) return;
    if (event.key === "Escape") { event.preventDefault(); toggleSettings(false); }
    if (event.key === "Tab") {
      const controls = [...drawer.querySelectorAll("button:not(:disabled),input:not(:disabled),select:not(:disabled),summary,textarea:not(:disabled)")].filter(el => el.getBoundingClientRect().height > 0);
      if (!controls.length) return;
      if (event.shiftKey && document.activeElement === controls[0]) { event.preventDefault(); controls.at(-1).focus(); }
      else if (!event.shiftKey && document.activeElement === controls.at(-1)) { event.preventDefault(); controls[0].focus(); }
    }
  };
  root.addEventListener("keydown", escape);
  find("[data-refresh]").addEventListener("click", () => void send("understanding.get"));
  find("[data-search]").value = search; find("[data-domain]").value = domain;
  find("[data-search-tools]").open = restored.searchOpen === true || Boolean(search);
  find("[data-awareness-settings]").open = restored.awarenessOpen === true;
  const dispose = () => { disposed = true; for (const el of root.querySelectorAll("button,input,textarea,select")) el.disabled = true; style.remove(); };
  dispose.getState = () => ({ view, search, domain, kind, archiveOpen, settingsOpen: !find("[data-settings]").hidden,
    searchOpen: find("[data-search-tools]").open, awarenessOpen: find("[data-awareness-settings]").open, opened: [...opened], awarenessDraft });
  context.signal.addEventListener("abort", dispose, { once: true });
  await send("understanding.get");
  if (!disposed && restored.settingsOpen === true) toggleSettings(true);
  return dispose;
}
