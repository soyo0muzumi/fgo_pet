(() => {
  "use strict";
  // One message contract for Peek and Workspace. Each view owns only its own
  // subscription and pending requests, which end with that page's lifetime.
  window.FgoPetTodoBridge = Object.freeze({
    create(prefix, onEvent) {
      const bridge = window.chrome && window.chrome.webview;
      const pending = new Map();
      let serial = 0;
      let closed = false;

      function request(type, payload = {}) {
        if (!bridge || closed) return Promise.reject(new Error("WEB_UNAVAILABLE"));
        const requestId = `${prefix}_${++serial}`;
        return new Promise((resolve, reject) => {
          pending.set(requestId, { resolve, reject });
          bridge.postMessage({ type, requestId, payload });
        });
      }

      function receive(event) {
        const message = event.data;
        if (!message || typeof message.type !== "string") return;
        if (message.type === "command.result") {
          const waiter = pending.get(message.requestId);
          if (!waiter) return;
          pending.delete(message.requestId);
          if (message.success) waiter.resolve(message.payload);
          else waiter.reject(new Error(message.errorCode || "TODO_COMMAND_FAILED"));
        } else if (message.type === "theme.changed") {
          for (const [key, value] of Object.entries(message.variables || {})) {
            if (key.startsWith("--") && typeof value === "string") document.documentElement.style.setProperty(key, value);
          }
          bridge.postMessage({ type: "theme.ack", version: message.version });
        } else onEvent(message);
      }

      function close() {
        if (closed) return;
        closed = true;
        pending.clear();
        bridge?.removeEventListener("message", receive);
      }

      bridge?.addEventListener("message", receive);
      window.addEventListener("pagehide", close, { once: true });
      return {
        available: !!bridge,
        request,
        ready() { if (bridge && !closed) bridge.postMessage({ type: "ready" }); },
        close,
      };
    },
  });
})();
