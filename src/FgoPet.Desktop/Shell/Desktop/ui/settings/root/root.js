(() => {
  "use strict";

  const t = window.MarginSettingsI18n.t;
  const bridge = window.chrome && window.chrome.webview;
  const navigation = document.getElementById("settings-navigation");
  const mobileNavigation = document.getElementById("settings-mobile-page");
  const scrollRegion = document.getElementById("settings-scroll");
  const back = document.getElementById("settings-back");
  const parent = document.getElementById("settings-parent");
  const title = document.getElementById("page-title");
  const description = document.getElementById("page-description");
  const status = document.getElementById("root-status");
  const pageContent = document.getElementById("page-content");
  const pending = new Map();
  const sections = ["companion", "services", "knowledge", "app"];
  const sectionPages = {
    companion: ["Personalization", "RolePackages"],
    services: ["ModelConnection", "Speech", "Qq", "WebAccess", "AgentConnection"],
    knowledge: ["ConversationMemory", "Understanding"],
    app: ["UserProfile", "Theme", "Privacy"],
  };
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

  function pageDescription(page) {
    const key = `settings.description.${page.id}`;
    return t(key) !== key ? t(key) : (page.description || "");
  }

  function orderedSectionPages(section) {
    const known = sectionPages[section].map(id => pages.find(page => page.id === id)).filter(Boolean);
    if (section !== "app") return known;
    return known.concat(pages.filter(page => !sections.some(group => sectionPages[group].includes(page.id))));
  }

  function navigationGlyph(pageId) {
    const paths = {
      Personalization: "M8 20v-2a4 4 0 0 1 8 0v2M12 12a4 4 0 1 0 0-8 4 4 0 0 0 0 8Z",
      RolePackages: "M4 20v-2a4 4 0 0 1 8 0v2M8 12a4 4 0 1 0 0-8 4 4 0 0 0 0 8ZM16 4a4 4 0 0 1 0 8m0 4a4 4 0 0 1 4 4",
      ModelConnection: "M8 8h8v8H8ZM9 3v3m6-3v3M9 18v3m6-3v3M3 9h3m-3 6h3m12-6h3m-3 6h3",
      Speech: "M5 10H2v4h3l5 4V6l-5 4Zm9-2a6 6 0 0 1 0 8m3-11a10 10 0 0 1 0 14",
      Qq: "M20 11a8 8 0 0 1-8 8H4l1.5-3A8 8 0 1 1 20 11Z",
      WebAccess: "M21 12a9 9 0 1 1-18 0 9 9 0 0 1 18 0ZM3 12h18M12 3a19 19 0 0 1 0 18 19 19 0 0 1 0-18Z",
      ConversationMemory: "M4 4h7a3 3 0 0 1 3 3v14a4 4 0 0 0-4-2H4V4Zm10 3a3 3 0 0 1 3-3h3v15h-3a4 4 0 0 0-3 2",
      Understanding: "M12 3v18M9 4a4 4 0 0 0-5 5 4 4 0 0 0 0 6 4 4 0 0 0 5 5M15 4a4 4 0 0 1 5 5 4 4 0 0 1 0 6 4 4 0 0 1-5 5M8 9h4m0 6h4",
      UserProfile: "M4 21v-2a6 6 0 0 1 12 0v2M10 12a4 4 0 1 0 0-8 4 4 0 0 0 0 8Z",
      Theme: "M21 13a9 9 0 0 1-10-10 9 9 0 1 0 10 10Z",
      Privacy: "M12 3 4 6v6c0 5 8 9 8 9s8-4 8-9V6l-8-3ZM9 12l2 2 4-4",
      AgentConnection: "M8 11V7a4 4 0 0 1 8 0v4M5 11h14v10H5Z",
    };
    const icon = document.createElement("span");
    icon.className = "navigation-glyph";
    icon.setAttribute("aria-hidden", "true");
    const svg = document.createElementNS("http://www.w3.org/2000/svg", "svg");
    svg.setAttribute("viewBox", "0 0 24 24");
    const path = document.createElementNS("http://www.w3.org/2000/svg", "path");
    path.setAttribute("d", paths[pageId] || "M5 5h14v14H5ZM9 9h6m-6 6h6");
    svg.append(path);
    icon.append(svg);
    return icon;
  }

  function renderNavigation() {
    const fragment = document.createDocumentFragment();
    const mobile = document.createDocumentFragment();
    for (const section of sections) {
      const entries = orderedSectionPages(section);
      if (!entries.length) continue;
      const group = document.createElement("div");
      group.className = "navigation-group";
      group.dataset.section = section;
      const heading = document.createElement("h2");
      heading.className = "section-name";
      heading.textContent = t(`settings.${section}`);
      group.append(heading);
      const options = document.createElement("optgroup");
      options.label = heading.textContent;
      for (const page of entries) {
        const item = document.createElement("button");
        item.type = "button";
        item.className = "navigation-page";
        item.dataset.pageId = page.id;
        item.disabled = !availablePage(page.id);
        if (page.id === document.body.dataset.pageId) item.setAttribute("aria-current", "page");
        const name = document.createElement("span");
        name.className = "navigation-label";
        name.textContent = pageTitle(page);
        item.append(navigationGlyph(page.id), name);
        if (item.disabled) {
          const state = document.createElement("span");
          state.className = "navigation-state";
          state.textContent = t("settings.unavailable");
          item.append(state);
        } else if (pageStates.get(page.id)?.dirty === true) {
          const draft = document.createElement("span");
          draft.className = "navigation-draft";
          draft.title = t("settings.unsavedDraft");
          draft.setAttribute("aria-label", draft.title);
          item.append(draft);
        }
        item.addEventListener("click", () => { void navigateTo(page); });
        group.append(item);
        const option = document.createElement("option");
        option.value = page.id;
        option.textContent = pageTitle(page) + (item.disabled ? ` · ${t("settings.unavailable")}` : "");
        option.disabled = item.disabled;
        options.append(option);
      }
      fragment.append(group);
      mobile.append(options);
    }
    navigation.replaceChildren(fragment);
    mobileNavigation.replaceChildren(mobile);
    mobileNavigation.value = document.body.dataset.pageId || "";
  }

  function availablePage(pageId) {
    return pages.find(page => page.id === pageId && page.available === true
      && typeof page.module === "string" && modulePathPattern.test(page.module)) || null;
  }

  function restoreNavigationFocus() {
    if (getComputedStyle(mobileNavigation.parentElement).display !== "none") mobileNavigation.focus({ preventScroll: true });
    else navigation.querySelector(`[data-page-id="${document.body.dataset.pageId}"]`)?.focus({ preventScroll: true });
  }

  async function navigateTo(page, itemId) {
    if (!page || page.available !== true || typeof page.module !== "string" || !modulePathPattern.test(page.module)) return;
    if (activePage && activePage.id === page.id) {
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

    const navigationHadFocus = navigation.contains(document.activeElement) || document.activeElement === mobileNavigation;
    const generation = ++routeGeneration;
    cleanupActivePage();
    const selectedSection = sectionFor(page);
    document.body.dataset.settingsSection = selectedSection;
    document.body.dataset.pageId = page.id;
    back.hidden = true;
    parent.textContent = t(`settings.${selectedSection}`);
    title.textContent = pageTitle(page);
    description.textContent = pageDescription(page);
    scrollRegion.scrollTop = 0;
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
        overview: false,
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
        onRouteChanged: route => {
          if (!closed && generation === routeGeneration && !controller.signal.aborted) {
            scrollRegion.scrollTop = 0;
            if (route && typeof route.canGoBack === "boolean") back.hidden = !route.canGoBack;
          }
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
      if (navigationHadFocus) restoreNavigationFocus();
    } catch (error) {
      if (closed || generation !== routeGeneration) return;
      cleanupActivePage();
      setStatus(error && error.message === "SETTINGS_PAGE_UNAVAILABLE"
        ? t("settings.pageUnavailable")
        : t("settings.openFailed"), true);
      renderNavigation();
      if (navigationHadFocus) restoreNavigationFocus();
    }
  }

  function applyTheme(variables, version) {
    if (variables && typeof variables === "object")
      for (const [key, value] of Object.entries(variables)) {
        if (key.startsWith("--") && typeof value === "string")
          document.documentElement.style.setProperty(key, value);
      }
    if (variables?.["--color-scheme"] === "light" || variables?.["--color-scheme"] === "dark")
      document.documentElement.dataset.theme = variables["--color-scheme"];
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
      if (destination) void navigateTo(destination, requestedHostItemId);
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
      if (typeof activeCleanup?.goBack === "function" && await activeCleanup.goBack()) {
        if (!closed) title.focus({ preventScroll: true });
      }
    } catch { return; /* The module retains its route and presents the owner error. */ }
  });
  mobileNavigation.addEventListener("change", () => {
    const destination = availablePage(mobileNavigation.value);
    if (destination) void navigateTo(destination);
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
    if (initial) void navigateTo(initial, requestedHostItemId);
    else setStatus(t("settings.noPages"), true);
  }).catch(() => {
    if (closed) return;
    navigation.replaceChildren();
    setStatus(t("settings.catalogFailed"), true);
  });
})();
