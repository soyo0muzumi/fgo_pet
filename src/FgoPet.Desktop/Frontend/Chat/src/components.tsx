import React, { useEffect, useRef, useState } from "react";
import { MarkdownContent } from "./MarkdownContent";
import { AnimatePresence, motion } from "motion/react";
import {
  ArrowUpRight,
  Check,
  CheckCheck,
  ChevronDown,
  Copy,
  FileText,
  FolderOpen,
  ListTodo,
  LoaderCircle,
  ShieldCheck,
  Square,
  Volume2,
  X,
} from "lucide-react";
import type { Answer, Approval, Message, PendingInput, Phase } from "./domain";

export function IconButton({
  label,
  children,
  onClick,
  disabled = false,
  className = "",
  pressed,
}: {
  label: string;
  children: React.ReactNode;
  onClick: () => void;
  disabled?: boolean;
  className?: string;
  pressed?: boolean;
}) {
  return (
    <button
      type="button"
      aria-label={label}
      title={label}
      aria-pressed={pressed}
      className={`icon-button ${className}`}
      onClick={onClick}
      disabled={disabled}
    >
      {children}
    </button>
  );
}

export function Sheet({
  title,
  kicker,
  children,
  onClose,
  className = "",
}: {
  title: string;
  kicker?: string;
  children: React.ReactNode;
  onClose: () => void;
  className?: string;
}) {
  const ref = useRef<HTMLDivElement>(null);
  const close = useRef(onClose);
  close.current = onClose;
  useEffect(() => {
    const previous = document.activeElement as HTMLElement | null;
    ref.current
      ?.querySelector<HTMLElement>("button,input,textarea,select")
      ?.focus();
    function key(e: KeyboardEvent) {
      // A closing quick panel can remain mounted for its exit animation while
      // the separate settings dialog owns focus and the Chat stage is inert.
      if (!ref.current?.isConnected || ref.current.closest("[inert]")) return;
      if (e.key === "Escape") {
        e.preventDefault();
        e.stopPropagation();
        close.current();
        return;
      }
      if (e.key !== "Tab") return;
      const nodes = ref.current?.querySelectorAll<HTMLElement>(
        'button:not(:disabled), input:not(:disabled), textarea:not(:disabled), select:not(:disabled), a[href], [tabindex="0"]',
      );
      if (!nodes?.length) return;
      const first = nodes[0],
        last = nodes[nodes.length - 1];
      if (
        e.shiftKey &&
        (document.activeElement === first ||
          !ref.current?.contains(document.activeElement))
      ) {
        e.preventDefault();
        last.focus();
      } else if (
        !e.shiftKey &&
        (document.activeElement === last ||
          !ref.current?.contains(document.activeElement))
      ) {
        e.preventDefault();
        first.focus();
      }
    }
    document.addEventListener("keydown", key, true);
    return () => {
      document.removeEventListener("keydown", key, true);
      if (previous?.isConnected && !previous.closest("[inert]")) previous.focus();
    };
  }, []);
  return (
    <motion.div
      className="sheet-overlay"
      initial={{ opacity: 0 }}
      animate={{ opacity: 1 }}
      exit={{ opacity: 0 }}
      onMouseDown={(e) => {
        if (e.target === e.currentTarget) onClose();
      }}
    >
      <motion.section
        ref={ref}
        role="dialog"
        aria-modal="true"
        aria-label={title}
        className={`sheet ${className}`}
        initial={{ opacity: 0, y: 12 }}
        animate={{ opacity: 1, y: 0 }}
        exit={{ opacity: 0, y: 8 }}
      >
        <header className="sheet-header">
          <div>
            {kicker && <p className="eyebrow">{kicker}</p>}
            <h2>{title}</h2>
          </div>
          <IconButton label="关闭面板" onClick={onClose}>
            <X size={18} />
          </IconButton>
        </header>
        <div className="sheet-body">{children}</div>
      </motion.section>
    </motion.div>
  );
}

