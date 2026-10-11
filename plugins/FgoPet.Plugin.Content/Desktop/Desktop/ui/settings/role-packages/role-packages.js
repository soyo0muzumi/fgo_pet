const english = {
  "characters.busy": "Working…",
  "characters.reload": "Reload",
  "characters.catalog": "Character packages",
  "characters.search": "Search characters",
  "characters.searchHint": "Name or package ID",
  "characters.chooseFile": "Choose local file",
  "characters.noFile": "No file selected",
  "characters.install": "Install",
  "characters.scan": "Rescan",
  "characters.empty": "No matching character packages.",
  "characters.safety": "Only a selected local .fgopetpack file is installed after validation. An unverified source is not official or store certification. Package contents are not uploaded automatically.",
  "characters.companion": "Back to Companion",
  "characters.detail": "Character details",
  "characters.backAria": "Back to characters",
  "characters.back": "<span aria-hidden=\"true\">‹</span> Back",
  "characters.preview": "Character preview",
  "characters.removed": "This package is unavailable. Return to the catalog and rescan.",
  "characters.sections": "Character detail sections",
  "characters.looks": "Appearance",
  "characters.addressSettings": "Address",
  "characters.info": "Package information",
  "characters.availableLooks": "Available appearances",
  "characters.folder": "Open package folder",
  "characters.useLook": "Use this appearance",
  "characters.uninstall": "Uninstall this version",
  "characters.addressHint": "Set how this character addresses you. Your profile name stays separate.",
  "characters.addressSource": "Address source",
  "characters.packageDefault": "Use package default",
  "characters.customAddress": "Custom address",
  "characters.custom": "Custom",
  "characters.saveAddress": "Save address",
  "characters.address": "Address",
  "characters.id": "Package ID",
  "characters.version": "Version",
  "characters.source": "Source",
  "characters.settings": "Package settings",
  "characters.settingsHint": "These options are declared by the package. Save to apply them to this character.",
  "characters.noSettings": "This package has no configurable options.",
  "characters.saveSettings": "Save package settings",
  "characters.failed": "The action did not complete. Try again.",
  "characters.unnamed": "Unnamed character",
  "characters.unknownVersion": "Unknown version",
  "characters.active": "Currently in use",
  "characters.embedded": "Built-in package",
  "characters.installed": "Installed",
  "characters.details": "View details",
  "characters.look": "Appearance",
  "characters.selected": "In use",
  "characters.unavailable": "Package unavailable",
  "characters.activePackage": "This character is currently in use",
  "characters.inactivePackage": "This character is not active",
  "characters.available": "Package available",
  "characters.unavailableNow": "This package is unavailable",
  "characters.unspecified": "Not specified",
  "characters.notice": "Package notice",
  "characters.actionFailed": "The package action failed. Check the current state and try again.",
  "characters.refreshed": "The action did not complete. Current state was reloaded.",
  "characters.refreshFailed": "The action and current state could not be confirmed. Try again.",
  "characters.pendingRefreshed": "The action is incomplete. Current state was reloaded.",
  "characters.pendingFailed": "The action is incomplete and current state could not be loaded. Try again.",
  "characters.stateFailed": "Character state could not be loaded. Try again.",
  "characters.styleFailed": "The page style could not be loaded. Reopen the page.",
  "characters.catalogFailed": "Character packages could not be loaded. Try again.",
  "characters.character": "Character",
  "characters.thisPackage": "this package",
  "characters.meta": "{version} · {count} appearances",
  "characters.versionValue": "Version {version}",
  "characters.previewName": "{name} preview",
  "characters.defaultAddress": "Package default: {name}",
  "characters.uninstallPrompt": "Uninstall {name}, version {version}?"
};
const t = (key, values = {}) => (english[key] || key).replace(/\{(\w+)\}/g, (match, name) => values[name] === undefined ? match : String(values[name]));

const PAGE_ID = "RolePackages";
const MAX_PREVIEW_LENGTH = 2_000_000;

