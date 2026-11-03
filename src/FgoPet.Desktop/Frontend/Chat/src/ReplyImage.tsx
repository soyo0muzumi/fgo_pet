import { useEffect, useRef, useState } from "react";
import { createPortal } from "react-dom";
import "./reply-image.css";

type PreviewPort = {
  postMessage(message: unknown): void;
  addEventListener(type: "message", listener: (event: MessageEvent) => void): void;
  removeEventListener(type: "message", listener: (event: MessageEvent) => void): void;
};

/** Pixels arrive only from the opt-in native raster loader. CSP still forbids remote images. */
export function ReplyImage({ src, alt, streaming }: { src?: string; alt?: string; streaming: boolean }) {
  const [imageId] = useState(() => crypto.randomUUID());
  const [preview, setPreview] = useState<{ url: string; data: string }>();
  const [status, setStatus] = useState<"idle" | "loading" | "failed">("idle");
  const [expanded, setExpanded] = useState(false);
  const [attempt, setAttempt] = useState(0);
  const container = useRef<HTMLSpanElement>(null);
  const dialog = useRef<HTMLDialogElement>(null);
  const label = alt || "分享的图片";
  let url: URL | undefined;
  try { url = new URL(src || ""); } catch { /* A partial streaming URL is not actionable. */ }
  const source = url && ["https:", "http:"].includes(url.protocol) && !url.username && !url.password ? url.href : undefined;
  const canPreview = !!source && url?.protocol === "https:" && !url.port && source.length <= 2048;
  const data = preview && preview.url === source ? preview.data : undefined;

  useEffect(() => {
    setStatus("idle"); setExpanded(false); setPreview(undefined);
  }, [source]);
  useEffect(() => {
    if (!canPreview || streaming) return;
    const port = (window as Window & { chrome?: { webview?: PreviewPort } }).chrome?.webview;
    if (!port) { setStatus("failed"); return; }
    let timer: ReturnType<typeof setTimeout> | undefined;
    let settled = false;
    const receive = (event: MessageEvent) => {
      const value = event.data;
      if (settled || !value || value.type !== "web.imagePreview.result" || value.imageId !== imageId) return;
      settled = true;
      clearTimeout(timer);
      if (typeof value.dataUrl === "string" && value.dataUrl.length <= 5_592_432 && value.dataUrl.startsWith("data:image/png;base64,")) {
        setPreview({ url: source!, data: value.dataUrl }); setStatus("idle");
      } else setStatus("failed");
    };
    port.addEventListener("message", receive);
    // Older history and hidden compact views do not trigger background downloads.
    const observer = new IntersectionObserver(entries => {
      if (!entries.some(entry => entry.isIntersecting)) return;
      observer.disconnect();
      setStatus("loading");
      timer = setTimeout(() => { settled = true; setStatus("failed"); }, 18_000);
      port.postMessage({ type: "web.imagePreview", requestId: imageId, payload: { url: source, imageId } });
    }, { rootMargin: "160px" });
    if (container.current) observer.observe(container.current);
    return () => { settled = true; clearTimeout(timer); observer.disconnect(); port.removeEventListener("message", receive); };
  }, [attempt, canPreview, streaming, imageId, source]);
  useEffect(() => {
    if (expanded && dialog.current && !dialog.current.open) dialog.current.showModal();
  }, [expanded]);

  return <span className="reply-image" ref={container}>
    {data ? <button type="button" className="reply-image-thumbnail" onClick={() => setExpanded(true)} aria-label={`放大图片：${label}`}>
      <img src={data} alt={label} onError={() => { setPreview(undefined); setStatus("failed"); }} />
    </button> : status === "failed" ? <button type="button" className="reply-image-load" onClick={() => setAttempt(value => value + 1)}>重试加载配图</button>
      : canPreview && <span className="reply-image-placeholder" role="status">{streaming ? "配图随回复完成后显示…" : "正在加载配图…"}</span>}
    <span className="reply-image-caption">{label}</span>
    {status === "failed" && <span className="reply-image-note" role="status">配图暂时无法加载，可以打开原图查看。</span>}
    {!canPreview && <span className="reply-image-note">此图片暂不支持预览。</span>}
    {source && <a className="reply-image-source" href={source} target="_blank" rel="noopener noreferrer">打开原图 · {url?.hostname}</a>}
    {expanded && data && createPortal(<dialog ref={dialog} className="reply-image-dialog" aria-label={label}
      onClose={() => setExpanded(false)}
      onKeyDown={event => { if (event.key === "Escape") { event.preventDefault(); event.stopPropagation(); setExpanded(false); } }}
      onClick={event => { if (event.target === event.currentTarget) setExpanded(false); }}>
      <button type="button" className="reply-image-close" autoFocus onClick={() => setExpanded(false)}>关闭大图</button>
      <img src={data} alt={label} />
      <span>{label}</span>
      {source && <a href={source} target="_blank" rel="noopener noreferrer">打开原图出处</a>}
    </dialog>, document.body)}
  </span>;
}
