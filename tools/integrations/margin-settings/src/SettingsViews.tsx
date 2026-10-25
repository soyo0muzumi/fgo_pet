import React, { useEffect, useId, useRef, useState } from "react";
import { AlertCircle, ArrowUp, Check, ChevronDown, ChevronRight, CircleCheck, KeyRound, LoaderCircle, Monitor, Moon, Plug, RefreshCw, Sun, WandSparkles } from "lucide-react";

export type ThemeChoice = "system" | "light" | "dark";
export type SettingsPage = "personalization" | "roles" | "profile" | "appearance" | "connection" | "speech" | "qq" | "memory" | "understanding" | "privacy" | "about";

export function SettingsHeading({ kicker, title, children }: { kicker: string; title: string; children: React.ReactNode }) {
  return <header className="settings-page-heading"><span className="settings-eyebrow">{kicker}</span><h2>{title}</h2><p>{children}</p></header>;
}

export function AppearanceSettings({ choice, effective, onChange, busy = false, error, embedded = false, motion }: {
  choice: ThemeChoice;
  effective: "light" | "dark";
  onChange: (choice: ThemeChoice) => void;
  busy?: boolean;
  error?: string | null;
  embedded?: boolean;
  motion?: { reduced: boolean; systemReduced: boolean; onChange: () => void };
}) {
  return <div className="settings-page appearance-page">
    {!embedded && <SettingsHeading kicker="LOOK & FEEL" title="Make yourself at home.">配色与动效，按照你的习惯来。</SettingsHeading>}
    <div className="settings-appearance-layout">
    <div className="settings-appearance-controls">
    <div className="settings-section-heading"><div><h3>界面配色</h3><p>选一种舒服的光线。</p></div><span className="settings-save-hint">{busy ? <LoaderCircle size={12} className="settings-spin" /> : <Check size={12} />}{busy ? "保存中" : embedded ? "选择即保存" : "即时生效"}</span></div>
    <div className="theme-choices" role="group" aria-label="界面配色">
      {([
        ["light", "Light", "日间", Sun],
        ["dark", "Dark", "夜间", Moon],
        ["system", "System", "跟随系统", Monitor],
      ] as const).map(([value, label, description, Icon]) => <button
        key={value} type="button" className={`theme-choice ${choice === value ? "is-selected" : ""}`}
        aria-label={description} aria-pressed={choice === value} disabled={busy} onClick={() => onChange(value)}>
        <span className={`theme-miniature miniature-${value}`} aria-hidden="true"><span className="mini-window"><span className="mini-top"><i /><i /><i /></span><span className="mini-message mine" /><span className="mini-message" /><span className="mini-message short" /><span className="mini-composer" /></span></span>
        <span className="theme-choice-label"><Icon size={14} /><span>{description}<small>{label}</small></span><span className="theme-choice-check">{choice === value && <Check size={12} />}</span></span>
      </button>)}
    </div>
    <p className="settings-field-help">{choice === "system" ? `当前随系统使用${effective === "dark" ? "夜间" : "日间"}配色。` : "让界面保持您选择的明暗。"}</p>
    {error && <p className="settings-feedback is-error" role="alert">{error}</p>}
    <div className="settings-motion-section">
    <div className="settings-section-heading"><div><h3>动效与节奏</h3><p>让切换轻一点，也安静一点。</p></div></div>
    <div className="settings-preference-row">
      <span className="settings-row-icon"><WandSparkles size={18} /></span>
      <div><strong>减少动效</strong><p>{motion?.systemReduced ? "系统已启用减少动效，界面会尊重这一偏好。" : motion ? "保留状态反馈，减少位移和循环动画。" : "动效遵循系统的减少动态效果偏好。"}</p></div>
      {motion ? <button type="button" role="switch" aria-label="减少动效" aria-checked={motion.reduced || motion.systemReduced} disabled={motion.systemReduced}
        className={`settings-switch ${motion.reduced || motion.systemReduced ? "is-on" : ""}`} onClick={motion.onChange}><i /></button>
        : <span className="settings-quiet-badge">跟随系统</span>}
    </div>
    </div>
    </div>
    <aside className="settings-appearance-preview">
      <div className="settings-preview-caption"><span>对话预览</span><span>{effective === "dark" ? "夜间配色" : "日间配色"}</span></div>
      <div className="settings-reading-preview" aria-label="消息排版预览">
        <div className="reading-preview-heading"><span className="preview-mash-mark">✧</span><div><strong>Mash</strong><small><span className="preview-status-dot" />Here with you</small></div><span className="preview-window-dot" aria-hidden="true">···</span></div>
        <div className="preview-user-message">今天想慢一点。</div>
        <div className="preview-assistant-message"><div><p>嗯，前辈。先把眼前的一件小事做好就可以了。</p><p>剩下的，我们慢慢来。</p></div></div>
        <div className="reading-preview-composer" aria-hidden="true"><span>今天有什么想和我说的？</span><ArrowUp size={14} /></div>
      </div>
      <p className="settings-preview-note">留一点空间，给下一句话。</p>
    </aside>
    </div>
  </div>;
}

