import React, { createContext, useCallback, useContext, useEffect, useId, useLayoutEffect, useRef, useState } from "react";
import Markdown, { type Components } from "react-markdown";
import remarkGfm from "remark-gfm";
import { TextSelect, WrapText } from "lucide-react";
import { ReplyImage } from "./ReplyImage";

const StreamingContext = createContext(false);

/** Keep wide content inside its own viewport, including while text streams in. */
function useHorizontalOverflow(content: React.ReactNode) {
  const viewport = useRef<HTMLDivElement>(null);
  const [overflows, setOverflows] = useState(false);
  const measure = useCallback(() => {
    const element = viewport.current;
    if (element) setOverflows(element.scrollWidth > element.clientWidth + 1);
  }, []);
  useLayoutEffect(measure, [content, measure]);
  useEffect(() => {
    const element = viewport.current;
    if (!element) return;
    const observer = new ResizeObserver(measure);
    observer.observe(element);
    if (element.firstElementChild) observer.observe(element.firstElementChild);
    return () => observer.disconnect();
  }, [measure]);
  return { viewport, overflows };
}

function CodeBlock({ children }: React.ComponentPropsWithoutRef<"pre">) {
  const streaming = useContext(StreamingContext);
  const [wrapped, setWrapped] = useState(false);
  const [selectionNote, setSelectionNote] = useState("");
  const hintId = useId();
  const { viewport, overflows } = useHorizontalOverflow(children);
  const code = React.Children.toArray(children).find((child) =>
    React.isValidElement<{ className?: string }>(child),
  ) as React.ReactElement<{ className?: string }> | undefined;
  const language = /(?:^|\s)language-([^\s]+)/.exec(code?.props.className || "")?.[1];
  const label = language === "csharp" || language === "cs"
    ? "C#"
    : language === "typescript" || language === "ts"
      ? "TypeScript"
      : language === "javascript" || language === "js"
        ? "JavaScript"
        : language || "代码";

  function selectCode() {
    const element = viewport.current?.querySelector("code");
    const selection = window.getSelection();
    if (!element || !selection) return;
    viewport.current?.focus({ preventScroll: true });
    const range = document.createRange();
    range.selectNodeContents(element);
    selection.removeAllRanges();
    selection.addRange(range);
    setSelectionNote("代码已选中，可按 Ctrl+C 复制。");
  }

  return (
    <div className="markdown-code" data-wrap={wrapped}>
      <div className="code-toolbar">
        <span className="code-language">{label}</span>
        <div className="code-tools">
          <button
            type="button"
            aria-label="代码自动换行"
            aria-pressed={wrapped}
            title={wrapped ? "保留原始行，横向滚动" : "自动换行"}
            onClick={() => setWrapped((value) => !value)}
          >
            <WrapText size={13} aria-hidden="true" />
            <span>换行</span>
          </button>
          <button
            type="button"
            onClick={selectCode}
            disabled={streaming}
            title={streaming ? "回复完成后可选中代码" : "选中代码，按 Ctrl+C 复制"}
          >
            <TextSelect size={13} aria-hidden="true" />
            <span>选中代码</span>
          </button>
        </div>
      </div>
      <div
        ref={viewport}
        className="code-viewport"
        role="region"
        aria-label={language ? `${label}代码内容` : "代码内容"}
        aria-describedby={overflows && !wrapped ? hintId : undefined}
        tabIndex={0}
      >
        <pre>{children}</pre>
      </div>
      {overflows && !wrapped && (
        <span className="markdown-overflow-hint" id={hintId}>
          左右滚动查看完整代码
        </span>
      )}
      <span className="sr-only" role="status">{selectionNote}</span>
    </div>
  );
}

function MarkdownTable({ children, ...props }: React.ComponentPropsWithoutRef<"table">) {
  const hintId = useId();
  const { viewport, overflows } = useHorizontalOverflow(children);
  return (
    <div className="markdown-table">
      {overflows && (
        <span className="markdown-overflow-hint" id={hintId}>
          左右滚动查看完整表格
        </span>
      )}
      <div
        ref={viewport}
        className="table-scroll"
        role="region"
        aria-label="回复中的表格"
        aria-describedby={overflows ? hintId : undefined}
        tabIndex={overflows ? 0 : undefined}
      >
        <table {...props}>{children}</table>
      </div>
    </div>
  );
}

// Stable component identities preserve block controls as streamed Markdown changes.
const components: Components = {
  a: ({ node, ...props }) => <a {...props} target="_blank" rel="noopener noreferrer" />,
  img: ({ src, alt }) => <ReplyImage key={typeof src === "string" ? src : ""} src={typeof src === "string" ? src : undefined} alt={alt} streaming={useContext(StreamingContext)} />,
  pre: ({ node, ...props }) => <CodeBlock {...props} />,
  table: ({ node, ...props }) => <MarkdownTable {...props} />,
};
const plugins = [remarkGfm];

export function MarkdownContent({ text, streaming = false }: { text: string; streaming?: boolean }) {
  return (
    <StreamingContext.Provider value={streaming}>
      <Markdown remarkPlugins={plugins} skipHtml components={components}>{text}</Markdown>
    </StreamingContext.Provider>
  );
}