export function MessageItem({
  message,
  roleName,
  onCopy,
  onRead,
  onSpeechSettings,
  onWorkspace,
  onSaveTask,
}: {
  message: Message;
  roleName: string;
  onCopy: () => void;
  onRead: () => void;
  onSpeechSettings: () => void;
  onWorkspace: () => void;
  onSaveTask: () => void;
}) {
  return (
    <article className={`message message-${message.role}`} data-message-id={message.id}>
      {message.role === "assistant" && (
        <div className="message-label">
          <span className="small-flower">✧</span>
          {roleName}
        </div>
      )}
      {message.reasoning && (
        <details className="reasoning">
          <summary>
            <ChevronDown size={13} />
            {message.reasoningSummary || "查看思考摘要"}
            {message.reasoningDuration && (
              <small>{message.reasoningDuration}</small>
            )}
          </summary>
          <p>{message.reasoning}</p>
        </details>
      )}
      {message.imageUrl && (
        <img
          className="attachment-preview"
          src={message.imageUrl}
          alt={message.imageLabel || "附加图片"}
        />
      )}
      {!message.imageUrl && message.imageLabel && (
        <span className="attachment-label">
          <FileText size={13} />
          {message.imageLabel}
        </span>
      )}
      <div
        className={`message-text ${message.streaming ? "is-streaming" : ""}`}
      >
        {message.role === "user" ? (
          message.text
        ) : (
          <MarkdownContent text={message.text} streaming={message.streaming} />
        )}
        {message.streaming && (
          <span className="stream-caret" aria-hidden="true" />
        )}
      </div>
      {message.stopped && (
        <span className="stopped-note">
          回复已停止 · 已生成的内容保留在这里
        </span>
      )}
      {message.task && (
        <div
          className={`action-card task-proposal ${message.task.saved ? "is-saved" : ""}`}
        >
          <div className="card-heading">
            <span className="card-icon">
              {message.task.saved ? (
                <CheckCheck size={18} />
              ) : (
                <ListTodo size={18} />
              )}
            </span>
            <div className="card-copy">
              <small>
                {message.task.saved
                  ? "SAVED TO TASKS"
                  : "A LITTLE LESS TO REMEMBER"}
              </small>
              <strong>{message.task.title}</strong>
              <span>{message.task.due === "tomorrow" ? "明天" : "今天"}</span>
            </div>
          </div>
          <button
            type="button"
            className="secondary-button"
            disabled={message.task.saved}
            onClick={onSaveTask}
          >
            {message.task.saved ? (
              <>
                <Check size={14} />
                已记下
              </>
            ) : (
              <>
                帮我记下
                <ArrowUpRight size={14} />
              </>
            )}
          </button>
        </div>
      )}
      {!message.streaming && message.text && (
        <div className="message-actions">
          {message.canCopy && (
            <IconButton label="复制这条消息" onClick={onCopy}>
              <Copy size={13} />
            </IconButton>
          )}
          {message.canReadAloud && (
            <IconButton
              label={message.speechAction?.trim() || (message.speechBusy ? "停止朗读" : "朗读这条回复")}
              onClick={onRead}
            >
              {message.speechBusy ? (
                <Square size={13} />
              ) : (
                <Volume2 size={13} />
              )}
            </IconButton>
          )}
          {message.speechNeedsConfiguration && (
            <button className="text-button" onClick={onSpeechSettings}>
              设置语音
            </button>
          )}
          {message.canOpenWorkspace && (
            <IconButton label="在工作区中打开" onClick={onWorkspace}>
              <FolderOpen size={13} />
            </IconButton>
          )}
          {message.speechStatus && <span>{message.speechStatus}</span>}
        </div>
      )}
    </article>
  );
}