const MARKUP = `
  <div class="rp-page" data-role-packages-page data-state="loading">
    <div class="rp-feedback" aria-live="polite" aria-atomic="true">
      <p data-busy hidden>${t("characters.busy")}</p>
      <p data-status role="status" hidden></p>
      <p data-error role="alert" aria-live="assertive" hidden></p>
      <div class="rp-diagnostic" data-diagnostic hidden><strong data-diagnostic-heading></strong><p data-diagnostic-text></p></div>
      <button class="rp-button" type="button" data-retry hidden>${t("characters.reload")}</button>
    </div>
    <section class="rp-catalog" data-route="catalog" aria-label="${t("characters.catalog")}">
      <div class="rp-toolbar">
        <label class="rp-search-label">${t("characters.search")}<span class="rp-search-field"><input type="search" data-catalog-search maxlength="256" autocomplete="off" placeholder="${t("characters.searchHint")}"><button class="rp-button rp-file-picker" type="button" data-select-file aria-label="${t("characters.chooseFile")}" title="${t("characters.chooseFile")}"><svg viewBox="0 0 24 24" aria-hidden="true"><path d="M3 7V5h6l2 2h10v12H3Z"/></svg></button></span></label>
        <span class="rp-file-name" data-selected-file-name>${t("characters.noFile")}</span>
        <button class="rp-button rp-primary" type="button" data-install-package disabled>${t("characters.install")}</button>
        <button class="rp-button" type="button" data-rescan>${t("characters.scan")}</button>
      </div>
      <p class="rp-scan-status" data-scan-status></p>
      <div class="rp-package-grid" data-package-list></div>
      <p class="rp-empty" data-catalog-empty hidden>${t("characters.empty")}</p>
      <p class="rp-safety-note">${t("characters.safety")}</p>
      <button class="rp-text-button" type="button" data-back-to-personalization>${t("characters.companion")}</button>
    </section>
    <section class="rp-detail" data-route="detail" aria-label="${t("characters.detail")}" hidden>
      <header class="rp-detail-header" data-detail-header>
        <button class="rp-back-button" type="button" data-back-to-catalog aria-label="${t("characters.backAria")}">${t("characters.back")}</button>
        <div class="rp-detail-title"><h2 data-detail-name></h2><p data-detail-version></p></div>
        <span class="rp-badge" data-detail-source></span>
        <img class="rp-detail-preview" data-detail-preview alt="${t("characters.preview")}" hidden>
        <p class="rp-detail-unavailable" data-detail-unavailable hidden>${t("characters.removed")}</p>
      </header>
      <nav class="rp-tabs" aria-label="${t("characters.sections")}">
        <button type="button" data-section="appearance">${t("characters.looks")}</button>
        <button type="button" data-section="address">${t("characters.addressSettings")}</button>
        <button type="button" data-section="packageInfo">${t("characters.info")}</button>
      </nav>
      <div class="rp-section-panel" data-section-panel="appearance">
        <section class="rp-card">
          <div class="rp-card-heading"><div><h3>${t("characters.availableLooks")}</h3><p data-active-status></p></div><button class="rp-button" type="button" data-open-detail-folder>${t("characters.folder")}</button></div>
          <div class="rp-appearance-list" data-appearance-list></div>
          <div class="rp-action-row"><button class="rp-button rp-primary" type="button" data-activate-appearance>${t("characters.useLook")}</button><button class="rp-button rp-danger" type="button" data-uninstall-package>${t("characters.uninstall")}</button></div>
        </section>
      </div>
      <div class="rp-section-panel" data-section-panel="address" hidden>
        <section class="rp-card">
          <div class="rp-card-heading"><div><h3>${t("characters.address")}</h3><p>${t("characters.addressHint")}</p></div></div>
          <p class="rp-default-address" data-address-default></p>
          <fieldset class="rp-address-modes"><legend>${t("characters.addressSource")}</legend>
            <label><input type="radio" name="rp-address-mode" value="packageDefault" data-address-mode="packageDefault">${t("characters.packageDefault")}</label>
            <label><input type="radio" name="rp-address-mode" value="custom" data-address-mode="custom">${t("characters.custom")}</label>
          </fieldset>
          <label class="rp-field-label">${t("characters.customAddress")}<input type="text" data-custom-address maxlength="256" autocomplete="off"></label>
          <p class="rp-inline-status" data-address-status></p>
          <button class="rp-button rp-primary" type="button" data-save-address>${t("characters.saveAddress")}</button>
        </section>
      </div>
      <div class="rp-section-panel" data-section-panel="packageInfo" hidden>
        <section class="rp-card">
          <div class="rp-card-heading"><div><h3>${t("characters.info")}</h3><p data-package-summary></p></div></div>
          <p class="rp-compatibility" data-compatibility></p>
          <dl class="rp-info-list"><div><dt>${t("characters.id")}</dt><dd data-package-id></dd></div><div><dt>${t("characters.version")}</dt><dd data-info-version></dd></div><div><dt>${t("characters.source")}</dt><dd data-info-source></dd></div></dl>
          <p class="rp-availability" data-availability></p>
          <button class="rp-button" type="button" data-open-package-folder>${t("characters.folder")}</button>
        </section>
        <p class="rp-notice" data-migration-notice hidden></p>
        <section class="rp-card"><div class="rp-card-heading"><div><h3>${t("characters.settings")}</h3><p>${t("characters.settingsHint")}</p></div></div><div class="rp-settings-list" data-package-settings></div><p class="rp-empty" data-package-settings-empty hidden>${t("characters.noSettings")}</p><p class="rp-inline-status" data-package-settings-status></p><button class="rp-button" type="button" data-save-package-settings>${t("characters.saveSettings")}</button></section>
      </div>
    </section>
  </div>`;

