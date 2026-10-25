import React, { useEffect, useId, useRef, useSyncExternalStore } from "react";
import { ConnectionSettings, type ConnectionActions, type ConnectionView } from "../SettingsViews";
import { createNativeModelConnectionController, type NativeModelConnectionController,
  type SettingsModuleContext } from "../nativeSettingsBridge";
import { attachSettingsPage, ensureSettingsStyles, styleFailure } from "./shared";
import "../settings.css";

function ModelConnectionPage({ controller, stylesReady }: { controller: NativeModelConnectionController; stylesReady: boolean }) {
  const state = useSyncExternalStore(controller.subscribe, controller.getSnapshot, controller.getSnapshot);
  const id = useId();
  const source = state.host && !state.draftPending && state.draft.providerId === state.host.providerId &&
    state.draft.baseUrl === state.host.baseUrl ? JSON.stringify([state.host.providerId, state.host.baseUrl]) : null;
  const lastSource = useRef<string | null>(null);
  const disabled = !state.ready || state.busy || !!state.host?.isBusy;
  useEffect(() => {
    if (!source || disabled) return;
    // load() already requests the initial list. Later source changes refresh only
    // after the host has acknowledged the final input and typing has settled.
    if (lastSource.current === null) { lastSource.current = source; return; }
    if (lastSource.current === source) return;
    const timeout = setTimeout(() => {
      lastSource.current = source;
      void controller.refreshModels();
    }, 280);
    return () => clearTimeout(timeout);
  }, [controller, source, disabled]);

  const host = state.host;
  if (!host) return <section className="settings-surface settings-native-entry" data-theme="light" aria-label="模型与连接">
    <div className="settings-native-load" role={state.error ? "alert" : "status"}>
      <p>{state.error || "正在读取桌面连接设置…"}</p>
      {!stylesReady && <p>{styleFailure}</p>}
      {state.error && <button type="button" className="settings-secondary-button" disabled={state.busy}
        onClick={() => { void controller.load(); }}>重新读取</button>}
    </div>
  </section>;
  const sameSource = state.draft.providerId === host.providerId && state.draft.baseUrl === host.baseUrl;
  const view: ConnectionView = {
    providerId: state.draft.providerId,
    providers: host.providers.map(provider => ({ id: provider.id, label: provider.displayName })),
    endpoint: state.draft.baseUrl, modelId: state.draft.modelId,
    models: sameSource ? host.availableModels.map(model => ({ id: model.id, label: model.displayName,
      detail: [model.contextWindowTokens === null ? "" : `上下文 ${model.contextWindowTokens.toLocaleString()} tokens`,
        model.maxOutputTokens === null ? "" : `最大输出 ${model.maxOutputTokens.toLocaleString()} tokens`].filter(Boolean).join(" · ") || undefined,
    })) : [],
    loading: state.refreshingModels,
    testing: state.activeAction === "test", saving: state.activeAction === "save", disabled,
    status: state.draftPending ? "正在确认当前输入…" : host.statusText,
    error: state.error || host.errorText || (!stylesReady ? styleFailure : null),
    keyStatus: state.pendingKey ? "新密钥尚未保存" : host.keyStateText,
    // There is no saved-connection/dirty DTO in this host contract. The current
    // card and restore action are deliberately absent rather than inferred.
  };
  const actions: ConnectionActions = {
    provider: value => controller.setDraft("providerId", value),
    endpoint: value => controller.setDraft("baseUrl", value),
    model: value => controller.setDraft("modelId", value),
    refresh: () => { void controller.refreshModels(); },
    test: () => { void controller.test(); },
    save: () => { void controller.save(); },
  };
  return <section className="settings-surface settings-native-entry" data-theme="light" aria-label="模型与连接" aria-busy={state.busy}>
    <ConnectionSettings view={view} actions={actions} embedded credential={<>
      <div className="settings-credential"><input key={state.keyResetVersion} type="password" className="settings-input"
        aria-label="新的 API key" aria-describedby={`${id}-key-help`} autoComplete="new-password" spellCheck={false}
        placeholder={host.isKeySaved ? "已有保存的密钥；输入新密钥可替换" : "输入 API key"} disabled={disabled}
        onChange={event => controller.setApiKey(event.currentTarget.value)} /></div>
        <button type="button" className="settings-secondary-button" disabled={disabled || !host.isKeySaved}
          onClick={() => { void controller.clearKey(); }}>清除已保存密钥</button>
      <p id={`${id}-key-help`} className="settings-field-help">已保存的密钥不会回显。新密钥在保存连接后写入桌面凭据存储。</p>
    </>} advanced={<>
      <div className="settings-inline-fields">
        <div className="settings-field"><label className="settings-field-label" htmlFor={`${id}-context`}>上下文窗口覆盖值</label>
          <input id={`${id}-context`} className="settings-input" inputMode="numeric" autoComplete="off" disabled={disabled}
            value={state.draft.contextWindowOverrideText} aria-describedby={`${id}-context-help`}
            onChange={event => controller.setDraft("contextWindowOverride", event.currentTarget.value)} />
          <p id={`${id}-context-help`} className="settings-field-help">{host.contextLimitText}</p>
        </div>
        <div className="settings-field"><label className="settings-field-label" htmlFor={`${id}-output`}>最大输出 tokens</label>
          <input id={`${id}-output`} className="settings-input" inputMode="numeric" autoComplete="off" disabled={disabled}
            value={state.draft.maxOutputTokensText}
            onChange={event => controller.setDraft("maxOutputTokens", event.currentTarget.value)} />
        </div>
      </div>
      <label className="settings-check-row"><input type="checkbox" checked={host.showReasoning} disabled={disabled}
        onChange={event => { void controller.setShowReasoning(event.currentTarget.checked); }} />显示思考过程</label>
      <p className="settings-field-help">此选项单独保存。</p>
      {host.providerStatusText && <p className="settings-field-help">{host.providerStatusText}</p>}
      {host.modelStatusText && <p className="settings-field-help">{host.modelStatusText}</p>}
    </>} />
  </section>;
}

export async function mount(container: HTMLElement, context: SettingsModuleContext): Promise<() => void> {
  const stylesReady = await ensureSettingsStyles(
    new URL("../margin/margin-settings.css", import.meta.url).href, context.signal, container.ownerDocument,
  );
  if (context.signal.aborted) return () => {};
  const controller = createNativeModelConnectionController(context);
  return attachSettingsPage(container, context, controller,
    <ModelConnectionPage controller={controller} stylesReady={stylesReady} />);
}