export function RunStatus({
  phase,
  status,
  detail,
}: {
  phase: Phase;
  status: string;
  detail?: string;
}) {
  const [elapsed, setElapsed] = useState(0);
  useEffect(() => {
    setElapsed(0);
    const started = Date.now();
    const id = setInterval(
      () => setElapsed(Math.floor((Date.now() - started) / 1000)),
      1000,
    );
    return () => clearInterval(id);
  }, [phase]);
  const waiting = phase === "waiting" || phase === "submitting";
  return (
    <motion.div
      className="run-status"
      data-phase={phase}
      role="status"
      aria-live="polite"
      initial={{ opacity: 0, y: 4 }}
      animate={{ opacity: 1, y: 0 }}
      exit={{ opacity: 0 }}
    >
      <div className="status-orbit" aria-hidden="true">
        {phase === "tool" ? (
          <FolderOpen size={17} />
        ) : phase === "stopping" ? (
          <Square size={13} />
        ) : (
          <span className="waiting-dots"><i /><i /><i /></span>
        )}
      </div>
      <div className="status-copy">
        <strong>{status}</strong>
        <small>
          {detail ||
            (waiting && elapsed >= 10
              ? "等待的时间比平时长一些，您可以继续写下一句。"
              : phase === "stopping"
                ? "正在等待停止确认"
                : "")}
        </small>
      </div>
      {phase === "tool" && (
        <LoaderCircle className="loading-icon" size={15} aria-hidden="true" />
      )}
    </motion.div>
  );
}

export function ApprovalCard({
  approval,
  onReview,
  onDeny,
  onCancel,
  busy,
}: {
  approval: Approval;
  onReview: () => void;
  onDeny: () => void;
  onCancel: () => void;
  busy: boolean;
}) {
  return (
    <section className="action-card approval-card" aria-label="等待操作确认">
      <div className="card-heading">
        <span className="card-icon">
          <ShieldCheck size={20} />
        </span>
        <div className="card-copy">
          <small>YOUR SAY MATTERS</small>
          <strong>{approval.title || "需要您确认一下"}</strong>
        </div>
      </div>
      <p>{approval.description}</p>
      {approval.detail && (
        <details key={`${approval.requestId}-${approval.revision}`} open={approval.detailExpanded}>
          <summary>
            查看操作内容 <ChevronDown size={13} />
          </summary>
          <pre>{approval.detail}</pre>
        </details>
      )}
      {approval.error && (
        <p className="inline-error" role="alert">
          {approval.error}
        </p>
      )}
      <div className="card-actions">
        <button
          className="primary-button"
          disabled={busy || approval.canReview === false}
          onClick={onReview}
        >
          查看并确认
          <ArrowUpRight size={14} />
        </button>
        <button
          className="secondary-button"
          disabled={busy || approval.canDeny === false}
          onClick={onDeny}
        >
          这次不允许
        </button>
        {approval.canCancel && (
          <button className="text-button" disabled={busy} onClick={onCancel}>
            取消请求
          </button>
        )}
      </div>
    </section>
  );
}

export function QuestionCard({
  input,
  onAnswer,
  onCancel,
  busy,
}: {
  input: PendingInput;
  onAnswer: (answers: Answer[]) => void;
  onCancel: () => void;
  busy: boolean;
}) {
  const [answers, setAnswers] = useState<Record<string, Answer>>({});
  function choose(id: string, index: number, multiple: boolean) {
    setAnswers((prev) => {
      const old = prev[id] || {
        questionId: id,
        selectedOptionIndices: [],
        text: null,
      };
      return {
        ...prev,
        [id]: {
          ...old,
          selectedOptionIndices: multiple
            ? old.selectedOptionIndices.includes(index)
              ? old.selectedOptionIndices.filter((i) => i !== index)
              : [...old.selectedOptionIndices, index]
            : [index],
          ...(!multiple ? { text: null } : {}),
        },
      };
    });
  }
  const complete = input.questions.every(
    (q) =>
      answers[q.id]?.selectedOptionIndices.length ||
      answers[q.id]?.text?.trim(),
  );
  return (
    <section className="action-card question-card" aria-label="等待您的选择">
      <div className="card-heading">
        <span className="card-icon">✧</span>
        <div className="card-copy">
          <small>ONE LITTLE QUESTION</small>
          <strong>{input.title || "想先听听您的想法"}</strong>
        </div>
      </div>
      {input.questions.map((q) => (
        <fieldset key={q.id}>
          <legend>{q.prompt}</legend>
          <div className="question-options">
            {q.options.map((option, i) => (
              <button
                type="button"
                className={`choice-option ${answers[q.id]?.selectedOptionIndices.includes(i) ? "selected" : ""}`}
                key={`${q.id}-${i}`}
                aria-pressed={
                  answers[q.id]?.selectedOptionIndices.includes(i) || false
                }
                onClick={() => choose(q.id, i, !!q.multiSelect)}
                disabled={busy}
              >
                <span>
                  <strong>{option.label}</strong>
                  {option.description && <small>{option.description}</small>}
                </span>
                <span className="choice-indicator">
                  {answers[q.id]?.selectedOptionIndices.includes(i) && (
                    <Check size={12} />
                  )}
                </span>
              </button>
            ))}
          </div>
          {q.allowText !== false && (
            <input
              aria-label={`${q.label || q.prompt}：其他回答`}
              className="text-field"
              maxLength={12000}
              placeholder="也可以说说自己的想法…"
              value={answers[q.id]?.text || ""}
              onChange={(e) =>
                setAnswers((prev) => ({
                  ...prev,
                  [q.id]: {
                    questionId: q.id,
                    selectedOptionIndices: q.multiSelect
                      ? prev[q.id]?.selectedOptionIndices || []
                      : [],
                    text: e.target.value,
                  },
                }))
              }
              disabled={busy}
            />
          )}
        </fieldset>
      ))}
      {input.error && (
        <p className="inline-error" role="alert">
          {input.error}
        </p>
      )}
      <div className="card-actions">
        <button
          className="primary-button"
          disabled={!complete || busy || input.canSubmit === false}
          onClick={() => onAnswer(input.questions.map((q) => answers[q.id]))}
        >
          就这样继续
          <ArrowUpRight size={14} />
        </button>
        {input.canCancel !== false && (
          <button className="text-button" disabled={busy} onClick={onCancel}>
            先不选了
          </button>
        )}
      </div>
    </section>
  );
}


