(() => {
  "use strict";

  const t = window.MarginSettingsI18n.t;
  const bridge = window.chrome && window.chrome.webview;
  const navigation = document.getElementById("settings-navigation");
  const back = document.getElementById("settings-back");
  const parent = document.getElementById("settings-parent");
  const title = document.getElementById("page-title");
  const description = document.getElementById("page-description");
  const status = document.getElementById("root-status");
  const pageContent = document.getElementById("page-content");
  const pending = new Map();
  const sections = ["companion", "services", "app", "privacy"];
  const sectionPages = {
    companion: ["Personalization", "RolePackages"],
    services: ["ModelConnection", "Speech", "Qq"],
    app: ["UserProfile", "Theme"],
    privacy: ["AgentConnection", "ConversationMemory", "Privacy"],
  };
  let selectedSection = "companion";
  let overview = false;
  const pageStates = new Map();
  const themeChangedListeners = new Set();
  const modulePathPattern = /^pages\/[A-Za-z0-9_-]+(?:\/[A-Za-z0-9_-]+)*\.js$/;
  let serial = 0;
  let pages = [];
  let requestedHostPageId = null;
  let requestedHostItemId;
  let activePage = null;
  let activeCleanup = null;
  let activeMountController = null;
  let routeGeneration = 0;
  let closed = false;

  function setStatus(text, isError = false) {
    status.textContent = text;
    status.classList.toggle("is-error", isError);
  }

  function request(type, payload = {}, ownerPageId = null) {
    if (!bridge || closed) return Promise.reject(new Error("SETTINGS_UNAVAILABLE"));
    const requestId = `settings_${++serial}`;
    return new Promise((resolve, reject) => {
      const timeout = window.setTimeout(() => {
        pending.delete(requestId);
        reject(new Error("SETTINGS_TIMEOUT"));
      }, 10000);
      pending.set(requestId, { resolve, reject, timeout, ownerPageId });
      try {
        bridge.postMessage({ type, requestId, payload });
      } catch {
        window.clearTimeout(timeout);
        pending.delete(requestId);
        reject(new Error("SETTINGS_UNAVAILABLE"));
      }
    });
  }

  function discardPageRequests(pageId) {
    for (const [requestId, waiter] of pending) {
      if (waiter.ownerPageId !== pageId) continue;
      window.clearTimeout(waiter.timeout);
      pending.delete(requestId);
      waiter.reject(new Error("SETTINGS_PAGE_CHANGED"));
    }
  }

  function cleanupActivePage() {
    if (activePage && activeCleanup && typeof activeCleanup.getState === "function") {
      try {
        const state = activeCleanup.getState();
        if (state !== undefined) pageStates.set(activePage.id, state);
      } catch { /* A page state snapshot is optional and opaque to the root. */ }
    }
    if (activePage) discardPageRequests(activePage.id);
    if (activeMountController) activeMountController.abort();
    activeMountController = null;
    const cleanup = activeCleanup;
    activeCleanup = null;
    activePage = null;
    if (typeof cleanup === "function") {
      try { cleanup(); } catch { /* Page cleanup must not block navigation. */ }
    } else if (cleanup && typeof cleanup.dispose === "function") {
      try { cleanup.dispose(); } catch { /* Page cleanup must not block navigation. */ }
    }
    pageContent.replaceChildren();
  }

  function sectionFor(page) {
    return sections.find(section => sectionPages[section].includes(page.id)) || "app";
  }

  function pageTitle(page) {
    const key = `settings.page.${page.id}`;
    return t(key) !== key ? t(key) : (page.title || t("settings.untitled"));
  }

  function renderNavigation() {
    const fragment = document.createDocumentFragment();
    for (const section of sections) {
      const item = document.createElement("button");
      item.type = "button";
      item.className = "section-button";
      item.dataset.section = section;
      if (section === selectedSection) item.setAttribute("aria-current", "page");
      const name = document.createElement("span");
      name.className = "section-name";
      name.textContent = t(`settings.${section}`);
      const subtitle = document.createElement("span");
      subtitle.className = "section-subtitle";
      subtitle.textContent = t(`settings.${section}Sub`);
      item.append(name, subtitle);
      item.addEventListener("click", () => { void openSection(section); });
      fragment.append(item);
    }
    navigation.replaceChildren(fragment);
  }

  function detailRow(label, pageId, hint = "") {
    const page = pages.find(candidate => candidate.id === pageId);
    const button = document.createElement("button");
    button.type = "button";
    button.className = "detail-row";
    button.dataset.pageId = pageId;
    button.disabled = !availablePage(pageId);
    const copy = document.createElement("span");
    copy.className = "row-copy";
    const name = document.createElement("span");
    name.className = "row-label";
    name.textContent = label;
    copy.append(name);
    if (hint || button.disabled) {
      const detail = document.createElement("span");
      detail.className = "row-description";
      detail.textContent = button.disabled && hint === t("settings.checkService")
        ? t("settings.unavailable") : (hint || t("settings.unavailable"));
      copy.append(detail);
    }
    const arrow = document.createElement("span");
    arrow.className = "row-chevron";
    arrow.setAttribute("aria-hidden", "true");
    arrow.textContent = "›";
    button.append(copy, arrow);
    if (page) button.addEventListener("click", () => { void navigateTo(page); });
    return button;
  }

  async function openSection(section) {
    if (!sections.includes(section) || closed) return;
    selectedSection = section;
    if (section === "companion" && availablePage("Personalization")) {
      await navigateTo(availablePage("Personalization"), undefined, true);
      return;
    }
    routeGeneration++;
    cleanupActivePage();
    overview = true;
    document.body.dataset.settingsSection = section;
    document.body.dataset.settingsOverview = section;
    delete document.body.dataset.pageId;
    title.textContent = t(`settings.${section}`);
    description.textContent = t(`settings.${section}Lead`);
    back.hidden = true;
    setStatus("");
    renderNavigation();
    const rows = document.createElement("div");
    rows.className = "overview-rows";
    if (section === "privacy") {
      rows.append(detailRow(t("settings.scope"), "AgentConnection", t("settings.scopeUnavailable")),
        detailRow(t("settings.permissions"), "ActionPermissions", t("settings.permissionsUnavailable")),
        detailRow(t("settings.page.ConversationMemory"), "ConversationMemory"));
      const more = document.createElement("details");
      more.className = "overview-more";
      const summary = document.createElement("summary");
      summary.textContent = t("settings.more");
      more.append(summary, detailRow(t("settings.page.Privacy"), "Privacy"));
      rows.append(more);
    } else {
      const included = sectionPages[section];
      for (const pageId of included) rows.append(detailRow(t(`settings.page.${pageId}`), pageId,
        section === "services" ? t("settings.checkService") : ""));
      if (section === "app") {
        const additional = pages.filter(page => !sections.some(group => sectionPages[group].includes(page.id)));
        if (additional.length) {
          const more = document.createElement("details");
          more.className = "overview-more";
          const summary = document.createElement("summary");
          summary.textContent = t("settings.more");
          more.append(summary);
          for (const page of additional) more.append(detailRow(pageTitle(page), page.id));
          rows.append(more);
        }
      }
    }
    pageContent.append(rows);
    if (section === "services") {
      const note = document.createElement("p");
      note.className = "offline-note";
      note.textContent = t("settings.offline");
      pageContent.append(note);
    }
  }
  function availablePage(pageId) {
    return pages.find(page => page.id === pageId && page.available === true
      && typeof page.module === "string" && modulePathPattern.test(page.module)) || null;
  }

  async function navigateTo(page, itemId, asOverview = false) {
    if (!page || page.available !== true || typeof page.module !== "string" || !modulePathPattern.test(page.module)) return;
    if (activePage && activePage.id === page.id && overview === asOverview) {
      if (itemId !== undefined && typeof activeCleanup?.openItem === "function") {
        const owner = activeCleanup;
        const generation = routeGeneration;
        try {
          await owner.openItem(itemId);
          if (!closed && generation === routeGeneration && owner === activeCleanup) setStatus("");
        } catch {
          if (!closed && generation === routeGeneration && owner === activeCleanup)
            setStatus(t("settings.itemUnavailable"), true);
        }
      }
      return;
    }

    const restoreNavigationFocus = navigation.contains(document.activeElement);
    const generation = ++routeGeneration;
    cleanupActivePage();
    overview = asOverview;
    selectedSection = sectionFor(page);
    document.body.dataset.settingsSection = selectedSection;
    document.body.dataset.settingsOverview = asOverview ? selectedSection : "";
    document.body.dataset.pageId = page.id;
    back.hidden = asOverview;
    parent.textContent = t(`settings.${selectedSection}`);
    title.textContent = asOverview ? t(`settings.${selectedSection}`) : pageTitle(page);
    description.textContent = asOverview ? t(`settings.${selectedSection}Lead`) : "";
    setStatus(t("settings.opening"));
    renderNavigation();

    try {
      const route = await request("settings.navigate", { pageId: page.id });
      if (closed || generation !== routeGeneration) return;
      if (!route || route.pageId !== page.id || typeof route.module !== "string" || !modulePathPattern.test(route.module))
        throw new Error("SETTINGS_PAGE_UNAVAILABLE");

      const moduleUrl = new URL(route.module, import.meta.url);
      const pageModule = await import(moduleUrl.href);
      if (closed || generation !== routeGeneration) return;
      if (typeof pageModule.mount !== "function") throw new Error("SETTINGS_MODULE_INVALID");

      activePage = page;
      const controller = new AbortController();
      activeMountController = controller;
      const stagingContent = document.createElement("div");
      const cleanup = await pageModule.mount(stagingContent, {
        pageId: page.id,
        overview: asOverview,
        itemId,
        state: pageStates.get(page.id),
        signal: controller.signal,
        request: (type, payload = {}) => request(type, { ...payload, pageId: page.id }, page.id),
        navigate: async pageId => {
          const destination = availablePage(pageId);
          if (!destination) return false;
          await navigateTo(destination);
          return activePage?.id === pageId;
        },
        canNavigate: pageId => availablePage(pageId) !== null,
        onRouteChanged: () => {
          if (!closed && generation === routeGeneration && !controller.signal.aborted)
            pageContent.closest(".settings-main").scrollTop = 0;
        },
        onThemeChanged: callback => {
          if (typeof callback !== "function" || closed || controller.signal.aborted) return () => {};
          themeChangedListeners.add(callback);
          const unsubscribe = () => {
            themeChangedListeners.delete(callback);
            controller.signal.removeEventListener("abort", unsubscribe);
          };
          controller.signal.addEventListener("abort", unsubscribe, { once: true });
          return unsubscribe;
        },
      });
      if (closed || generation !== routeGeneration) {
        if (typeof cleanup === "function") cleanup();
        else if (cleanup && typeof cleanup.dispose === "function") cleanup.dispose();
        return;
      }
      activeCleanup = cleanup;
      pageContent.replaceChildren(stagingContent);
      setStatus("");
      renderNavigation();
      if (restoreNavigationFocus)
        navigation.querySelector(`[data-section="${selectedSection}"]`)?.focus({ preventScroll: true });
    } catch (error) {
      if (closed || generation !== routeGeneration) return;
      cleanupActivePage();
      setStatus(error && error.message === "SETTINGS_PAGE_UNAVAILABLE"
        ? t("settings.pageUnavailable")
        : t("settings.openFailed"), true);
      renderNavigation();
      if (restoreNavigationFocus)
        navigation.querySelector(`[data-section="${selectedSection}"]`)?.focus({ preventScroll: true });
    }
  }

  function applyTheme(variables, version) {
    if (variables && typeof variables === "object")
      for (const [key, value] of Object.entries(variables)) {
        if (key.startsWith("--") && typeof value === "string")
          document.documentElement.style.setProperty(key, value);
      }
    if (bridge && Number.isFinite(version)) bridge.postMessage({ type: "theme.ack", version });
  }

  if (!bridge) {
    navigation.textContent = t("settings.openInApp");
    setStatus(t("settings.hostUnavailable"), true);

    return;
  }

  bridge.addEventListener("message", event => {
    const response = event.data;
    if (!response || typeof response.type !== "string") return;
    if (response.type === "settings.openPage" && typeof response.pageId === "string") {
      requestedHostPageId = response.pageId;
      requestedHostItemId = typeof response.itemId === "string" && response.itemId.length <= 256
        ? response.itemId : null;
      const destination = availablePage(requestedHostPageId);
      if (destination) void navigateTo(destination, requestedHostItemId,
        destination.id === "Personalization" && !requestedHostItemId);
    } else if (response.type === "command.result") {
      const waiter = pending.get(response.requestId);
      if (!waiter) return;
      pending.delete(response.requestId);
      window.clearTimeout(waiter.timeout);
      if (response.success) waiter.resolve(response.payload);
      else {
        const error = new Error(response.errorCode || "SETTINGS_UNAVAILABLE");
        error.details = response.payload;
        waiter.reject(error);
      }
    } else if (response.type === "theme.changed") {
      applyTheme(response.variables, response.version);
      for (const listener of themeChangedListeners) {
        try { listener(response); } catch { /* Page listeners are isolated from the root message bridge. */ }
      }
    }
  });

  back.addEventListener("click", async () => {
    try {
      if (typeof activeCleanup?.goBack === "function" && await activeCleanup.goBack()) return;
    } catch { return; /* The module retains its route and presents the owner error. */ }
    await openSection(selectedSection);
    if (!closed) navigation.querySelector(`[data-section="${selectedSection}"]`)?.focus({ preventScroll: true });
  });
  window.addEventListener("pagehide", () => {
    closed = true;
    routeGeneration++;
    cleanupActivePage();
    themeChangedListeners.clear();
    for (const waiter of pending.values()) {
      window.clearTimeout(waiter.timeout);
      waiter.reject(new Error("WEB_SURFACE_CLOSED"));
    }
    pending.clear();
  }, { once: true });

  bridge.postMessage({ type: "ready" });
  request("settings.getCatalog").then(catalog => {
    if (closed) return;
    pages = Array.isArray(catalog && catalog.pages) ? catalog.pages.filter(page => page && typeof page === "object") : [];
    renderNavigation();
    const initial = availablePage(requestedHostPageId)
      || pages.find(page => page.id === catalog.selectedPageId && page.available === true)
      || pages.filter(page => page.available === true && typeof page.module === "string" && modulePathPattern.test(page.module))
        .sort((left, right) => (Number(left.order) || 0) - (Number(right.order) || 0)
          || String(left.title || "").localeCompare(String(right.title || ""), "zh-CN"))[0];
    if (initial) void navigateTo(initial, requestedHostItemId, initial.id === "Personalization" && !requestedHostItemId);
    else setStatus(t("settings.noPages"), true);
  }).catch(() => {
    if (closed) return;
    navigation.replaceChildren();
    setStatus(t("settings.catalogFailed"), true);
  });
})();