export type ConnectionModel = { id: string; label: string; detail?: string };
export type ConnectionView = {
  providerId: string;
  providers: Array<{ id: string; label: string }>;
  endpoint: string;
  modelId: string;
  models: ConnectionModel[];
  loading: boolean;
  testing: boolean;
  saving: boolean;
  disabled?: boolean;
  dirty?: boolean;
  current?: { provider: string; endpoint: string; model: string };
  status?: string;
  error?: string | null;
  endpointError?: string;
  modelError?: string;
  keyStatus?: string;
  canSave?: boolean;
};
export type ConnectionActions = {
  provider: (id: string) => void;
  endpoint: (value: string) => void;
  model: (id: string) => void;
  refresh: () => void;
  test: () => void;
  save: () => void;
  restore?: () => void;
};

function ModelPicker({ view, actions }: { view: ConnectionView; actions: ConnectionActions }) {
  const id = useId();
  const ref = useRef<HTMLDivElement>(null);
  const control = useRef<HTMLDivElement>(null);
  const input = useRef<HTMLInputElement>(null);
  const [open, setOpen] = useState(false);
  const [query, setQuery] = useState("");
  const [active, setActive] = useState(-1);
  const [above, setAbove] = useState(false);
  const options = view.models.filter((model) => `${model.id} ${model.label}`.toLowerCase().includes(query.toLowerCase()));
  useEffect(() => { setQuery(""); setActive(-1); }, [view.providerId, view.endpoint]);
  useEffect(() => {
    if (!open) return;
    function outside(event: PointerEvent) { if (!ref.current?.contains(event.target as Node)) setOpen(false); }
    document.addEventListener("pointerdown", outside);
    return () => document.removeEventListener("pointerdown", outside);
  }, [open]);
  function placeMenu() {
    const rect = control.current?.getBoundingClientRect();
    const scrollRect = ref.current?.closest(".settings-form-scroll")?.getBoundingClientRect();
    const bottom = Math.min(scrollRect?.bottom ?? window.innerHeight, window.innerHeight);
    const top = Math.max(scrollRect?.top ?? 0, 0);
    setAbove(!!rect && bottom - rect.bottom < 220 && rect.top - top > 220);
  }
  function show() { if (!open) { placeMenu(); setQuery(""); setActive(-1); actions.refresh(); } setOpen(true); }
  function choose(model: ConnectionModel) { actions.model(model.id); setQuery(""); setOpen(false); input.current?.focus(); }
  return <div className="settings-model-picker" ref={ref}>
    <div className="settings-field-label"><label htmlFor={`${id}-input`}>模型</label><button type="button" className="settings-text-button" disabled={view.disabled || view.loading} onClick={actions.refresh}><RefreshCw size={12} className={view.loading ? "settings-spin" : ""} />{view.loading ? "更新中" : "更新列表"}</button></div>
    <div className="settings-model-control" ref={control}>
    <div className="settings-model-input">
      <input ref={input} id={`${id}-input`} role="combobox" aria-label="模型 ID" autoComplete="off" spellCheck={false}
        aria-autocomplete="list" aria-expanded={open} aria-controls={`${id}-list`} aria-activedescendant={open && active >= 0 && active < options.length ? `${id}-option-${active}` : undefined}
        aria-invalid={!!view.modelError} aria-describedby={`${id}-help`} value={view.modelId} disabled={view.disabled}
        placeholder="选择或输入模型 ID" onChange={(event) => { if (!open) placeMenu(); actions.model(event.target.value); setQuery(event.target.value); setActive(-1); setOpen(true); }}
        onKeyDown={(event) => {
          if (event.key === "Escape" && open) { event.preventDefault(); event.stopPropagation(); setOpen(false); }
          if (event.key === "ArrowDown" || event.key === "ArrowUp") { event.preventDefault(); show(); setActive((value) => options.length ? value < 0 ? event.key === "ArrowDown" ? 0 : options.length - 1 : (value + (event.key === "ArrowDown" ? 1 : -1) + options.length) % options.length : -1); }
          if (event.key === "Enter" && open) { event.preventDefault(); if (options[active]) choose(options[active]); else setOpen(false); }
          if (event.key === "Tab") setOpen(false);
        }} />
      <button type="button" aria-label="查看可用模型" aria-expanded={open} disabled={view.disabled} onClick={() => { if (open) setOpen(false); else { show(); input.current?.focus(); } }}><ChevronDown size={15} /></button>
    </div>
    {open && <div className={`settings-model-menu ${above ? "is-above" : ""}`} id={`${id}-list`} role="listbox" aria-label="可用模型">
      {options.map((model, index) => <button key={model.id} id={`${id}-option-${index}`} type="button" role="option" tabIndex={-1}
        className={index === active ? "is-active" : ""} aria-selected={view.modelId === model.id} onMouseDown={(event) => event.preventDefault()} onClick={() => choose(model)}>
        <span><strong>{model.label}</strong>{model.detail && <small>{model.detail}</small>}</span>{view.modelId === model.id && <Check size={14} />}
      </button>)}
      {!options.length && <p role="status">{view.loading ? "正在获取可用模型…" : query ? "没有匹配项，也可以直接使用输入的 ID。" : "列表暂时为空，可以更新列表或手动填写模型 ID。"}</p>}
    </div>}
    </div>
    <p id={`${id}-help`} className={`settings-field-help ${view.modelError ? "is-error" : ""}`}>{view.modelError || (view.models.length ? `${view.models.length} 个可选模型，也支持手动填写。` : "模型列表随当前连接按需更新。")}</p>
  </div>;
}