function record(value) { return value !== null && typeof value === "object" && !Array.isArray(value); }
function safePng(value) {
  return typeof value === "string" && value.length <= MAX_PREVIEW_LENGTH
    && /^data:image\/png;base64,[A-Za-z0-9+/]+={0,2}$/.test(value);
}
function text(node, value) { node.textContent = typeof value === "string" ? value : ""; }
function make(tag, className, content) {
  const node = document.createElement(tag);
  if (className) node.className = className;
  if (content !== undefined) text(node, content);
  return node;
}
function unwrap(value) { return record(value) && record(value.snapshot) ? value.snapshot : value; }
function cleanSnapshot(value) {
  const candidate = unwrap(value);
  if (!record(candidate) || !["catalog", "detail"].includes(candidate.route)
      || typeof candidate.isBusy !== "boolean" || !(candidate.diagnostic === null || record(candidate.diagnostic))) return null;
  if (candidate.route === "catalog") {
    if (!record(candidate.catalog) || !Array.isArray(candidate.catalog.packages)
        || typeof candidate.catalog.searchText !== "string" || !(candidate.catalog.selectedArchiveName === null || typeof candidate.catalog.selectedArchiveName === "string")) return null;
  } else if (!(candidate.detail === null || record(candidate.detail))) return null;
  return candidate;
}
function waitForStylesheet(link, signal) {
  return new Promise(resolve => {
    let settled = false;
    const finish = loaded => {
      if (settled) return;
      settled = true;
      window.clearTimeout(timer);
      link.removeEventListener("load", onLoad);
      link.removeEventListener("error", onError);
      signal?.removeEventListener("abort", onAbort);
      resolve(loaded);
    };
    const onLoad = () => finish(true);
    const onError = () => finish(false);
    const onAbort = () => finish(false);
    const timer = window.setTimeout(() => finish(Boolean(link.sheet)), 3000);
    link.addEventListener("load", onLoad, { once: true });
    link.addEventListener("error", onError, { once: true });
    signal?.addEventListener("abort", onAbort, { once: true });
    if (signal?.aborted) finish(false);
    else document.head.append(link);
    if (link.sheet) finish(true);
  });
}

