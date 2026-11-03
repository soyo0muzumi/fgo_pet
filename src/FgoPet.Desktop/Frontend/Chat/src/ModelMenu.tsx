import React, { useEffect, useRef, useState } from "react";
import { ArrowUpRight, Check, LoaderCircle, Settings2, X } from "lucide-react";
import type { Choice } from "./domain";

export type ModelMenuProps = {
  id: string;
  models: readonly Choice[];
  canManage: boolean;
  status?: string;
  triggerRef: React.RefObject<HTMLButtonElement | null>;
  onSelect: (id: string) => Promise<boolean>;
  onManage: () => void;
  onClose: (restoreFocus?: boolean) => void;
};

/** A small, non-modal chooser inside the existing WebView bounds. Its parent
 * owns the presentation and trigger focus; this component never changes view. */
export function ModelMenu(props: ModelMenuProps) {
  const menu = useRef<HTMLElement>(null);
  const latest = useRef(props);
  latest.current = props;
  const live = useRef(false);
  const pending = useRef(false);
  const [pendingId, setPendingId] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const modelSignature = props.models.map(model => `${model.id}:${model.selected}:${model.disabled}`).join("|");

  useEffect(() => {
    const node = menu.current;
    if (!node || document.activeElement?.getAttribute("role") === "menuitemradio") return;
    node.querySelector<HTMLButtonElement>('[role="menuitemradio"][aria-checked="true"]:not(:disabled), [role="menuitemradio"]:not(:disabled)')
      ?.focus({ preventScroll: true });
  }, [modelSignature]);

  useEffect(() => {
    live.current = true;
    const node = menu.current;
    const selected = node?.querySelector<HTMLButtonElement>('[role="menuitemradio"][aria-checked="true"]:not(:disabled)');
    const first = node?.querySelector<HTMLButtonElement>('[role="menuitemradio"]:not(:disabled), [role="menuitem"]:not(:disabled)');
    (selected || first || node)?.focus({ preventScroll: true });

    const contains = (target: EventTarget | null) => target instanceof Node &&
      (!!menu.current?.contains(target) || !!latest.current.triggerRef.current?.contains(target));
    function pointerDown(event: PointerEvent) {
      if (!contains(event.target)) latest.current.onClose(false);
    }
    function focusIn(event: FocusEvent) {
      if (!contains(event.target)) latest.current.onClose(false);
    }
    function keyDown(event: KeyboardEvent) {
      const node = menu.current;
      if (event.defaultPrevented || event.isComposing || !node?.isConnected || node.closest("[inert]")) return;
      if (event.key === "Escape") {
        event.preventDefault();
        event.stopPropagation();
        latest.current.onClose(true);
        return;
      }
      if (!node.contains(event.target as Node) || pending.current) return;
      if (!["ArrowDown", "ArrowUp", "Home", "End"].includes(event.key)) return;
      const options = Array.from(node.querySelectorAll<HTMLButtonElement>(
        '[role="menuitemradio"]:not(:disabled), [role="menuitem"]:not(:disabled)',
      ));
      if (!options.length) return;
      event.preventDefault();
      const current = options.indexOf(document.activeElement as HTMLButtonElement);
      const index = event.key === "Home" ? 0 : event.key === "End" ? options.length - 1 :
        current < 0 ? event.key === "ArrowDown" ? 0 : options.length - 1 :
          (current + (event.key === "ArrowDown" ? 1 : -1) + options.length) % options.length;
      options[index].focus({ preventScroll: true });
      options[index].scrollIntoView({ block: "nearest" });
    }
    document.addEventListener("pointerdown", pointerDown);
    document.addEventListener("focusin", focusIn);
    document.addEventListener("keydown", keyDown);
    return () => {
      live.current = false;
      document.removeEventListener("pointerdown", pointerDown);
      document.removeEventListener("focusin", focusIn);
      document.removeEventListener("keydown", keyDown);
    };
  }, []);

  async function choose(model: Choice) {
    if (pending.current || model.disabled) return;
    if (model.selected) { latest.current.onClose(true); return; }
    pending.current = true;
    setPendingId(model.id);
    setError(null);
    try {
      const ok = await latest.current.onSelect(model.id);
      if (!live.current) return;
      if (ok) latest.current.onClose(true);
      else setError("模型暂时没有切换成功，请重试。");
    } catch {
      if (live.current) setError("模型暂时没有切换成功，请重试。");
    } finally {
      pending.current = false;
      if (live.current) setPendingId(null);
    }
  }

  return <section ref={menu} id={props.id} className="model-menu" aria-label="模型选择" aria-busy={!!pendingId} tabIndex={-1}>
    <header className="model-menu-heading">
      <strong>模型</strong><span>用于后续消息</span>
      <button type="button" aria-label="关闭模型菜单" onClick={() => props.onClose(true)}><X size={13} /></button>
    </header>
    <div role="menu" aria-label="选择对话模型">
      <div className="model-menu-options" role="group" aria-label="可用模型">
        {props.models.map(model => <button key={model.id} type="button" role="menuitemradio" tabIndex={-1}
          className={`model-menu-option ${model.selected ? "selected" : ""} ${pendingId === model.id ? "busy" : ""}`}
          aria-checked={!!model.selected} aria-disabled={model.disabled || !!pendingId}
          disabled={model.disabled} onClick={() => void choose(model)}>
          <span>{model.label}</span>
          {pendingId === model.id ? <LoaderCircle size={14} className="model-menu-spinner" /> : model.selected ? <Check size={14} /> : null}
        </button>)}
        {!props.models.length && <p className="model-menu-empty">当前连接没有可选模型。</p>}
      </div>
      <button type="button" role="menuitem" tabIndex={-1} className="model-menu-management" disabled={!props.canManage || !!pendingId}
        onClick={() => { props.onClose(false); props.onManage(); }}>
        <Settings2 size={13} /><span>管理模型与连接</span><ArrowUpRight size={13} />
      </button>
    </div>
    {props.status && <p className="model-menu-status">{props.status}</p>}
    {error && <p className="model-menu-error" role="alert">{error}</p>}
  </section>;
}
