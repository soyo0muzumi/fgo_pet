/* A presentation adapter over native select: values and change handlers stay with the owner. */
(() => {
  "use strict";
  let current = null;
  function close(restore = false) {
    if (!current) return;
    const { menu, trigger } = current;
    menu.remove(); trigger.setAttribute("aria-expanded", "false"); current = null;
    if (restore) trigger.focus();
  }
  function install(select) {
    if (select.dataset.marginSelect) return;
    select.dataset.marginSelect = "true";
    select.classList.add("margin-select-native"); select.tabIndex = -1;
    const trigger = document.createElement("button");
    trigger.type = "button"; trigger.className = "margin-select-trigger";
    trigger.setAttribute("aria-haspopup", "listbox"); trigger.setAttribute("aria-expanded", "false");
    const label = document.createElement("span"), arrow = document.createElement("span");
    arrow.textContent = "⌄"; arrow.setAttribute("aria-hidden", "true"); trigger.append(label, arrow);
    select.after(trigger);
    function sync() {
      label.textContent = select.selectedOptions[0]?.textContent || "";
      trigger.disabled = select.disabled;
      trigger.setAttribute("aria-label", select.getAttribute("aria-label") || label.textContent);
      if (current?.select === select) close();
    }
    function open() {
      if (trigger.disabled) return;
      close();
      const menu = document.createElement("div"); menu.className = "margin-select-menu";
      menu.setAttribute("role", "listbox"); menu.setAttribute("aria-label", trigger.getAttribute("aria-label"));
      const options = [];
      for (const option of select.options) {
        const button = document.createElement("button"); button.type = "button"; button.className = "margin-select-option";
        button.setAttribute("role", "option"); button.setAttribute("aria-selected", String(option.selected));
        button.disabled = option.disabled;
        const text = document.createElement("span"), check = document.createElement("span");
        text.textContent = option.textContent; check.textContent = option.selected ? "✓" : "";
        check.setAttribute("aria-hidden", "true"); button.append(text, check); menu.append(button); options.push(button);
        button.addEventListener("click", () => {
          if (select.disabled || option.disabled || !select.contains(option)) return;
          close(true); select.value = option.value; select.dispatchEvent(new Event("change", { bubbles:true })); sync();
        });
      }
      document.body.append(menu); current = { menu, trigger, select };
      trigger.setAttribute("aria-expanded", "true");
      const rect = trigger.getBoundingClientRect(), margin = 8;
      menu.style.width = `${Math.min(Math.max(rect.width, 160), innerWidth - margin * 2)}px`;
      menu.style.maxHeight = `${Math.max(30, innerHeight - margin * 2)}px`;
      const height = menu.getBoundingClientRect().height;
      menu.style.left = `${Math.max(margin, Math.min(rect.left, innerWidth - menu.offsetWidth - margin))}px`;
      menu.style.top = `${Math.max(margin, Math.min(rect.bottom + 4, innerHeight - height - margin))}px`;
      const available = options.filter(button => !button.disabled);
      (available.find(button => button.getAttribute("aria-selected") === "true") || available[0])?.focus();
      menu.addEventListener("keydown", event => {
        const index = available.indexOf(document.activeElement);
        let next = index;
        if (event.key === "ArrowDown") next = Math.min(available.length - 1, index + 1);
        else if (event.key === "ArrowUp") next = Math.max(0, index - 1);
        else if (event.key === "Home") next = 0;
        else if (event.key === "End") next = available.length - 1;
        else if (event.key === "Escape") { event.preventDefault(); close(true); return; }
        else if (event.key === "Tab") { close(true); return; }
        else return;
        event.preventDefault(); available[next]?.focus();
      });
    }
    trigger.addEventListener("click", () => current?.trigger === trigger ? close(true) : open());
    trigger.addEventListener("keydown", event => {
      if (["ArrowDown", "ArrowUp", "Home", "End"].includes(event.key)) { event.preventDefault(); open(); }
    });
    select.addEventListener("change", sync);
    new MutationObserver(sync).observe(select, { childList:true, subtree:true, attributes:true }); sync();
  }
  function scan() {
    if (current && !current.select.isConnected) close();
    document.querySelectorAll("select").forEach(install);
  }
  document.addEventListener("pointerdown", event => { if (current && !current.menu.contains(event.target) && !current.trigger.contains(event.target)) close(); });
  window.addEventListener("resize", () => close());
  document.addEventListener("scroll", event => { if (current && !current.menu.contains(event.target)) close(); }, true);
  new MutationObserver(scan).observe(document.body, { childList:true, subtree:true }); scan();
})();
