(() => {
  "use strict";

  const bridge = window.chrome && window.chrome.webview;
  const navigation = document.getElementById("settings-navigation");
  const search = document.getElementById("settings-search");
  const title = document.getElementById("page-title");
  const description = document.getElementById("page-description");
  const status = document.getElementById("root-status");
  const pageContent = document.getElementById("page-content");
  const pending = new Map();
  const expandedGroups = new Map();
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

  function normalizedGroup(page) {
    const name = typeof page.group === "string" ? page.group.trim() : "";
    return name || "其他";
  }

  function matches(page, query) {
    if (!query) return true;
    const fields = [page.id, page.title, page.description, normalizedGroup(page), ...(Array.isArray(page.keywords) ? page.keywords : [])];
    return fields.some(value => typeof value === "string" && value.toLocaleLowerCase().includes(query));
  }

  function renderNavigation() {
    const query = search.value.trim().toLocaleLowerCase();
    const groups = new Map();
    for (const page of pages) {
      if (!matches(page, query)) continue;
      const group = normalizedGroup(page);
      if (!groups.has(group)) groups.set(group, []);
      groups.get(group).push(page);
    }

    const fragment = document.createDocumentFragment();
    let visibleCount = 0;
    for (const [groupName, groupPages] of groups) {
      const section = document.createElement("section");
      section.className = "navigation-group";
      const toggle = document.createElement("button");
      toggle.type = "button";
      toggle.className = "group-toggle";
      toggle.setAttribute("aria-expanded", String(expandedGroups.get(groupName) !== false));
      toggle.setAttribute("aria-controls", `group-${Array.from(groupName).map(character => character.codePointAt(0).toString(16)).join("-")}`);
      const label = document.createElement("span");
      label.textContent = groupName;
      const chevron = document.createElement("span");
      chevron.className = "group-chevron";
      chevron.setAttribute("aria-hidden", "true");
      chevron.textContent = "⌄";
      toggle.append(label, chevron);

      const list = document.createElement("div");
      list.className = "navigation-items";
      list.id = toggle.getAttribute("aria-controls");
      list.hidden = expandedGroups.get(groupName) === false;
      toggle.addEventListener("click", () => {
        const expanded = toggle.getAttribute("aria-expanded") !== "true";
        expandedGroups.set(groupName, expanded);
        toggle.setAttribute("aria-expanded", String(expanded));
        list.hidden = !expanded;
      });

      groupPages.sort((left, right) => (Number(left.order) || 0) - (Number(right.order) || 0)
        || String(left.title || "").localeCompare(String(right.title || ""), "zh-CN"));
      for (const page of groupPages) {
        visibleCount++;
        const item = document.createElement("button");
        item.type = "button";
        item.className = "navigation-item";
        item.dataset.pageId = String(page.id ?? "");
        item.textContent = typeof page.title === "string" ? page.title : "未命名页面";
        const available = page.available === true && typeof page.module === "string" && modulePathPattern.test(page.module);
        item.disabled = !available;
        if (!available) {
          item.classList.add("is-unavailable");
          const unavailableLabel = page.id === "AgentConnection" ? "暂未开放" : "暂不可用";
          item.title = unavailableLabel;
          item.setAttribute("aria-label", `${item.textContent}，${unavailableLabel}`);
          const marker = document.createElement("span");
          marker.className = "migration-marker";
          marker.textContent = unavailableLabel;
          item.append(marker);
        }
        if (activePage && page.id === activePage.id) item.setAttribute("aria-current", "page");
        item.addEventListener("click", () => { void navigateTo(page); });
        list.append(item);
      }
      section.append(toggle, list);
      fragment.append(section);
    }

    navigation.replaceChildren(fragment);
    if (visibleCount === 0) {
      const empty = document.createElement("p");
      empty.className = "navigation-empty";
      empty.textContent = "没有匹配的设置";
      navigation.append(empty);
    }
  }

  function availablePage(pageId) {
    return pages.find(page => page.id === pageId && page.available === true
      && typeof page.module === "string" && modulePathPattern.test(page.module)) || null;
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
            setStatus("此项目暂时无法打开。", true);
        }
      }
      return;
    }

    const restoreNavigationFocus = navigation.contains(document.activeElement);
    const generation = ++routeGeneration;
    cleanupActivePage();
    title.textContent = typeof page.title === "string" ? page.title : "设置";
    description.textContent = typeof page.description === "string" ? page.description : "";
    setStatus("正在打开页面…");
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
        Array.from(navigation.querySelectorAll(".navigation-item"))
          .find(item => item.dataset.pageId === String(page.id))?.focus({ preventScroll: true });
    } catch (error) {
      if (closed || generation !== routeGeneration) return;
      cleanupActivePage();
      setStatus(error && error.message === "SETTINGS_PAGE_UNAVAILABLE"
        ? "此设置页面暂不可用。"
        : "页面打开失败，请切换页面后重试。", true);
      renderNavigation();
      if (restoreNavigationFocus)
        Array.from(navigation.querySelectorAll(".navigation-item"))
          .find(item => item.dataset.pageId === String(page.id))?.focus({ preventScroll: true });
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
    navigation.textContent = "此页面需要在 FGO Pet 中打开。";
    setStatus("设置宿主不可用。", true);
    search.disabled = true;
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

  search.addEventListener("input", renderNavigation);
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
    for (const page of pages) {
      const group = normalizedGroup(page);
      if (!expandedGroups.has(group)) expandedGroups.set(group, true);
    }
    renderNavigation();
    const initial = availablePage(requestedHostPageId)
      || pages.find(page => page.id === catalog.selectedPageId && page.available === true)
      || pages.filter(page => page.available === true && typeof page.module === "string" && modulePathPattern.test(page.module))
        .sort((left, right) => (Number(left.order) || 0) - (Number(right.order) || 0)
          || String(left.title || "").localeCompare(String(right.title || ""), "zh-CN"))[0];
    if (initial) void navigateTo(initial, requestedHostItemId);
    else setStatus("暂无可用的设置页面。", true);
  }).catch(() => {
    if (closed) return;
    navigation.replaceChildren();
    setStatus("设置目录读取失败，请重试。", true);
  });
})();
