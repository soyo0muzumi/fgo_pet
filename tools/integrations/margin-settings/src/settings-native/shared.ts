import type { ReactNode } from "react";
import { createRoot } from "react-dom/client";
import type { SettingsActionResult, SettingsModuleContext } from "../nativeSettingsBridge";

/** ESM page imports do not themselves attach the extracted stylesheet in the
 * settings root. Reuse one link for both pages; it outlives either page mount. */
export async function ensureSettingsStyles(href: string, signal: AbortSignal, owner: Document): Promise<boolean> {
  if (signal.aborted) return false;
  let link = [...owner.querySelectorAll<HTMLLinkElement>('link[rel="stylesheet"]')]
    .find(candidate => candidate.href === href);
  if (link?.sheet) return true;
  if (!link) {
    link = owner.createElement("link");
    link.rel = "stylesheet";
    link.href = href;
    link.dataset.marginSettingsStyle = "pending";
  }
  const stylesheet = link;
  return new Promise(resolve => {
    let settled = false;
    const finish = (loaded: boolean) => {
      if (settled) return;
      settled = true;
      clearTimeout(timeout);
      stylesheet.removeEventListener("load", loadedStyle);
      stylesheet.removeEventListener("error", failedStyle);
      signal.removeEventListener("abort", aborted);
      resolve(loaded);
    };
    const loadedStyle = () => { stylesheet.dataset.marginSettingsStyle = "loaded"; finish(true); };
    const failedStyle = () => { stylesheet.dataset.marginSettingsStyle = "failed"; finish(false); };
    const aborted = () => finish(false);
    const timeout = setTimeout(() => finish(!!stylesheet.sheet), 5000);
    stylesheet.addEventListener("load", loadedStyle, { once: true });
    stylesheet.addEventListener("error", failedStyle, { once: true });
    signal.addEventListener("abort", aborted, { once: true });
    if (!stylesheet.isConnected) owner.head.append(stylesheet);
    if (stylesheet.sheet) loadedStyle();
    else if (stylesheet.dataset.marginSettingsStyle === "failed") failedStyle();
    else if (signal.aborted) aborted();
  });
}

type PageController = { load(): Promise<SettingsActionResult>; dispose(): void };
export function attachSettingsPage(container: HTMLElement, context: SettingsModuleContext,
  controller: PageController, element: ReactNode): () => void {
  if (context.signal.aborted) { controller.dispose(); return () => {}; }
  const root = createRoot(container);
  let disposed = false;
  const cleanup = () => {
    if (disposed) return;
    disposed = true;
    context.signal.removeEventListener("abort", cleanup);
    controller.dispose();
    root.unmount();
  };
  context.signal.addEventListener("abort", cleanup, { once: true });
  try { root.render(element); }
  catch (error) { cleanup(); throw error; }
  void controller.load();
  return cleanup;
}

export const styleFailure = "界面样式未能完整加载，请重新打开设置页。";