export function ResolvedInputCard({
  title,
  prompt,
  summary,
  options,
}: {
  title?: string;
  prompt: string;
  summary: string;
  options?: string[];
}) {
  const [expanded, setExpanded] = useState(false);
  return (
    <section className="action-card resolved-input-card" aria-label="已确认的回答">
      <button
        type="button"
        className="resolved-input-summary"
        aria-expanded={expanded}
        onClick={() => setExpanded(!expanded)}
      >
        <span className="resolved-check">
          <Check size={12} />
        </span>
        <span className="resolved-label">
          已确认：<strong>{summary}</strong>
        </span>
        <span className="resolved-toggle">
          <small>{expanded ? "收起提问" : "查看提问"}</small>
          <ChevronDown size={12} className={`resolved-chevron ${expanded ? "is-expanded" : ""}`} />
        </span>
      </button>
      {expanded && (
        <div className="resolved-input-detail">
          <p className="resolved-prompt">{prompt}</p>
          {options && options.length > 0 && (
            <div className="resolved-options-history">
              <small>备选：{options.join(" · ")}</small>
            </div>
          )}
        </div>
      )}
    </section>
  );
}

export function ResolvedApprovalCard({
  title,
  approved,
  detail,
}: {
  title: string;
  approved: boolean;
  detail?: string;
}) {
  const [expanded, setExpanded] = useState(false);
  return (
    <div className={`action-card resolved-approval-card ${approved ? "is-approved" : "is-denied"}`}>
      <div className="resolved-approval-row">
        <span className="resolved-approval-icon">
          {approved ? <Check size={12} /> : <X size={12} />}
        </span>
        <span className="resolved-approval-title">
          {approved ? "已授权执行：" : "已拒绝执行："}
          <strong>{title}</strong>
        </span>
        {detail && (
          <button
            type="button"
            className="text-button"
            onClick={() => setExpanded(!expanded)}
          >
            {expanded ? "收起详情" : "查看详情"}
          </button>
        )}
      </div>
      {expanded && detail && (
        <pre className="resolved-approval-detail">{detail}</pre>
      )}
    </div>
  );
}

export function Feedback({ message }: { message: string }) {
  return (
    <AnimatePresence>
      {message && (
        <motion.div
          className="toast"
          role="status"
          initial={{ opacity: 0, y: 8 }}
          animate={{ opacity: 1, y: 0 }}
          exit={{ opacity: 0, y: 4 }}
        >
          <Check size={15} />
          {message}
        </motion.div>
      )}
    </AnimatePresence>
  );
}
