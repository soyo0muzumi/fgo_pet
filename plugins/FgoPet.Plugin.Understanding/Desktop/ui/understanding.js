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
const domains = { user: "About you", relationship: "Your relationship", self: "Your companion", world: "The world" };
const kinds = { currentInterest: "Interest", currentStance: "Perspective", openQuestion: "Open question",
  selfReflection: "Reflection", externalObservation: "Observation", interpretation: "Impression",
  userFact: "About you", stableIdentity: "Identity", permission: "Permission",
  uncertainty: "Uncertainty", interactionTendency: "Interaction tendency", selfObservation: "Self observation", interactionPattern: "Interaction pattern" };

const activityStates = new Map([
  ["disabled", "Background thought is off."], ["waiting_for_change", "Waiting for a meaningful change in approved context."],
  ["started", "Preparing a background thought."], ["dispatched", "A background thought is in progress."],
  ["completed", "The latest background thought is ready to inform conversation."], ["no_change", "The last check found nothing useful to add."],
  ["cancelled", "The last background thought stopped because its context changed."], ["timed_out", "The last background thought timed out."],
  ["storage_unavailable", "Background thought is paused because its saved settings are unavailable."],
  ["COMPANION_ACTIVITY_BUDGET_UNAVAILABLE", "Background thought is paused because its request allowance is unavailable or used up."]
]);

