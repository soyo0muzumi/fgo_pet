// Drafts live only in this WebView's module lifetime, including navigation away and back.
const drafts = new Map();
const operations = new Map();
const domains = { user: "About you", relationship: "Your relationship", self: "Your companion", world: "The world" };

export async function mount(container, context) {
  const style = document.createElement("link");
  style.rel = "stylesheet"; style.href = "pages/understanding.css"; document.head.append(style);
  container.innerHTML = `<section class="understanding-page" aria-label="Understanding">
    <header class="understanding-intro"><p class="understanding-eyebrow" data-scope></p>
      <h2>What your companion understands</h2>
      <p>Interpretations from your conversations. You can correct or forget them. They can be mistaken.</p></header>
    <p class="understanding-feedback" role="alert" data-error hidden></p>
    <p class="understanding-feedback" role="status" aria-live="polite" data-status>Loading understanding…</p>
    <nav class="understanding-tabs" aria-label="Understanding views">
      <button type="button" data-view="accumulated" aria-pressed="true">Saved</button>
      <button type="button" data-view="pending" aria-pressed="false">To review</button>
      <button type="button" data-view="archived" aria-pressed="false">Archived</button>
      <button type="button" class="understanding-refresh" data-refresh>Refresh</button></nav>
    <p class="understanding-caption" data-view-note></p><div class="understanding-entries" data-entries></div>
    <section class="understanding-setup" aria-labelledby="understanding-purpose">
      <h3 id="understanding-purpose">Conversation use</h3>
      <p data-readiness></p><p data-permission-note></p>
      <div class="understanding-actions" data-policy-actions></div>
      <div class="understanding-confirm" data-policy-confirm hidden></div>
      <details><summary>Current access & service</summary><div data-access></div></details>
    </section></section>`;
  const root = container.querySelector(".understanding-page");
  const find = selector => root.querySelector(selector);
  let snapshot, view = "accumulated", disposed = false, busy = false;
  const disabled = new WeakMap();
  function add(parent, tag, text, className) {
    const el = document.createElement(tag); if (text != null) el.textContent = text;
    if (className) el.className = className; parent.append(el); return el;
  }
  function button(parent, text, click, className = "") {
    const el = add(parent, "button", text, className); el.type = "button";
    el.addEventListener("click", () => { if (!busy && !disposed) click(); }); return el;
  }
  function feedback(text, failed = false) {
    const el = find(failed ? "[data-error]" : "[data-status]"); el.textContent = text; el.hidden = !text;
  }
  function scopeKey() { return JSON.stringify([snapshot?.scope?.servantId, snapshot?.scope?.projectId]); }
  function draftKey(item) { return scopeKey() + ":" + item.id; }
  function entryElement(item) { return [...root.querySelectorAll("[data-entry-id]")].find(el => el.dataset.entryId === item.id); }
  async function send(type, payload = {}, notice = "") {
    if (disposed || busy) return false;
    busy = true; root.setAttribute("aria-busy", "true"); feedback("", true);
    for (const el of root.querySelectorAll("button,input,textarea")) { disabled.set(el, el.disabled); el.disabled = true; }
    try {
      const next = await context.request(type, payload);
      if (disposed) return false;
      if (!next?.state?.policy || typeof next.contextToken !== "string") throw new Error("invalid snapshot");
      snapshot = next; render(); feedback(notice); return true;
    } catch {
      if (!disposed) {
        feedback("", false);
        feedback("This action could not be confirmed. Your draft is kept. Refresh to check the current scope and version before trying again.", true);
        if (!snapshot) find("[data-readiness]").textContent = "Understanding could not be loaded. Refresh to try again.";
      }
      return false;
    } finally {
      busy = false; root.removeAttribute("aria-busy");
      if (!disposed) for (const el of root.querySelectorAll("button,input,textarea"))
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
    const detail = add(card, "details", null, "understanding-details"); add(detail, "summary", "Sources & history");
    const body = add(detail, "div");
    add(body, "p", `Version ${item.revision} · ${item.review} · ${item.effect} · ${item.life}`);
    add(body, "p", `Observed ${item.observedAt}`);
    if (item.activeUntil && !item.activeUntil.startsWith("9999")) add(body, "p", `Active until ${item.activeUntil}`);
    if (item.correction) { add(body, "h4", "Original interpretation"); add(body, "p", item.content); }
    for (const source of item.sources || []) add(body, "p", `${source.kind} · ${source.occurredAtUtc}\nMessage ${source.messageId}\nSource version ${source.fingerprint}`);
    if (!item.coverage?.length) add(body, "p", "Read coverage was not attested. A current source version does not prove the whole message was read.");
    for (const coverage of item.coverage || []) add(body, "p", `Read coverage: ${coverage.includedScalars} of ${coverage.totalScalars} characters, starting at ${coverage.startScalar}. ${coverage.complete ? "Complete." : "Partial."}`);
    const changes = snapshot.changes?.[item.id] || [];
    if (changes.length) add(body, "h4", "Recent versions");
    for (const prior of changes) add(body, "p", `Version ${prior.revision} · ${prior.review} · ${prior.effect} · ${prior.life}${prior.correction ? "\n" + prior.correction : ""}`);
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
    const save = button(actions, "Save correction", () => void review(item, "correct", edit.value, draft), "understanding-primary");
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
    const meta = add(card, "div", null, "understanding-meta"); add(meta, "span", domains[item.domain] || "Interpretation", "understanding-tag");
    const usable = (snapshot.effectiveIds || []).includes(item.id);
    add(meta, "span", item.review === "rejected" ? "Rejected" : item.life === "resolved" ? "Resolved" : item.life !== "active" ? "Paused or expired"
      : item.effect === "pending" ? "Needs approval" : usable ? "Available for replies" : "Saved · not selected for replies", "understanding-caption");
    add(card, "p", item.correction || item.content, "understanding-body");
    if (item.conditions) add(card, "p", item.conditions, "understanding-conditions");
    const actions = add(card, "div", null, "understanding-actions");
    if (item.review !== "rejected") button(actions, "Correct", () => {
      if (!snapshot.state.policy.enabled || !snapshot.state.policy.readSources) { localReviewConsent(card); return; }
      const key = draftKey(item), prior = drafts.get(key);
      if (prior) prior.open = true;
      else drafts.set(key, { text: item.correction || "", contextToken: snapshot.contextToken, revision: item.revision, open: true });
      render(); entryElement(item)?.querySelector("textarea")?.focus();
    }, "understanding-primary").dataset.correct = "";
    const forget = button(actions, "Forget", () => {
      if (card.querySelector("[data-forget]")) return;
      const confirm = add(card, "div", null, "understanding-confirm"); confirm.dataset.forget = "";
      add(confirm, "p", "Forget this interpretation and all its versions? Original conversation messages will remain.");
      const row = add(confirm, "div", null, "understanding-actions");
      const binding = { contextToken: snapshot.contextToken, revision: item.revision };
      button(row, "Forget interpretation", () => void review(item, "delete", undefined, binding), "understanding-danger");
      button(row, "Cancel", () => { confirm.remove(); forget.focus(); }).focus();
    });
    if (item.review === "unreviewed" && item.effect === "pending") {
      button(actions, "Approve this version", () => {
        if (!snapshot.state.policy.enabled || !snapshot.state.policy.readSources) { localReviewConsent(card); return; }
        void review(item, "approve");
      });
      button(actions, "Reject", () => void review(item, "reject"));
    } else if (item.review !== "rejected" && item.life === "active") {
      const more = add(card, "details", null, "understanding-details"); add(more, "summary", "More actions");
      const row = add(more, "div", null, "understanding-actions");
      button(row, "Pause", () => void review(item, "suspend")); button(row, "Mark resolved", () => void review(item, "resolve"));
    } else if (item.review === "approved" && item.life !== "retracted") button(actions, "Restore this version", () => void review(item, "restore"));
    editor(card, item); details(card, item);
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
      const row = add(confirm, "div", null, "understanding-actions");
      button(row, "Confirm", () => void send("understanding.configure", frozen, "Access updated."), "understanding-primary");
      button(row, "Cancel", () => { confirm.hidden = true; }).focus();
    });
  }
  function render() {
    const policy = snapshot.state.policy, display = snapshot.presentation || {};
    find("[data-scope]").textContent = `${display.roleName || "Current companion"} · ${display.projectName || "Current project"}`;
    const all = [...(snapshot.accumulated || []), ...(snapshot.pending || [])];
    const archived = item => item.review === "rejected" || item.life !== "active";
    const pending = item => !archived(item) && item.effect === "pending" && item.review === "unreviewed";
    const items = all.filter(item => view === "archived" ? archived(item) : view === "pending" ? pending(item) : !archived(item) && !pending(item));
    find("[data-view-note]").textContent = view === "pending" ? "These interpretations are waiting for your approval and are not used in replies."
      : view === "archived" ? "Paused, resolved, expired or rejected interpretations. These are not active understanding."
      : "Saved does not mean used in replies. Availability depends on your access choices and current evidence.";
    for (const tab of root.querySelectorAll("[data-view]")) tab.setAttribute("aria-pressed", String(tab.dataset.view === view));
    find("[data-entries]").replaceChildren();
    if (!items.length) {
      const empty = add(find("[data-entries]"), "div", null, "understanding-empty");
      add(empty, "h3", view === "pending" ? "Nothing waiting for review" : view === "archived" ? "Nothing archived" : "No saved understanding yet");
      add(empty, "p", view === "accumulated" ? "Nothing has been saved for this companion and project." : "Items will appear here when their status changes.");
    }
    for (const item of items) renderEntry(item);
    find("[data-readiness]").textContent = "Automatic understanding is not available yet. You can review saved interpretations here.";
    find("[data-permission-note]").textContent = !policy.enabled ? "Understanding is paused. Saved items are kept out of replies."
      : !policy.readSources || !policy.useInDialogue ? "Saved items are currently kept out of replies."
      : "Eligible saved items may be used in this companion's replies. Pending items stay out.";
    const actions = find("[data-policy-actions]"); actions.replaceChildren();
    const hasAccess = policy.readSources || policy.externalProcessing || policy.saveCandidates || policy.useInDialogue || policy.mode !== "off";
    if (hasAccess && !policy.enabled) policyAction(actions, "Resume understanding", "Resume the existing access choices shown below. This does not add a model budget or enable automatic accumulation.", p => ({ ...p, enabled: true }));
    else if (hasAccess && policy.enabled) {
      if (all.length && policy.readSources && !policy.useInDialogue) policyAction(actions, "Use in replies", "Allow eligible saved interpretations to be selected for this companion's replies in the current project and sent to the configured model with your conversation. Pending interpretations stay excluded. Approved versions and any previously authorized bounded items follow the existing access choices shown below.", p => ({ ...p, useInDialogue: true }));
      policyAction(actions, "Pause understanding", "Pause local review and conversation use. Keep saved interpretations and existing access choices.", p => ({ ...p, enabled: false }));
    }
    if (hasAccess)
      policyAction(actions, "Turn off & revoke access", "Stop understanding and revoke source access, external processing, candidate saving and conversation use for this scope. Saved interpretations remain; originals are not deleted.", () => ({ mode: "off", enabled: false, readSources: false, externalProcessing: false, saveCandidates: false, useInDialogue: false }));
    find("[data-policy-confirm]").hidden = true;
    const access = find("[data-access]"); access.replaceChildren();
    add(access, "p", "Automatic review has no authorized host budget connected. Opening this page sends no model request.");
    add(access, "p", `Configured conversation recipient: ${display.modelRecipient || "Unavailable"}.`);
    add(access, "p", `Existing mode: ${policy.mode}. Source validation: ${policy.readSources ? "allowed" : "off"}. External review processing: ${policy.externalProcessing ? "allowed, but no host budget is connected" : "off"}. Candidate saving: ${policy.saveCandidates ? "allowed" : "off"}. Conversation use: ${policy.useInDialogue ? "allowed" : "off"}.`);
    add(access, "p", "Existing permissions are preserved until you explicitly change them. Approval is permission to use an interpretation, not proof that it is true.");
  }
  for (const tab of root.querySelectorAll("[data-view]")) tab.addEventListener("click", () => { if (!busy) { view = tab.dataset.view; if (snapshot) render(); } });
  find("[data-refresh]").addEventListener("click", () => void send("understanding.get"));
  const dispose = () => { disposed = true; for (const el of root.querySelectorAll("button,input,textarea")) el.disabled = true; style.remove(); };
  context.signal.addEventListener("abort", dispose, { once: true });
  await send("understanding.get"); return dispose;
}