export function ConnectionSettings({ view, actions, credential, advanced, demo = false, embedded = false }: {
  view: ConnectionView;
  actions: ConnectionActions;
  credential: React.ReactNode;
  advanced?: React.ReactNode;
  demo?: boolean;
  embedded?: boolean;
}) {
  const endpointId = useId();
  const invalid = !!view.endpointError || !!view.modelError;
  const working = view.saving || view.testing;
  const footerTitle = view.saving ? "正在保存连接" : view.testing ? "正在检查连接"
    : view.error ? "连接操作未完成" : invalid ? "请补全连接信息"
      : view.dirty === true ? "有未保存的更改"
        : view.dirty === false ? demo ? "示例配置已保存" : "配置已保存" : "连接设置";
  const footerDetail = view.saving ? "正在确认保存结果，请稍候。" : view.testing ? "正在检查当前配置，不会自动保存。"
    : view.error || (invalid ? "检查一下上方标出的字段。" : view.status)
      || (view.dirty ? "保存后用于后续对话。" : "检查连接不会自动保存。");
  const footerTone = working ? "" : view.error || invalid ? "is-error" : view.dirty === false ? "is-saved" : view.dirty ? "is-dirty" : "";
  const showAlert = !working && !!view.error;
  return <div className="settings-page connection-page">
    <div className="settings-form-scroll">
      {!embedded && <SettingsHeading kicker="MODELS & CONNECTION" title="A way to think.">准备好连接，接下来的话就交给对话。</SettingsHeading>}
      {demo && <p className="settings-demo-note"><span>示例模式</span>可以试着修改和保存，无需真实密钥。</p>}
      {view.current && <section className="settings-current-connection" aria-label="当前使用的连接">
        <span className="connection-status-icon"><Plug size={18} /></span><div><span className="settings-eyebrow">当前用于对话</span><strong>{view.current.model}</strong><small>{view.current.provider}<span>·</span>{view.current.endpoint}</small></div>
      </section>}
      <div className="settings-connection-layout">
      <section className="settings-form-section" aria-labelledby={`${endpointId}-service`}>
      <div className="settings-section-heading"><div><h3 id={`${endpointId}-service`}>连接服务</h3><p>选择服务，填写它的访问方式。</p></div></div>
      <div className="settings-field">
        <span className="settings-field-label">服务类型</span>
        <div className="settings-provider-options" role="group" aria-label="服务类型">{view.providers.map((provider) => <button type="button" key={provider.id} aria-pressed={view.providerId === provider.id} disabled={view.disabled}
          className={view.providerId === provider.id ? "is-selected" : ""} onClick={() => actions.provider(provider.id)}>{provider.label}{view.providerId === provider.id && <Check size={12} />}</button>)}</div>
      </div>
      <div className="settings-field">
        <label className="settings-field-label" htmlFor={endpointId}>服务地址<span>Base URL</span></label>
        <input className="settings-input endpoint-input" id={endpointId} aria-label="服务地址" value={view.endpoint} disabled={view.disabled} aria-invalid={!!view.endpointError} aria-describedby={`${endpointId}-help`}
          autoComplete="off" spellCheck={false} onChange={(event) => actions.endpoint(event.target.value)} placeholder="https://…/v1" />
        <p id={`${endpointId}-help`} className={`settings-field-help ${view.endpointError ? "is-error" : ""}`}>{view.endpointError || "填写服务商提供的 API 基础地址。"}</p>
      </div>
      <div className="settings-field"><div className="settings-field-label"><span><KeyRound size={12} />API key</span>{view.keyStatus && <small>{view.keyStatus}</small>}</div>{credential}</div>
      </section>
      <section className="settings-form-section settings-model-section" aria-labelledby={`${endpointId}-model`}>
      <div className="settings-section-heading"><div><h3 id={`${endpointId}-model`}>对话模型</h3><p>选择接下来一起思考的模型。</p></div></div>
      <div className="settings-field"><ModelPicker view={view} actions={actions} /></div>
      <div className="settings-model-note"><RefreshCw size={14} /><p>打开列表时会按需更新。<br />也可以直接填写服务商提供的模型 ID。</p></div>
      {advanced && <details className="settings-advanced"><summary><ChevronRight size={14} />更多模型选项</summary><div>{advanced}</div></details>}
      </section>
      </div>
    </div>
    <footer className="settings-form-footer">
      <div className={`settings-footer-feedback ${footerTone}`} role={showAlert ? "alert" : "status"} aria-live={showAlert ? "assertive" : "polite"}>
        <span className="settings-feedback-icon" aria-hidden="true">{working ? <LoaderCircle size={16} className="settings-spin" /> : view.error || invalid ? <AlertCircle size={16} /> : view.dirty === false ? <CircleCheck size={16} /> : <span className="settings-feedback-dot" />}</span>
        <div><strong>{footerTitle}</strong><p>{footerDetail}</p></div>
      </div>
      <div className="settings-footer-actions">
        {actions.restore && <button type="button" className="settings-text-button settings-restore" disabled={!view.dirty || view.saving} onClick={actions.restore}>撤销更改</button>}
        <button type="button" className="settings-secondary-button" disabled={view.disabled || view.testing || view.saving} onClick={actions.test}>{view.testing && <LoaderCircle size={14} className="settings-spin" />}{view.testing ? "检查中…" : "检查连接"}</button>
        <button type="button" className="settings-primary-button" disabled={view.disabled || view.canSave === false || view.dirty === false || view.saving || view.testing} onClick={actions.save}>{view.saving ? <LoaderCircle size={14} className="settings-spin" /> : <Check size={14} />}{view.saving ? "保存中…" : demo ? "保存示例连接" : "保存连接"}</button>
      </div>
    </footer>
  </div>;
}