export async function mount(container, context) {
  const signal = context?.signal;
  const stylesheet = document.createElement("link");
  stylesheet.rel = "stylesheet";
  stylesheet.href = new URL("./role-packages.css", import.meta.url).href;
  stylesheet.dataset.settingsOwner = "role-packages";
  container.innerHTML = MARKUP;

  let disposed = false;
  let snapshot = null;
  let busyCount = 0;
  let localError = "";
  let readFailure = false;
  let lastCommand = null;
  let searchTimer = 0;
  let addressTimer = 0;
  const settingTimers = new Map();
  const draftSettings = new Map();
  const settingInFlight = new Map();
  let addressDraft = null;
  let addressInFlight = null;
  let searchDraft = "";
  let searchDirty = false;
  let searchInFlight = null;
  let routeTransition = false;
  let requestTail = Promise.resolve();
  const root = container.querySelector("[data-role-packages-page]");
  const search = container.querySelector("[data-catalog-search]");
  const busy = container.querySelector("[data-busy]");
  const error = container.querySelector("[data-error]");
  const retry = container.querySelector("[data-retry]");
  const status = container.querySelector("[data-status]");
  const diagnostic = container.querySelector("[data-diagnostic]");
  const catalog = container.querySelector('[data-route="catalog"]');
  const detailRoute = container.querySelector('[data-route="detail"]');
  const packageList = container.querySelector("[data-package-list]");

  function currentDetail() { return snapshot?.route === "detail" ? snapshot.detail : null; }
  function setBusy() {
    busy.hidden = busyCount === 0;
    root.dataset.state = busyCount > 0 ? "busy" : readFailure ? "read-error" : snapshot ? "ready" : "loading";
    for (const button of container.querySelectorAll("button")) {
      button.disabled = busyCount > 0 || (readFailure && !button.matches("[data-retry]")
        || !button.matches("[data-retry]") && button.dataset.businessDisabled === "true");
    }
    search.disabled = readFailure || routeTransition;
    const detail = currentDetail();
    const canEditDetail = Boolean(detail?.isAvailable) && !readFailure;
    const addressMode = addressDraft?.mode || detail?.address?.mode;
    for (const radio of container.querySelectorAll("[data-address-mode]")) radio.disabled = !canEditDetail;
    for (const input of container.querySelectorAll("[data-custom-address]")) input.disabled = !canEditDetail || addressMode !== "custom";
    for (const input of container.querySelectorAll("[data-setting-text], [data-setting-toggle], [data-setting-choice]")) input.disabled = !canEditDetail;
    for (const input of container.querySelectorAll("[data-appearance-select]")) input.disabled = !canEditDetail;
    retry.disabled = busyCount > 0;
  }
  function businessDisabled(button, value) {
    button.dataset.businessDisabled = value ? "true" : "false";
    button.disabled = busyCount > 0 || readFailure || value;
  }
  function showError(message, isReadFailure = false) {
    localError = message || t("characters.failed");
    readFailure = Boolean(isReadFailure);
    text(error, localError);
    error.hidden = false;
    retry.hidden = false;
    setBusy();
  }
  function clearError() {
    localError = "";
    readFailure = false;
    text(error, "");
    error.hidden = true;
    retry.hidden = true;
  }
  function updateImage(image, value, alt) {
    if (safePng(value)) {
      image.src = value;
      image.alt = alt || t("characters.preview");
      image.hidden = false;
    } else {
      image.removeAttribute("src");
      image.hidden = true;
    }
  }
  function renderCatalog(value) {
    const state = value.catalog;
    if (!searchDirty && document.activeElement !== search) searchDraft = state.searchText;
    if (search.value !== searchDraft) search.value = searchDraft;
    text(container.querySelector("[data-selected-file-name]"), state.selectedArchiveName || t("characters.noFile"));
    businessDisabled(container.querySelector("[data-install-package]"), !state.selectedArchiveName);
    text(container.querySelector("[data-scan-status]"), state.scanStatus || "");
    packageList.replaceChildren();
    for (const item of state.packages) {
      if (!record(item) || typeof item.packageId !== "string") continue;
      const card = make("article", "rp-package-card");
      card.dataset.packageCard = "";
      card.dataset.packageId = item.packageId;
      const previewWrap = make("div", "rp-package-art");
      previewWrap.dataset.packagePreview = "";
      const image = make("img", "rp-package-image");
      image.alt = "";
      const fallback = make("span", "rp-package-fallback", t("characters.preview"));
      fallback.hidden = safePng(item.previewDataUrl);
      image.addEventListener("error", () => { fallback.hidden = false; }, { once: true });
      updateImage(image, item.previewDataUrl, "");
      previewWrap.append(image, fallback);
      const content = make("div", "rp-package-copy");
      const heading = make("h3", "rp-package-name", item.displayName || t("characters.unnamed"));
      const badge = make("span", "rp-badge", item.sourceBadge || "");
      const meta = make("p", "rp-package-meta", t("characters.meta", { version: item.packageVersion || t("characters.unknownVersion"), count: Number.isFinite(item.appearanceCount) ? item.appearanceCount : 0 }));
      const state = make("p", item.isActive ? "rp-active-label" : "rp-package-state", item.isActive ? t("characters.active") : item.isEmbedded ? t("characters.embedded") : t("characters.installed"));
      const open = make("button", "rp-button rp-primary", t("characters.details"));
      open.type = "button";
      open.dataset.openPackage = "";
      content.append(heading, badge, meta, state, open);
      card.append(previewWrap, content);
      packageList.append(card);
    }
    container.querySelector("[data-catalog-empty]").hidden = state.packages.length > 0;
    catalog.hidden = value.route !== "catalog";
    detailRoute.hidden = value.route !== "detail";
  }
  function renderAppearances(detail) {
    const list = container.querySelector("[data-appearance-list]");
    list.replaceChildren();
    for (const appearance of detail.appearances || []) {
      if (!record(appearance)) continue;
      const label = make("label", "rp-appearance-choice");
      const radio = document.createElement("input");
      radio.type = "radio";
      radio.name = "rp-appearance";
      radio.dataset.appearanceSelect = "";
      radio.value = appearance.appearanceId || "";
      radio.dataset.appearanceId = appearance.appearanceId || "";
      radio.dataset.packageVersion = appearance.packageVersion || "";
      const selected = detail.selectedAppearance;
      radio.checked = record(selected) && selected.appearanceId === appearance.appearanceId && selected.packageVersion === appearance.packageVersion;
      const copy = make("span", "rp-appearance-copy");
      copy.append(make("strong", "", appearance.display || appearance.appearanceId || t("characters.look")));
      if (appearance.isCurrent) copy.append(make("small", "rp-current-tag", t("characters.selected")));
      label.append(radio, copy);
      list.append(label);
    }
  }
  function renderSettings(detail) {
    const list = container.querySelector("[data-package-settings]");
    if (list.contains(document.activeElement)) return;
    list.replaceChildren();
    for (const setting of detail.settings || []) {
      if (!record(setting) || typeof setting.key !== "string") continue;
      const settingValue = draftSettings.has(setting.key) ? draftSettings.get(setting.key) : setting.value;
      const group = make("label", "rp-setting");
      group.dataset.packageSettingKey = setting.key;
      group.append(make("span", "rp-setting-label", setting.label || setting.key));
      let control;
      if (setting.type === "toggle") {
        control = document.createElement("input");
        control.type = "checkbox";
        control.dataset.settingToggle = "";
        control.checked = settingValue === "true";
      } else if (setting.type === "choice") {
        control = document.createElement("select");
        control.dataset.settingChoice = "";
        for (const option of Array.isArray(setting.options) ? setting.options : []) {
          const item = document.createElement("option");
          item.value = typeof option === "string" ? option : "";
          text(item, typeof option === "string" ? option : "");
          control.append(item);
        }
        control.value = typeof settingValue === "string" ? settingValue : "";
      } else if (setting.type === "text") {
        control = document.createElement("input");
        control.type = "text";
        control.maxLength = 256;
        control.autocomplete = "off";
        control.dataset.settingText = "";
        control.value = typeof settingValue === "string" ? settingValue : "";
      } else continue;
      control.dataset.settingKey = setting.key;
      group.append(control);
      list.append(group);
    }
  }
  function renderDetail(value) {
    const detail = value.detail;
    catalog.hidden = value.route !== "catalog";
    detailRoute.hidden = value.route !== "detail";
    if (!detail) {
      text(container.querySelector("[data-detail-name]"), t("characters.unavailable"));
      text(container.querySelector("[data-detail-version]"), "");
      text(container.querySelector("[data-detail-source]"), "");
      updateImage(container.querySelector("[data-detail-preview]"), null, "");
      container.querySelector("[data-detail-unavailable]").hidden = false;
      for (const tab of container.querySelectorAll("[data-section]")) businessDisabled(tab, true);
      for (const pane of container.querySelectorAll("[data-section-panel]")) pane.hidden = true;
      for (const selector of ["[data-activate-appearance]", "[data-uninstall-package]", "[data-open-detail-folder]", "[data-open-package-folder]", "[data-save-address]", "[data-save-package-settings]"])
        businessDisabled(container.querySelector(selector), true);
      return;
    }
    container.querySelector("[data-detail-unavailable]").hidden = true;
    for (const tab of container.querySelectorAll("[data-section]")) businessDisabled(tab, false);
    text(container.querySelector("[data-detail-name]"), detail.displayName || t("characters.unnamed"));
    text(container.querySelector("[data-detail-version]"), detail.packageVersion ? t("characters.versionValue", { version: detail.packageVersion }) : "");
    text(container.querySelector("[data-detail-source]"), detail.sourceBadge || "");
    updateImage(container.querySelector("[data-detail-preview]"), detail.previewDataUrl, t("characters.previewName", { name: detail.displayName || t("characters.character") }));
    text(container.querySelector("[data-active-status]"), detail.isActive ? t("characters.activePackage") : t("characters.inactivePackage"));
    renderAppearances(detail);
    const selected = record(detail.selectedAppearance) ? detail.selectedAppearance : null;
    businessDisabled(container.querySelector("[data-activate-appearance]"), !selected || !detail.isAvailable);
    const cannotUninstall = Boolean(detail.isEmbedded) || !selected || !detail.isAvailable;
    businessDisabled(container.querySelector("[data-uninstall-package]"), cannotUninstall);
    businessDisabled(container.querySelector("[data-open-detail-folder]"), !detail.isAvailable);
    businessDisabled(container.querySelector("[data-open-package-folder]"), !detail.isAvailable);
    text(container.querySelector("[data-package-summary]"), detail.packageSummary || "");
    text(container.querySelector("[data-compatibility]"), detail.compatibilityText || "");
    text(container.querySelector("[data-section-panel=packageInfo] [data-package-id]"), detail.packageId || "");
    text(container.querySelector("[data-info-version]"), detail.packageVersion || "");
    text(container.querySelector("[data-info-source]"), detail.sourceBadge || "");
    text(container.querySelector("[data-availability]"), detail.isAvailable ? t("characters.available") : detail.availabilityStatus || t("characters.unavailableNow"));
    const availability = container.querySelector("[data-availability]");
    availability.dataset.state = detail.isAvailable ? "available" : "unavailable";
    for (const tab of container.querySelectorAll("[data-section]")) {
      const active = tab.dataset.section === detail.section;
      tab.setAttribute("aria-current", active ? "page" : "false");
      tab.classList.toggle("is-selected", active);
    }
    for (const pane of container.querySelectorAll("[data-section-panel]")) pane.hidden = pane.dataset.sectionPanel !== detail.section;
    text(container.querySelector("[data-address-default]"), t("characters.defaultAddress", { name: detail.address?.defaultText || t("characters.unspecified") }));
    const mode = addressDraft?.mode || (detail.address?.mode === "custom" ? "custom" : "packageDefault");
    for (const radio of container.querySelectorAll("[data-address-mode]")) radio.checked = radio.dataset.addressMode === mode;
    const addressInput = container.querySelector("[data-custom-address]");
    if (document.activeElement !== addressInput && !addressDraft) addressInput.value = detail.address?.customText || "";
    addressInput.disabled = mode !== "custom" || readFailure || !detail.isAvailable;
    text(container.querySelector("[data-address-status]"), detail.addressStatus || "");
    renderSettings(detail);
    const hasSettings = Array.isArray(detail.settings) && detail.settings.length > 0;
    container.querySelector("[data-package-settings-empty]").hidden = hasSettings;
    businessDisabled(container.querySelector("[data-save-package-settings]"), !hasSettings || !detail.isAvailable);
    businessDisabled(container.querySelector("[data-save-address]"), !detail.isAvailable);
    text(container.querySelector("[data-package-settings-status]"), detail.packageSettingsStatus || "");
    const migration = container.querySelector("[data-migration-notice]");
    text(migration, detail.migrationNotice || "");
    migration.hidden = !detail.migrationNotice;
  }
  let renderedRoute = null;
  function render(value = snapshot) {
    if (!value || disposed) return;
    text(status, "");
    status.hidden = true;
    if (record(value.diagnostic)) {
      diagnostic.hidden = false;
      text(container.querySelector("[data-diagnostic-heading]"), value.diagnostic.heading || t("characters.notice"));
      text(container.querySelector("[data-diagnostic-text]"), value.diagnostic.text || "");
    } else diagnostic.hidden = true;
    if (value.route === "catalog") renderCatalog(value);
    else renderDetail(value);
    if (renderedRoute !== value.route) {
      renderedRoute = value.route;
      context.onRouteChanged?.();
    }
    if (!localError) retry.hidden = true;
    setBusy();
  }
  function accept(value) {
    const next = cleanSnapshot(value);
    if (!next) throw new Error("SETTINGS_INVALID_SNAPSHOT");
    snapshot = next;
    render(next);
  }
  async function enqueue(type, payload) {
    if (disposed || signal?.aborted) throw new Error("SETTINGS_CANCELLED");
    busyCount++;
    setBusy();
    const run = async () => {
      if (disposed || signal?.aborted) throw new Error("SETTINGS_CANCELLED");
      const response = await context.request(type, { pageId: PAGE_ID, ...payload });
      if (disposed || signal?.aborted) throw new Error("SETTINGS_CANCELLED");
      const next = cleanSnapshot(response);
      if (!next) throw new Error("SETTINGS_INVALID_SNAPSHOT");
      if (type === "rolePackages.search" && searchDraft === payload.query) searchDirty = false;
      if (type === "rolePackages.editAddress" && addressDraft
          && addressDraft.mode === payload.mode && addressDraft.value === payload.value) addressDraft = null;
      if (type === "rolePackages.setSetting" && draftSettings.get(payload.key) === payload.value)
        draftSettings.delete(payload.key);
      snapshot = next;
      clearError();
      render(next);
      return next;
    };
    const operation = requestTail.then(run, run);
    requestTail = operation.catch(() => {});
    try { return await operation; }
    catch (caught) {
      if (!disposed && !signal?.aborted) showError(t("characters.actionFailed"));
      throw caught;
    } finally {
      busyCount = Math.max(0, busyCount - 1);
      if (!disposed) { setBusy(); render(snapshot); }
    }
  }
  function safeAction(work) {
    void Promise.resolve().then(work).catch(() => {});
  }
  async function flushDrafts() {
    while (!disposed && !signal?.aborted) {
      if (searchTimer) { clearTimeout(searchTimer); searchTimer = 0; }
      if (addressTimer) { clearTimeout(addressTimer); addressTimer = 0; }
      for (const [key, timer] of settingTimers) { clearTimeout(timer); settingTimers.delete(key); }
      const detail = currentDetail();
      const tasks = [];
      if (snapshot?.route === "catalog" && searchDirty && searchDraft !== snapshot.catalog.searchText
          && searchInFlight !== searchDraft) {
        const query = searchDraft;
        searchInFlight = query;
        tasks.push(enqueue("rolePackages.search", { query }).finally(() => {
          if (searchInFlight === query) searchInFlight = null;
        }));
      }
      if (addressDraft && detail && addressInFlight !== addressDraft) tasks.push(queueAddressDraft(detail.packageId, addressDraft));
      if (detail) {
        for (const [key, value] of draftSettings) {
          if (settingInFlight.get(key) !== value) tasks.push(queueSettingDraft(detail.packageId, key, value));
        }
      } else draftSettings.clear();
      if (tasks.length === 0) {
        const pending = searchInFlight !== null || addressInFlight !== null || settingInFlight.size > 0;
        if (!pending) break;
        await requestTail;
        continue;
      }
      await Promise.all(tasks);
    }
    await requestTail;
  }
  async function perform(type, payload = {}) {
    try { return await enqueue(type, payload); }
    catch (caught) {
      if (disposed || signal?.aborted) throw caught;
      let refreshed = false;
      try {
        await enqueue("rolePackages.get");
        refreshed = true;
      } catch { /* Keep the last authoritative snapshot and expose retry. */ }
      showError(refreshed
        ? t("characters.refreshed")
        : t("characters.refreshFailed"), !refreshed);
      throw caught;
    }
  }
  async function action(type, payload = {}) {
    try { await flushDrafts(); }
    catch (caught) {
      if (!disposed && !signal?.aborted) {
        let refreshed = false;
        try { await enqueue("rolePackages.get"); refreshed = true; } catch { /* Retry remains available. */ }
        showError(refreshed ? t("characters.pendingRefreshed") : t("characters.pendingFailed"), !refreshed);
      }
      throw caught;
    }
    const isRouteTransition = type === "rolePackages.open" || type === "rolePackages.back";
    if (isRouteTransition) {
      if (searchTimer) { clearTimeout(searchTimer); searchTimer = 0; }
      routeTransition = true;
      setBusy();
    }
    try { return await perform(type, payload); }
    finally {
      if (isRouteTransition) {
        routeTransition = false;
        setBusy();
      }
    }
  }
  async function queueAddressDraft(packageId, draft) {
    addressInFlight = draft;
    try { return await perform("rolePackages.editAddress", { packageId, ...draft }); }
    finally { if (addressInFlight === draft) addressInFlight = null; }
  }
  async function queueSettingDraft(packageId, key, value) {
    settingInFlight.set(key, value);
    try { return await perform("rolePackages.setSetting", { packageId, key, value }); }
    finally { if (settingInFlight.get(key) === value) settingInFlight.delete(key); }
  }
  async function queueSearchDraft(query) {
    searchInFlight = query;
    try { return await perform("rolePackages.search", { query }); }
    finally { if (searchInFlight === query) searchInFlight = null; }
  }
  function scheduleSearch() {
    if (routeTransition || readFailure) return;
    searchDraft = search.value;
    searchDirty = true;
    if (searchTimer) clearTimeout(searchTimer);
    searchTimer = window.setTimeout(() => {
      searchTimer = 0;
      if (!routeTransition && !readFailure) safeAction(() => queueSearchDraft(searchDraft));
    }, 120);
  }
  function scheduleAddress(value) {
    addressDraft = value;
    if (addressTimer) clearTimeout(addressTimer);
    addressTimer = window.setTimeout(() => {
      addressTimer = 0;
      const draft = addressDraft;
      if (currentDetail()) safeAction(() => queueAddressDraft(currentDetail().packageId, draft));
    }, 180);
  }
  function scheduleSetting(key, value) {
    draftSettings.set(key, value);
    if (settingTimers.has(key)) clearTimeout(settingTimers.get(key));
    settingTimers.set(key, window.setTimeout(() => {
      settingTimers.delete(key);
      const latest = draftSettings.get(key);
      if (currentDetail()) safeAction(() => queueSettingDraft(currentDetail().packageId, key, latest));
    }, 180));
  }
  function selectedAppearancePayload(detail = currentDetail()) {
    if (!detail || !record(detail.selectedAppearance)) return null;
    return { packageId: detail.packageId, appearanceId: detail.selectedAppearance.appearanceId, packageVersion: detail.selectedAppearance.packageVersion };
  }

  container.addEventListener("input", event => {
    if (event.target === search) { if (!readFailure && !routeTransition) scheduleSearch(); }
    else if (event.target.matches?.("[data-custom-address]")) {
      if (!readFailure && currentDetail()?.isAvailable) scheduleAddress({ mode: "custom", value: event.target.value });
    } else if (event.target.matches?.("[data-setting-text]")) {
      if (!readFailure && currentDetail()?.isAvailable) scheduleSetting(event.target.dataset.settingKey, event.target.value);
    }
  });
  container.addEventListener("change", event => {
    if (readFailure || !currentDetail()?.isAvailable) return;
    const target = event.target;
    if (target.matches?.("[data-address-mode]")) {
      const mode = target.dataset.addressMode;
      const input = container.querySelector("[data-custom-address]");
      input.disabled = mode !== "custom" || busyCount > 0;
      scheduleAddress({ mode, value: mode === "custom" ? input.value : "" });
    } else if (target.matches?.("[data-setting-toggle]")) {
      scheduleSetting(target.dataset.settingKey, target.checked ? "true" : "false");
    } else if (target.matches?.("[data-setting-choice]")) {
      scheduleSetting(target.dataset.settingKey, target.value);
    } else if (target.matches?.("[data-appearance-select]")) {
      const detail = currentDetail();
      if (detail) safeAction(() => action("rolePackages.selectAppearance", {
        packageId: detail.packageId, appearanceId: target.dataset.appearanceId, packageVersion: target.dataset.packageVersion,
      }));
    }
  });
  container.addEventListener("click", event => {
    const target = event.target.closest?.("button");
    if (!target || target.disabled || busyCount > 0) return;
    if (target.matches("[data-open-package]")) {
      const card = target.closest("[data-package-card]");
      if (card) safeAction(() => action("rolePackages.open", { packageId: card.dataset.packageId }));
    } else if (target.matches("[data-select-file]")) safeAction(() => action("rolePackages.chooseFile"));
    else if (target.matches("[data-install-package]")) safeAction(() => action("rolePackages.install"));
    else if (target.matches("[data-rescan]")) safeAction(() => action("rolePackages.scan"));
    else if (target.matches("[data-back-to-catalog]")) safeAction(() => action("rolePackages.back"));
    else if (target.matches("[data-back-to-personalization]")) safeAction(async () => { await flushDrafts(); await context.navigate("Personalization"); });
    else if (target.matches("[data-section]")) safeAction(() => action("rolePackages.section", { section: target.dataset.section }));
    else if (target.matches("[data-activate-appearance]")) {
      const selected = selectedAppearancePayload();
      if (selected) safeAction(() => action("rolePackages.activate", selected));
    } else if (target.matches("[data-open-detail-folder]")) {
      const detail = currentDetail();
      if (detail) safeAction(() => action("rolePackages.openFolder", { packageId: detail.packageId }));
    } else if (target.matches("[data-open-package-folder]")) {
      const detail = currentDetail();
      if (detail) safeAction(() => action("rolePackages.openFolder", { packageId: detail.packageId }));
    } else if (target.matches("[data-uninstall-package]")) {
      const selected = selectedAppearancePayload();
      if (selected && window.confirm(t("characters.uninstallPrompt", { name: currentDetail()?.displayName || t("characters.thisPackage"), version: selected.packageVersion })))
        safeAction(() => action("rolePackages.uninstall", selected));
    } else if (target.matches("[data-save-address]")) {
      const detail = currentDetail();
      if (detail) safeAction(() => action("rolePackages.saveAddress", { packageId: detail.packageId }));
    } else if (target.matches("[data-save-package-settings]")) {
      const detail = currentDetail();
      if (detail) safeAction(() => action("rolePackages.saveSettings", { packageId: detail.packageId }));
    } else if (target.matches("[data-retry]")) safeAction(async () => {
      clearError();
      try {
        const value = await enqueue("rolePackages.get");
        accept(value);
      } catch (caught) {
        showError(t("characters.stateFailed"), true);
        throw caught;
      }
    });
  });

  const disposeTheme = typeof context?.onThemeChanged === "function" ? context.onThemeChanged(() => {
    // Theme variables are read from the shared root; the stylesheet itself is theme agnostic.
    if (disposed) return;
    root.dataset.themeRevision = String((Number(root.dataset.themeRevision) || 0) + 1);
  }) : null;
  const dispose = () => {
    if (disposed) return;
    disposed = true;
    if (searchTimer) clearTimeout(searchTimer);
    if (addressTimer) clearTimeout(addressTimer);
    for (const timer of settingTimers.values()) clearTimeout(timer);
    settingTimers.clear();
    if (typeof disposeTheme === "function") disposeTheme();
    else if (disposeTheme && typeof disposeTheme.dispose === "function") disposeTheme.dispose();
    signal?.removeEventListener("abort", dispose);
    stylesheet.remove();
  };
  dispose.goBack = async () => {
    if (disposed || signal?.aborted || snapshot?.route !== "detail") return false;
    await action("rolePackages.back");
    return true;
  };
  dispose.openItem = async packageId => {
    if (disposed || signal?.aborted) return;
    if (typeof packageId === "string" && currentDetail()?.packageId === packageId) return;
    if (packageId === null && snapshot?.route === "catalog") return;
    if (typeof packageId === "string") await action("rolePackages.open", { packageId });
    else if (packageId === null) await action("rolePackages.back");
  };
  signal?.addEventListener("abort", dispose, { once: true });
  if (signal?.aborted) dispose();
  const styleLoaded = await waitForStylesheet(stylesheet, signal);
  if (disposed) return dispose;
  if (!styleLoaded) showError(t("characters.styleFailed"));
  try {
    accept(await enqueue("rolePackages.get"));
    if (typeof context.itemId === "string") await dispose.openItem(context.itemId);
  } catch {
    if (!disposed) showError(t("characters.catalogFailed"), true);
  }
  return dispose;
}
