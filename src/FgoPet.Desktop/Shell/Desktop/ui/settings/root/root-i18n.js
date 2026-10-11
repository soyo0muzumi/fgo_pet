(() => {
  "use strict";
  const english = {
  "settings.other": "Other",
  "settings.untitled": "Untitled page",
  "settings.notAvailableYet": "Not available yet",
  "settings.unavailable": "Unavailable",
  "settings.noMatches": "No matching settings",
  "settings.itemUnavailable": "This item could not be opened.",
  "settings.title": "Settings",
  "settings.opening": "Opening page…",
  "settings.pageUnavailable": "This settings page is unavailable.",
  "settings.openFailed": "The page could not be opened. Choose another page and try again.",
  "settings.openInApp": "Open this page in FGO Pet.",
  "settings.hostUnavailable": "Settings is unavailable.",
  "settings.noPages": "No settings pages are available.",
  "settings.catalogFailed": "Settings could not be loaded. Try again.",
  "settings.navigation": "Settings navigation",
  "settings.search": "Search settings",
  "settings.searchHint": "Search settings and keywords",
  "settings.groups": "Settings groups",
  "settings.loading": "Loading settings…",
  "settings.page": "Settings page"
};
  const t = key => english[key] || key;
  window.MarginSettingsI18n = { t };
  document.querySelectorAll("[data-i18n]").forEach(node => node.textContent = t(node.dataset.i18n));
  for (const attribute of ["aria-label", "placeholder"])
    document.querySelectorAll(`[data-i18n-${attribute}]`).forEach(node =>
      node.setAttribute(attribute, t(node.getAttribute(`data-i18n-${attribute}`))));
})();
