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
  "settings.page": "Settings page",
  "settings.back": "Back",
  "settings.companion": "Companion",
  "settings.companionSub": "Character · presence · voice",
  "settings.companionLead": "Character, desktop presence, and expression.",
  "settings.services": "Services",
  "settings.servicesSub": "Model · voice · QQ",
  "settings.servicesLead": "Services for conversation, speech, and mobile chat.",
  "settings.app": "App",
  "settings.appSub": "Everyday preferences",
  "settings.appLead": "Make Margin feel at home on your desktop.",
  "settings.privacy": "Privacy & Safety",
  "settings.privacySub": "Access · permissions · memory",
  "settings.privacyLead": "Manage access, permissions, and memory.",
  "settings.offline": "Focus and remembered things remain available without these services.",
  "settings.checkService": "Open to view current status and configuration",
  "settings.scope": "Authorized scope",
  "settings.permissions": "Action permissions",
  "settings.scopeUnavailable": "An access settings page is not available in this build.",
  "settings.permissionsUnavailable": "A permissions settings page is not available in this build.",
  "settings.more": "More settings",
  "settings.page.Personalization": "Character & presence",
  "settings.page.RolePackages": "Characters",
  "settings.page.ModelConnection": "Model",
  "settings.page.Speech": "Voice Service",
  "settings.page.Qq": "QQ",
  "settings.page.UserProfile": "Your name",
  "settings.page.Theme": "Appearance",
  "settings.page.ConversationMemory": "Conversation memory",
  "settings.page.Understanding": "Understanding",
  "settings.page.Privacy": "Local data & backups",
  "settings.page.AgentConnection": "Access settings"
};
  const t = key => english[key] || key;
  window.MarginSettingsI18n = { t };
  document.querySelectorAll("[data-i18n]").forEach(node => node.textContent = t(node.dataset.i18n));
  for (const attribute of ["aria-label", "placeholder"])
    document.querySelectorAll(`[data-i18n-${attribute}]`).forEach(node =>
      node.setAttribute(attribute, t(node.getAttribute(`data-i18n-${attribute}`))));
})();
