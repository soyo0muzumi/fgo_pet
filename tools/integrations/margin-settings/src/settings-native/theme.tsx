import React, { useSyncExternalStore } from "react";
import { AppearanceSettings, type ThemeChoice } from "../SettingsViews";
import { createNativeThemeController, type NativeThemeController, type SettingsModuleContext,
  type ThemeSelection } from "../nativeSettingsBridge";
import { attachSettingsPage, ensureSettingsStyles, styleFailure } from "./shared";
import "../settings.css";

const choices: Record<ThemeSelection, ThemeChoice> = { System: "system", FgoLight: "light", FgoDark: "dark" };
const selections: Record<ThemeChoice, ThemeSelection> = { system: "System", light: "FgoLight", dark: "FgoDark" };

function ThemePage({ controller, stylesReady }: { controller: NativeThemeController; stylesReady: boolean }) {
  const state = useSyncExternalStore(controller.subscribe, controller.getSnapshot, controller.getSnapshot);
  const host = state.host;
  if (!host) return <section className="settings-surface settings-native-entry" data-theme="light" aria-label="界面配色">
    <div className="settings-native-load" role={state.error ? "alert" : "status"}>
      <p>{state.error || "正在读取桌面主题…"}</p>
      {!stylesReady && <p>{styleFailure}</p>}
      {state.error && <button type="button" className="settings-secondary-button" disabled={state.busy}
        onClick={() => { void controller.load(); }}>重新读取</button>}
    </div>
  </section>;
  const effective = host.effective === "FgoDark" ? "dark" : "light";
  return <section className="settings-surface settings-native-entry" data-theme={effective} aria-label="界面配色" aria-busy={state.busy}>
    <AppearanceSettings choice={choices[host.selected]} effective={effective} embedded busy={state.busy}
      error={state.error || (!stylesReady ? styleFailure : null)}
      onChange={choice => { void controller.select(selections[choice]); }} />
    <div className="settings-native-load">
      {host.statusText && <p role="status">{host.statusText}</p>}
      <p className="settings-field-help">主题选择会保存到桌面设置；此页展示配色预览。</p>
    </div>
  </section>;
}

export async function mount(container: HTMLElement, context: SettingsModuleContext): Promise<() => void> {
  const stylesReady = await ensureSettingsStyles(
    new URL("../margin/margin-settings.css", import.meta.url).href, context.signal, container.ownerDocument,
  );
  if (context.signal.aborted) return () => {};
  const controller = createNativeThemeController(context);
  return attachSettingsPage(container, context, controller, <ThemePage controller={controller} stylesReady={stylesReady} />);
}
