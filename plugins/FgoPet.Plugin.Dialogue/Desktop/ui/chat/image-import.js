/* Explicit paste/drop only. Never reads the clipboard in the background or follows URLs. */
(() => {
  "use strict";
  function install(root, options) {
    let busy = false;
    async function receive(event) {
      const source = event.clipboardData || event.dataTransfer;
      if (!event.isTrusted || !source) return;
      const files = Array.from(source.files || []);
      if (!files.length) return; // Preserve ordinary text paste and text dragging.
      event.preventDefault();
      if (busy || !options.canImport()) { options.error("unavailable"); return; }
      const file = files[0];
      const mime = file?.type || (/\.png$/i.test(file?.name || "") ? "image/png" : /\.jpe?g$/i.test(file?.name || "") ? "image/jpeg" : "");
      if (files.length !== 1 || !["image/png", "image/jpeg"].includes(mime) || file.size < 1 || file.size > 8 * 1024 * 1024) {
        options.error("invalid"); return;
      }
      const identity = options.identity();
      const transferId = crypto.randomUUID().replaceAll("-", "");
      busy = true; options.busy(true);
      let complete = false;
      try {
        for (let offset = 0; offset < file.size; offset += 24576) {
          if (identity !== options.identity()) throw new Error("stale");
          const bytes = new Uint8Array(await file.slice(offset, offset + 24576).arrayBuffer());
          if (identity !== options.identity()) throw new Error("stale");
          await options.send("chat.image", { transferId, mimeType: mime, offset,
            data: btoa(String.fromCharCode(...bytes)), complete: offset + bytes.length === file.size });
        }
        complete = true;
      } catch { options.error("invalid"); }
      finally {
        if (!complete && identity === options.identity()) {
          try { await options.send("chat.image.cancel", {}); } catch { /* Host also expires incomplete transfers. */ }
        }
        busy = false; options.busy(false);
      }
    }
    root.addEventListener("paste", receive);
    root.addEventListener("drop", receive);
    root.addEventListener("dragover", event => {
      if (Array.from(event.dataTransfer?.types || []).includes("Files")) {
        event.preventDefault(); event.dataTransfer.dropEffect = options.canImport() ? "copy" : "none";
      }
    });
  }
  globalThis.ChatImageImport = { install };
})();