export async function mount(container, context) {
  const style = document.createElement("link");
  style.rel = "stylesheet"; style.href = "pages/understanding.css"; document.head.append(style);
  container.innerHTML = `<section class="understanding-page" aria-label="Understanding">
    <header class="understanding-intro">
      <div class="understanding-context"><p class="understanding-eyebrow">${icon("brain")}<span data-scope>Current companion</span></p>
        <button type="button" class="understanding-refresh understanding-quiet" data-refresh>${icon("refresh")}Refresh</button></div>
    </header>
    <p class="understanding-feedback" role="alert" data-error hidden></p>
    <p class="understanding-feedback" role="status" aria-live="polite" data-status>Loading understanding…</p>
    <section class="understanding-list" aria-label="Saved impressions">
      <div class="understanding-browser-tools" data-list-tools hidden><nav class="understanding-tabs" aria-label="Understanding views">
        <button type="button" data-view="accumulated" aria-label="Saved" aria-pressed="true">Saved <span data-count="accumulated" aria-hidden="true">0</span></button>
        <button type="button" data-view="pending" aria-label="To review" aria-pressed="false">To review <span data-count="pending" aria-hidden="true">0</span></button>
        <button type="button" data-view="archived" aria-label="Archived" aria-pressed="false" hidden>Archived <span data-count="archived" aria-hidden="true">0</span></button>
      </nav>
      <details class="understanding-search-tools" data-search-tools><summary>Search</summary><div class="understanding-search-row">
        <label class="understanding-search"><span aria-hidden="true"><svg viewBox="0 0 20 20" fill="none"><circle cx="8.5" cy="8.5" r="5.5"></circle><path d="m13 13 4 4"></path></svg></span><input type="search" data-search aria-label="Search understanding" placeholder="Search insights…" autocomplete="off" /></label>
        <select data-domain aria-label="Filter by topic"><option value="all">All topics</option><option value="user">About you</option><option value="relationship">Your relationship</option><option value="self">Your companion</option><option value="world">The world</option></select>
      </div><button type="button" class="understanding-quiet understanding-archive" data-archive>View archived</button></details></div>
      <p class="understanding-caption understanding-view-note" data-view-note hidden></p>
      <div class="understanding-entries" data-entries></div>
    </section>
    <div class="understanding-health" data-review-health aria-label="Review status" hidden><span data-review-summary></span><details class="understanding-details" data-review-details hidden><summary>Details</summary><div><p data-readiness></p><p data-review-diagnostics></p></div></details></div>
    <details class="understanding-settings" data-settings><summary>${icon("settings")}Settings</summary><div class="understanding-setup">
      <section class="understanding-control-card" data-automation-settings hidden><h3>Automatic understanding</h3><p class="understanding-caption" data-automation-note></p><div class="understanding-actions" data-automation-actions></div></section>
      <section class="understanding-control-card"><div class="understanding-control-heading"><h3>Use in replies</h3><span class="understanding-state" data-use-state></span></div>
      <p data-permission-note></p>
      <div class="understanding-actions" data-policy-actions></div>
      <div class="understanding-confirm" data-policy-confirm hidden></div></section>
      <details class="understanding-settings-section" data-awareness-settings><summary>${icon("monitor")}Activity & background</summary><section class="understanding-awareness" aria-label="Activity and background" data-awareness></section></details>
    </div></details></section>`;
  const root = container.querySelector(".understanding-page");
  const find = selector => root.querySelector(selector);
  const restored = context.state || {};
  let snapshot, view = ["accumulated", "pending", "archived"].includes(restored.view) ? restored.view : "accumulated",
    search = typeof restored.search === "string" ? restored.search : "", domain = restored.domain in domains ? restored.domain : "all",
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
    return Number.isNaN(date.valueOf()) ? "Date unavailable" : new Intl.DateTimeFormat("en", { month: "short", day: "numeric", year: "numeric" }).format(date);
  }
  function stateLabel(item) {
    return item.review === "rejected" ? "Rejected" : item.life === "resolved" ? "Resolved" : item.life !== "active" ? "Paused or expired"
      : item.effect === "pending" ? "Needs approval" : (snapshot.effectiveIds || []).includes(item.id) ? "Available for replies" : "Saved · not selected for replies";
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
        feedback("This action could not be confirmed. Your draft is kept. Refresh to check the current scope and version before trying again.", true);
        if (!snapshot) {
          find("[data-review-health]").hidden = false;
          find("[data-review-summary]").textContent = "Understanding could not be loaded. Refresh to try again.";
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
      action === "correct" && item.effect === "pending" ? "Correction saved. This interpretation still needs approval."
        : action === "correct" ? "Correction saved." : action === "delete" ? "Interpretation and its history forgotten. Original messages kept."
        : action === "approve" ? "This version approved. Conversation use still follows your current access choices." : "Understanding updated.");
    if (ok && (action === "correct" || action === "delete")) { drafts.delete(draftKey(item)); render(); }
  }
  function details(card, item) {
    const detail = disclosure(card, "Details", draftKey(item) + ":sources", "understanding-details understanding-item-details", "history");
    const body = add(detail, "div");
    add(body, "p", `${kinds[item.kind] || "Impression"} · ${domains[item.domain] || "Interpretation"}`);
    add(body, "p", `${stateLabel(item)} · Added ${shortDate(item.observedAt)}`);
    if (item.origin) add(body, "p", ({ expressed: "Expressed in a companion reply", enacted: "Possible pattern in choices",
      secondOrderInferred: "Inferred during review", externallySuggested: "Suggested externally" })[item.origin] || "Authorship needs review");
    if (item.status === "hypothesis") add(body, "p", "Hypothesis · excluded from reply context, including after approval.");
    else if (item.status === "tentative") add(body, "p", "Current tentative understanding · open to revision.");
    if (item.activeUntil && !item.activeUntil.startsWith("9999")) add(body, "p", `Active until ${shortDate(item.activeUntil)}`);
    if (item.correction) { add(body, "h4", "Original interpretation"); add(body, "p", item.content); }
    const sources = item.sources || [];
    add(body, "h4", sources.length ? `Based on ${sources.length} conversation ${sources.length === 1 ? "reference" : "references"}` : "No conversation references available");
    for (const source of sources) add(body, "p", `${({ user: "Your message", assistant: "Companion message", system: "System context" })[source.kind] || "Conversation message"} · ${shortDate(source.occurredAtUtc)}`);
    if (!item.coverage?.length) add(body, "p", "How much of the source was read has not been confirmed.");
    if (item.coverage?.some(part => !part.complete)) add(body, "p", "Some source messages were read only in part.");
    const changes = snapshot.changes?.[item.id] || [];
    if (changes.length) add(body, "h4", "Earlier wording");
    for (const prior of changes) add(body, "p", prior.correction || prior.content || "Earlier wording is unavailable.");
    return body;
  }
  function editor(card, item) {
    const key = draftKey(item), draft = drafts.get(key); if (!draft?.open) return;
    const panel = add(card, "div", null, "understanding-editor");
    const id = "correction-" + crypto.randomUUID(); const label = add(panel, "label", "Your correction"); label.htmlFor = id;
    const edit = add(panel, "textarea"); edit.id = id; edit.maxLength = 2000; edit.value = draft.text;
    edit.addEventListener("input", () => { draft.text = edit.value; save.disabled = stale || !edit.value.trim(); });
    const stale = draft.contextToken !== snapshot.contextToken || draft.revision !== item.revision;
    add(panel, "p", stale ? "The scope or version changed. Your text is kept, but cannot be saved to this version. Copy it or explicitly start from the current version."
      : item.effect === "pending" ? "Saving a correction does not approve this interpretation." : "Only the correction changes; original evidence stays in its history.", "understanding-caption");
    const actions = add(panel, "div", null, "understanding-actions");
    const save = button(actions, "Save correction", () => void review(item, "correct", edit.value, draft), "understanding-primary", "check");
    save.disabled = stale || !edit.value.trim();
    button(actions, "Cancel editing", () => {
      draft.open = false; render(); entryElement(item)?.querySelector("[data-correct]")?.focus();
      feedback("Draft kept. Select Correct to continue editing.");
    });
    if (stale) button(actions, "Use current version", () => {
      draft.contextToken = snapshot.contextToken; draft.revision = item.revision; render(); entryElement(item)?.querySelector("textarea")?.focus();
    });
  }
  function renderEntry(item) {
    const card = add(find("[data-entries]"), "article", null, "understanding-card"); card.dataset.entryId = item.id;
    const pending = item.review === "unreviewed" && item.effect === "pending" && item.life === "active";
    card.classList.toggle("is-pending", pending);
    add(card, "p", item.correction || item.content, "understanding-body");
    if (item.conditions) { const conditions = add(card, "p", null, "understanding-conditions"); add(conditions, "span", "When · "); add(conditions, "span", item.conditions); }
    const footer = add(card, "div", null, "understanding-entry-footer");
    const actions = add(footer, "div", null, "understanding-actions");
    if (pending && item.proposedOperation === "retract") add(card, "p", "Proposed withdrawal · approval will stop using the earlier understanding; its history will be kept.", "understanding-caption");
    if (pending) button(actions, item.proposedOperation === "retract" ? "Approve withdrawal" : "Approve", () => {
      if (!snapshot.state.policy.enabled || !snapshot.state.policy.readSources) { localReviewConsent(card); return; }
      void review(item, "approve");
    }, "understanding-primary", "check").setAttribute("aria-label", item.proposedOperation === "retract" ? "Approve withdrawal" : "Approve this version");
    if (item.review !== "rejected") button(actions, "Correct", () => {
      if (!snapshot.state.policy.enabled || !snapshot.state.policy.readSources) { localReviewConsent(card); return; }
      const key = draftKey(item), prior = drafts.get(key);
      if (prior) prior.open = true;
      else drafts.set(key, { text: item.correction || item.content || "", contextToken: snapshot.contextToken, revision: item.revision, open: true });
      render(); entryElement(item)?.querySelector("textarea")?.focus();
    }, "understanding-quiet", "pencil").dataset.correct = "";
    if (pending) button(actions, "Reject", () => void review(item, "reject"), "understanding-quiet");
    else if (item.review === "approved" && item.life !== "active" && item.life !== "retracted")
      button(actions, "Restore this version", () => void review(item, "restore"));
    const detail = details(footer, item);
    const moreActions = add(detail, "div", null, "understanding-actions");
    if (!pending && item.review !== "rejected" && item.life === "active") {
      button(moreActions, "Pause", () => void review(item, "suspend"));
      button(moreActions, "Mark resolved", () => void review(item, "resolve"));
    }
    const forget = button(moreActions, "Forget", () => {
      if (card.querySelector("[data-forget]")) return;
      const confirm = add(card, "div", null, "understanding-confirm"); confirm.dataset.forget = "";
      add(confirm, "p", "Forget this interpretation and all its versions? Original conversation messages will remain.");
      const row = add(confirm, "div", null, "understanding-actions");
      const binding = { contextToken: snapshot.contextToken, revision: item.revision };
      button(row, "Forget interpretation", () => void review(item, "delete", undefined, binding), "understanding-danger");
      button(row, "Cancel", () => { confirm.remove(); forget.focus(); }).focus();
    }, "understanding-danger");
    editor(card, item);
  }
  function localReviewConsent(card) {
    if (card.querySelector("[data-local-review]")) return;
    const confirm = add(card, "div", null, "understanding-confirm"); confirm.dataset.localReview = "";
    const policy = snapshot.state.policy, display = snapshot.presentation || {};
    add(confirm, "p", `Allow local review for ${display.roleName || "this companion"} · ${display.projectName || "this project"}? Reviewing checks the original messages behind an interpretation on this computer. Approving a version saves it; correcting does not approve it.`);
    add(confirm, "p", policy.useInDialogue
      ? `Your existing permission to use understanding in replies will also resume. Eligible saved items may be sent to ${display.modelRecipient || "the configured conversation model"}. Pending items stay excluded.`
      : "This choice does not send messages to a model or allow understanding to be used in replies.");
    const frozen = { contextToken: snapshot.contextToken, revision: snapshot.state.revision, policy: { ...policy, enabled: true, readSources: true } };
    const row = add(confirm, "div", null, "understanding-actions");
    button(row, "Allow local review", () => void send("understanding.configure", frozen, "Local review allowed. Select Correct or Approve again to review the current version."), "understanding-primary");
    button(row, "Cancel", () => { confirm.remove(); card.querySelector("[data-correct]")?.focus(); }).focus();
  }
  function policyAction(parent, text, purpose, change) {
    button(parent, text, () => {
      const confirm = find("[data-policy-confirm]"); confirm.replaceChildren(); confirm.hidden = false;
      add(confirm, "p", purpose);
      add(confirm, "p", `Companion: ${snapshot.presentation?.roleName || "Current companion"}. Scope: ${snapshot.presentation?.projectName || "Current project"}. Configured model recipient: ${snapshot.presentation?.modelRecipient || "Unavailable"}.`);
      const frozen = { contextToken: snapshot.contextToken, revision: snapshot.state.revision, policy: change({ ...snapshot.state.policy }) };
      if (frozen.policy.enabled) {
        const uses = [["read original messages", frozen.policy.readSources], ["send messages for model review", frozen.policy.externalProcessing],
          ["save new impressions", frozen.policy.saveCandidates], ["use eligible impressions in replies", frozen.policy.useInDialogue]];
        add(confirm, "p", `Allowed after this change: ${uses.filter(([, allowed]) => allowed).map(([label]) => label).join(", ") || "none"}.`);
      }
      const row = add(confirm, "div", null, "understanding-actions");
      button(row, "Confirm", () => void send("understanding.configure", frozen, "Access updated."), "understanding-primary");
      button(row, "Cancel", () => { confirm.hidden = true; }).focus();
    });
  }
  function automaticSetup() {
    const confirm = find("[data-policy-confirm]"); confirm.replaceChildren(); confirm.hidden = false;
    const policy = snapshot.state.policy, service = snapshot.automation;
    const binding = { contextToken: snapshot.contextToken, revision: snapshot.state.revision, routeHash: service?.routeHash };
    add(confirm, "p", `为 ${snapshot.presentation.roleName} · ${snapshot.presentation.projectName} 开启自动理解。发送给 ${snapshot.presentation.modelRecipient}。首次开启只整理开启后的新内容，同角色和项目的新对话可共享有效理解。`);
    const modeLabel = add(confirm, "label", "生效方式 "); const mode = add(modeLabel, "select"); mode.setAttribute("aria-label", "生效方式");
    for (const [value, text] of [["bounded", "有限自动生效：低影响理解经校验后用于回复"], ["proposals", "仅生成候选：确认后才能用于回复"]]) {
      const option = add(mode, "option", text); option.value = value;
    }
    mode.value = policy.mode === "proposals" ? "proposals" : "bounded";
    const choices = {};
    for (const [key, text] of [["readSources", "允许读取此角色和项目的对话来源"], ["externalProcessing", "允许将所选对话片段发送给上述模型分析"], ["saveCandidates", "允许保存带来源的理解与待确认候选"], ["useInDialogue", "允许在后续回复中使用有效理解（待确认内容除外）"]]) {
      const label = add(confirm, "label", null, "understanding-choice"); const box = add(label, "input"); box.type = "checkbox";
      box.checked = Boolean(policy[key]); add(label, "span", text); choices[key] = box;
    }
    const limitLabel = add(confirm, "label", "每日后台派发上限（所有角色和项目共用） ");
    const limit = add(limitLabel, "input"); limit.type = "number"; limit.min = "1"; limit.max = "10000"; limit.step = "1";
    limit.setAttribute("aria-label", "每日后台派发上限"); limit.value = String(service?.budget?.limit || 12);
    add(confirm, "p", "理解、独立分类和重试分别计数，失败或取消不退还额度。代理内部重试可能增加实际上游调用与费用。更改上限影响全部角色和项目，已用次数不会清零。");
    const row = add(confirm, "div", null, "understanding-actions");
    const start = button(row, "确认自动理解设置", () => {
      if (!Number.isInteger(Number(limit.value)) || !limit.checkValidity()) return;
      void send("understanding.automation", { ...binding, dailyLimit: Number(limit.value), policy: {
        mode: mode.value, enabled: true, ...Object.fromEntries(Object.entries(choices).map(([key, box]) => [key, box.checked]))
      } }, "自动理解设置已保存。后台会在满足条件时整理新内容。");
    }, "understanding-primary");
    const validate = () => { start.disabled = !binding.routeHash || !choices.readSources.checked || !choices.externalProcessing.checked || !choices.saveCandidates.checked || !limit.checkValidity(); };
    for (const box of Object.values(choices)) box.addEventListener("change", validate);
    limit.addEventListener("input", validate); validate();
    button(row, "取消", () => { confirm.hidden = true; });
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
    add(recipient, "p", `Model recipient: ${status.modelRecipient || "No model configured"}.`, "understanding-awareness-recipient");
    if (!available) add(card, "p", "Ambient awareness is unavailable for the current context. Refresh to check this companion and scope.", "understanding-caption");
    function choice(parent, name, title, description) {
      const label = add(parent, "label", null, "understanding-awareness-option");
      const text = add(label, "span"); add(text, "strong", title); add(text, "span", description, "understanding-caption");
      const input = add(label, "input"); input.type = "checkbox"; input.checked = draft.options[name]; input.disabled = !available;
      input.dataset.awarenessOption = name;
      const toggle = add(label, "span", null, "understanding-switch"); toggle.setAttribute("aria-hidden", "true");
      input.addEventListener("change", () => { draft.options[name] = input.checked; });
    }
    const sources = add(card, "section", null, "understanding-settings-group"); add(sources, "h4", "Information sources");
    choice(sources, "activityContextEnabled", "Use activity context", "Focus, reminders, and the local task book shared across companions and projects.");
    choice(sources, "applicationsEnabled", "Use application overview", "Application names, the app in front, and window counts. Window titles and content need separate permission.");
    const windows = add(sources, "section", null, "understanding-awareness-windows"); add(windows, "h4", "Window access");
    for (const allowed of status.windows || []) {
      const row = add(windows, "div", null, "understanding-awareness-window");
      add(row, "p", allowed.title || "Untitled window"); add(row, "p", allowed.application || "Unknown application", "understanding-caption");
    }
    if (!status.windows?.length) add(windows, "p", "No windows allowed. Choose a window to share its title and visible text.", "understanding-caption");
    const windowActions = add(windows, "div", null, "understanding-actions");
    const choose = button(windowActions, "Allow a window", () => {
      const binding = { contextToken: status.contextToken, revision: status.revision };
      windowChoices = null; renderAwareness();
      void send("understanding.awareness.windows", { contextToken: binding.contextToken }, "Choose one window to allow background reading.", next => {
        if (next?.contextToken !== binding.contextToken || !Array.isArray(next.windows) ||
            snapshot.awareness?.contextToken !== binding.contextToken || snapshot.awareness?.revision !== binding.revision) throw new Error("stale window choices");
        windowChoices = { ...binding, windows: next.windows }; renderAwareness();
      });
    });
    choose.disabled = !available || !status.modelApprovalCurrent;
    if (available && !status.modelApprovalCurrent) add(windows, "p", "Save the choices for this model before allowing a window.", "understanding-caption");
    if (status.windows?.length) {
      const clear = button(windowActions, "Clear window access", () => void send("understanding.awareness.clearWindows", {
        contextToken: status.contextToken, revision: status.revision
      }, "Window access cleared. Background window reading stopped."));
      clear.disabled = !available;
    }
    if (windowChoices) {
      const choices = add(windows, "div", null, "understanding-awareness-choices");
      add(choices, "p", "Allowing a window lets your companion repeatedly read its title and visible text, and use them with the model shown above. Access ends when its title changes, it closes, the scope or model changes, or you clear access. A new selection replaces the previous one.");
      if (!windowChoices.windows.length) add(choices, "p", "No eligible windows are available.");
      const binding = windowChoices;
      for (const candidate of binding.windows) {
        const row = add(choices, "div", null, "understanding-awareness-window");
        add(row, "p", candidate.title || "Untitled window"); add(row, "p", candidate.application || "Unknown application", "understanding-caption");
        const allow = button(row, "Allow this window", () => void send("understanding.awareness.allowWindow", {
          contextToken: binding.contextToken, revision: binding.revision, candidateToken: candidate.token
        }, "Window allowed for background reading in the current scope."));
        allow.disabled = !available || !status.modelApprovalCurrent;
      }
      button(choices, "Close window choices", () => { windowChoices = null; renderAwareness(); });
    }
    const background = add(card, "section", null, "understanding-settings-group");
    choice(background, "backgroundEnabled", "Background thought", "Use approved context for extra model requests between conversations.");
    const budget = add(background, "p", validLimit ? `Up to ${savedLimit} extra model requests in any 24 hours. Your saved allowance is kept.`
      : "The saved request allowance is unavailable. Refresh before saving.", "understanding-caption understanding-background-status");
    budget.dataset.awarenessBudget = "";
    add(background, "p", !status.options?.backgroundEnabled ? "Background thought is off." : !status.modelApprovalCurrent
      ? "Background thought is paused until you approve the current model."
      : activityStates.get(status.activityStatus) || "Background thought is currently unavailable. Refresh to check its state.", "understanding-caption");
    const saveBar = add(card, "div", null, "understanding-save-bar");
    const copy = add(saveBar, "div");
    const dirty = add(copy, "p", "", "understanding-draft-state"); dirty.setAttribute("role", "status");
    add(copy, "p", "Saving allows these sources for this companion, scope and model. A model change requires approval again.", "understanding-caption");
    const save = button(saveBar, "Save", () => void send("understanding.awareness.configure", {
      contextToken: draft.contextToken, revision: draft.revision, options: { ...draft.options, dailyRequestLimit: savedLimit }
    }, "Ambient awareness choices saved for the current model."), "understanding-primary", "check");
    save.setAttribute("aria-label", "Save & approve awareness");
    function validateChoices() {
      save.disabled = !available || !validLimit;
      const changed = Object.keys(draft.options).some(key => draft.options[key] !== status.options?.[key]);
      dirty.textContent = !validLimit ? "Refresh to check the saved allowance." : changed ? "You have unsaved choices." : !status.modelApprovalCurrent ? "Approval needed for this model." : "Your choices are saved.";
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
    if (busy || disposed || !snapshot || !["accumulated", "pending", "archived"].includes(next)) return;
    view = next; feedback(""); renderEntries();
  }
  function renderEntries() {
    const groups = entryGroups(), query = search.trim().toLocaleLowerCase();
    const items = groups[view].filter(item => (domain === "all" || item.domain === domain) && (!query ||
      [item.content, item.correction, item.conditions, kinds[item.kind], domains[item.domain]].filter(Boolean).join(" ").toLocaleLowerCase().includes(query)));
    for (const tab of root.querySelectorAll("[data-view]")) {
      tab.setAttribute("aria-pressed", String(tab.dataset.view === view));
      tab.querySelector("[data-count]").textContent = String(groups[tab.dataset.view].length);
      tab.setAttribute("aria-description", `${groups[tab.dataset.view].length} items`);
      if (tab.dataset.view === "archived") tab.hidden = view !== "archived";
    }
    const hasEntries = Object.values(groups).some(items => items.length);
    find("[data-list-tools]").hidden = !hasEntries && view === "accumulated" && !query && domain === "all";
    find("[data-search-tools] > summary").textContent = query || domain !== "all" ? "Search · filtered" : "Search";
    find("[data-view-note]").hidden = view === "accumulated" || !groups[view].length;
    find("[data-view-note]").textContent = view === "pending" ? "Not used in replies until you approve."
      : "Paused, resolved, expired or rejected impressions.";
    find("[data-archive]").textContent = `View archived (${groups.archived.length})`;
    find("[data-entries]").replaceChildren();
    if (!items.length) {
      const empty = add(find("[data-entries]"), "div", null, "understanding-empty");
      empty.insertAdjacentHTML("afterbegin", icon("inbox"));
      if (query || domain !== "all") {
        add(empty, "h3", "No matching understanding");
        add(empty, "p", "Try another search or clear the filters.");
        button(empty, "Clear filters", () => {
          search = ""; domain = "all"; find("[data-search]").value = ""; find("[data-domain]").value = "all";
          renderEntries(); find("[data-search-tools]").open = true; find("[data-search]").focus();
        });
      } else {
        add(empty, "h3", view === "pending" ? "You're all caught up" : view === "archived" ? "Nothing archived" : "Nothing here yet");
        add(empty, "p", view === "pending" ? "Any impressions that need your approval will appear here."
          : view === "archived" ? "Impressions you pause, resolve, or reject will stay here."
          : groups.pending.length ? "There are impressions waiting in To review."
          : "No impressions saved for this companion.");
      }
    }
    for (const item of items) renderEntry(item);
  }
  function render() {
    const policy = snapshot.state.policy, display = snapshot.presentation || {};
    find("[data-scope]").textContent = `${display.roleName || "Current companion"} · ${display.projectName || "Current project"}`;
    renderAwareness(); renderEntries();
    const all = [...(snapshot.accumulated || []), ...(snapshot.pending || [])];
    const canUse = policy.enabled && policy.readSources && policy.useInDialogue;
    const useState = find("[data-use-state]"); useState.textContent = !policy.enabled ? "Paused" : canUse ? "Allowed" : "Off";
    useState.classList.toggle("is-available", canUse);
    const automation = snapshot.automation, budget = automation?.budget, run = snapshot.lastRun;
    const automatic = policy.enabled && policy.mode !== "off" && policy.readSources && policy.externalProcessing && policy.saveCandidates;
    const reasons = {
      UNDERSTANDING_MODEL_UNAVAILABLE: "Choose a model to use automatic understanding.",
      UNDERSTANDING_BUDGET_UNAUTHORIZED: "Approve background requests for the current model in Settings.",
      UNDERSTANDING_BUDGET_LIMITED: "Today's automatic understanding allowance has been used.",
      UNDERSTANDING_CLOCK_ROLLBACK: "Automatic understanding is paused because the system clock moved back.",
      UNDERSTANDING_DAILY_STORE_UNAVAILABLE: "Automatic understanding is paused because its allowance record is unavailable."
    };
    const issue = automatic && automation?.code;
    const failed = run?.stage === "failed";
    const summary = !display.automaticReviewAvailable ? "Automatic review isn't available in this build."
      : issue ? reasons[issue] || "Automatic understanding is temporarily unavailable."
      : failed ? "The last review failed. Eligible work will wait before trying again."
      : automatic && run?.stage === "running" ? "Reviewing new conversations…" : "";
    find("[data-review-health]").hidden = !summary;
    find("[data-review-summary]").textContent = summary;
    find("[data-review-details]").hidden = !issue && !failed;
    find("[data-readiness]").textContent = !display.automaticReviewAvailable
      ? "Automatic understanding is not available yet in this build. Existing impressions can still be reviewed."
      : !automatic ? "Automatic understanding is off or paused."
      : run?.stage === "waiting_foreground" ? "Waiting for the current conversation to finish."
      : snapshot.pendingSources > 0 ? "New conversations are waiting for the next review opportunity."
      : "Waiting for new conversations.";
    const diagnostics = [];
    if (issue) diagnostics.push(`Service: ${issue}.`);
    if (run) diagnostics.push(`Last review: ${new Date(run.at).toLocaleString()} · ${run.newItems || 0} new impressions.${failed ? ` Code: ${run.code || "Unavailable"}.` : ""}`);
    find("[data-review-diagnostics]").textContent = diagnostics.join(" ");
    find("[data-automation-settings]").hidden = !display.automaticReviewAvailable;
    find("[data-automation-note]").textContent = `${automatic ? "Review new conversations automatically." : "Automatic understanding is off or paused."} ${budget ? `${budget.used} of ${budget.limit} background requests used today. Resets ${new Date(budget.resetsAt).toLocaleString()}.` : "Request allowance is not available yet."} Refresh only checks status.`;
    const automaticActions = find("[data-automation-actions]"); automaticActions.replaceChildren();
    if (display.automaticReviewAvailable) button(automaticActions, automatic ? "自动理解设置" : "开启自动理解", automaticSetup, "understanding-primary");
    find("[data-permission-note]").textContent = !policy.enabled ? "Understanding is paused. Saved items are kept out of replies."
      : !policy.readSources || !policy.useInDialogue ? "Saved items are currently kept out of replies."
      : "Eligible saved items may be used in this companion's replies. Pending items stay out.";
    const actions = find("[data-policy-actions]"); actions.replaceChildren();
    const hasAccess = policy.readSources || policy.externalProcessing || policy.saveCandidates || policy.useInDialogue || policy.mode !== "off";
    if (hasAccess && !policy.enabled) policyAction(actions, "Resume understanding", "Resume understanding with your existing access choices. Saved impressions may be used again if you previously allowed that.", p => ({ ...p, enabled: true }));
    else if (hasAccess && policy.enabled) {
      if (all.length && policy.readSources && !policy.useInDialogue) policyAction(actions, "Use in replies", "Allow eligible saved impressions to be sent to the conversation model and used in this companion's replies. Pending impressions stay excluded.", p => ({ ...p, useInDialogue: true }));
      if (policy.useInDialogue) policyAction(actions, "停止用于回复", "停止把已保存的理解用于后续回复，保留其他授权和已保存内容。", p => ({ ...p, useInDialogue: false }));
      policyAction(actions, "Pause understanding", "Pause local review and conversation use. Keep saved interpretations and existing access choices.", p => ({ ...p, enabled: false }));
    }
    if (hasAccess)
      policyAction(actions, "Turn off & revoke access", "Stop understanding and revoke source access, external processing, candidate saving and conversation use for this scope. Saved interpretations remain; originals are not deleted.", () => ({ mode: "off", enabled: false, readSources: false, externalProcessing: false, saveCandidates: false, useInDialogue: false }));
    find("[data-policy-confirm]").hidden = true;
  }
  for (const tab of root.querySelectorAll("[data-view]")) tab.addEventListener("click", () => setView(tab.dataset.view));
  find("[data-search]").addEventListener("input", event => { search = event.target.value; if (snapshot) renderEntries(); });
  find("[data-domain]").addEventListener("change", event => { domain = event.target.value; if (snapshot) renderEntries(); });
  find("[data-archive]").addEventListener("click", () => {
    if (busy || disposed || !snapshot) return;
    search = ""; domain = "all"; find("[data-search]").value = ""; find("[data-domain]").value = "all";
    setView("archived"); find("[data-search-tools]").open = false;
    find('[data-view="archived"]').focus();
  });
  find("[data-refresh]").addEventListener("click", () => void send("understanding.get"));
  find("[data-search]").value = search; find("[data-domain]").value = domain;
  find("[data-settings]").open = restored.settingsOpen === true;
  find("[data-search-tools]").open = restored.searchOpen === true || Boolean(search) || domain !== "all";
  find("[data-awareness-settings]").open = restored.awarenessOpen === true;
  const dispose = () => { disposed = true; for (const el of root.querySelectorAll("button,input,textarea,select")) el.disabled = true; style.remove(); };
  dispose.getState = () => ({ view, search, domain, settingsOpen: find("[data-settings]").open,
    searchOpen: find("[data-search-tools]").open, awarenessOpen: find("[data-awareness-settings]").open, opened: [...opened], awarenessDraft });
  context.signal.addEventListener("abort", dispose, { once: true });
  await send("understanding.get"); return dispose;
}
