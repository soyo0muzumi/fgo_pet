import React, {
  useCallback,
  useEffect,
  useLayoutEffect,
  useRef,
  useState,
  useSyncExternalStore,
} from "react";
import { createRoot } from "react-dom/client";
import {
  AnimatePresence,
  MotionConfig,
  motion,
  useReducedMotion,
} from "motion/react";
import {
  ArrowDown,
  ArrowUp,
  ArrowUpRight,
  Check,
  ChevronDown,
  ChevronUp,
  Folder,
  History,
  ImagePlus,
  ListTodo,
  Maximize2,
  Minimize2,
  Minus,
  MoreHorizontal,
  Pause,
  Play,
  Plus,
  RotateCcw,
  Square,
  Timer,
  X,
} from "lucide-react";
import { createNativeBridge } from "./nativeBridge";
import { createNativeDriver } from "./nativeDriver";
import {
  isRunning,
  needsAttention,
  type ActionName,
  type ChatDriver,
  type View,
} from "./domain";
import {
  ApprovalCard,
  Feedback,
  IconButton,
  MessageItem,
  QuestionCard,
  RunStatus,
  Sheet,
} from "./components";
import { UtilityPanels, type Panel } from "./UtilityPanels";
import { ModelMenu } from "./ModelMenu";
import { SpeechNotice } from "./SpeechNotice";
import { startCompactTurn, recentCompactTurn, reconcileCompactTurn, compactTurnMessages, type CompactTurnAnchor } from "./compactTurn";
import { useConversationScroll } from "./useConversationScroll";
import { createCompactHeightReporter, measureCompactContent } from "./compactHeight";
import "./styles.css";

export function App({ driver }: { driver: ChatDriver }) {
  const native = true;
  const state = useSyncExternalStore(
    driver.subscribe,
    driver.getSnapshot,
    driver.getSnapshot,
  );
  const theme = state.nativeTheme || "light";
  const [reduced, setReduced] = useState(false);
  const systemReduced = useReducedMotion();
  const [view, setView] = useState<View>("composer");
  const [wide, setWide] = useState(false);
  const [presentationEpoch, setPresentationEpoch] = useState(0);
  const [visible, setVisible] = useState(true);
  const [panel, setPanel] = useState<Panel>(null);
  const [modelMenuOpen, setModelMenuOpen] = useState(false);
  const [compactTurn, setCompactTurn] = useState<CompactTurnAnchor | null>(null);
  const currentCompactTurn = reconcileCompactTurn(compactTurn, state);
  const compactMessages = compactTurnMessages(currentCompactTurn, state);
  // The host can create an empty assistant turn while it is still waiting.
  // Keep one waiting indicator instead of reserving a second blank reply row.
  const visibleCompactMessages = compactMessages.filter(message => message.role !== "assistant"
    || message.text || message.reasoning || message.imageUrl || message.imageLabel
    || message.stopped || message.task || message.speechBusy);
  const compactReading = view === "composer" && (!!currentCompactTurn || !!state.approval || !!state.input);
  const readingOpen = view === "conversation" || compactReading;
  const showRunStatus = ["submitting", "waiting", "tool", "stopping"].includes(state.phase);
  const [toast, setToast] = useState("");
  const [busyAction, setBusyAction] = useState("");
  const [now, setNow] = useState(Date.now());
  const textarea = useRef<HTMLTextAreaElement>(null);
  const modelTrigger = useRef<HTMLButtonElement>(null);
  const surface = useRef<HTMLElement>(null);
  const heightReporter = useRef<ReturnType<typeof createCompactHeightReporter> | null>(null);
  const presentationIntent = useRef<{ view: View; wide: boolean } | null>(null);
  const presentationSequence = useRef(0);
  const currentView = useRef(view);
  currentView.current = view;
  const {
    scroller,
    content: messageContent,
    showLatest,
    capturePosition,
    resetFollow,
    latest,
  } = useConversationScroll({
    conversationId: state.conversationId,
    active: readingOpen && !modelMenuOpen && (visible || native),
  });
  const toastTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const notifyingRef = useRef<(text: string) => void>(() => {});
  const remaining = state.focus
    ? state.focus.endAt
      ? Math.min(
          state.focus.remaining,
          Math.max(0, Math.ceil((state.focus.endAt - now) / 1000)),
        )
      : state.focus.remaining
    : 0;
  const timerText = `${String(Math.floor(remaining / 60)).padStart(2, "0")}:${String(remaining % 60).padStart(2, "0")}`;
  const speechMessage = [...state.messages].reverse().find((message) => message.speechBusy);
  const lastReply = [...state.messages]
    .reverse()
    .find((m) => m.role === "assistant" && m.text)
    ?.text.replace(/[#*`>]/g, "")
    .replace(/\s+/g, " ");
  const status = state.approval
    ? "等您确认一下"
    : state.input
      ? "想听听您的选择"
      : speechMessage
        ? speechMessage.speechStatus || "语音处理中"
        : isRunning(state.phase)
          ? state.status
          : state.focus?.endAt
            ? "安静地陪着您"
            : "Here with you";

  const notify = useCallback((text: string) => {
    setToast(text);
    if (toastTimer.current) clearTimeout(toastTimer.current);
    toastTimer.current = setTimeout(() => setToast(""), 3400);
  }, []);
  notifyingRef.current = notify;
  useEffect(() => {
    if (currentCompactTurn !== compactTurn) setCompactTurn(currentCompactTurn);
  }, [currentCompactTurn, compactTurn]);
  const report = useCallback(
    (error: unknown) =>
      notify(
        error instanceof Error
          ? error.message
          : "这次操作没有完成，请再试一次。",
      ),
    [notify],
  );
  useEffect(() => {
    void driver
      .connect()
      .catch((error) =>
        notifyingRef.current(
          error instanceof Error ? error.message : "暂时无法连接桌面宿主。",
        ),
      );
    return () => {
      driver.dispose();
      if (toastTimer.current) clearTimeout(toastTimer.current);
    };
  }, [driver]);
  useEffect(() => {
    if (native && state.hostPresentation) {
      const desired = presentationIntent.current;
      if (desired && (desired.view !== state.hostPresentation.view || desired.wide !== state.hostPresentation.wide))
        return;
      presentationIntent.current = null;
      capturePosition();
      currentView.current = state.hostPresentation.view;
      setView(state.hostPresentation.view);
      setWide(state.hostPresentation.wide);
    }
  }, [native, state.hostPresentation?.view, state.hostPresentation?.wide, capturePosition]);
  useEffect(() => {
    // Legacy hosts have no compact-presentation contract. Keep pending requests
    // fully visible there, after the native handshake has completed.
    if (state.ready && !state.hostPresentation && (state.approval || state.input)) {
      currentView.current = "conversation";
      setView("conversation");
    }
  }, [state.ready, state.hostPresentation, state.approval?.requestId, state.input?.requestId]);
  useEffect(() => {
    if (!native) return;
    if (state.visibility) {
      setVisible(true);
    } else if (state.visibility === false) setVisible(false);
  }, [native, state.visibility, driver, report]);
  useLayoutEffect(() => {
    const el = textarea.current;
    if (!el) return;
    el.style.height = "auto";
    el.style.height = `${Math.min(Math.max(el.scrollHeight, 30), 112)}px`;
  }, [state.draft, view, visible]);
  useEffect(() => {
    if (!state.focus?.endAt) return;
    const id = setInterval(() => setNow(Date.now()), 500);
    return () => clearInterval(id);
  }, [state.focus?.endAt]);
  useEffect(() => {
    if (state.focus?.endAt && remaining === 0)
      void driver.action("focusFinish").catch(report);
  }, [remaining, state.focus?.endAt, driver, report]);
  useLayoutEffect(() => {
    if (!native || !state.ready || view !== "composer" || !surface.current)
      return;
    const element = surface.current;
    const reporter = createCompactHeightReporter(height => {
      if (currentView.current !== "composer") return Promise.resolve();
      return driver.presentation("composer", wide, height);
    }, report);
    heightReporter.current = reporter;
    const measure = () => reporter.measure(measureCompactContent(element));
    const observer = new ResizeObserver(measure);
    observer.observe(element);
    for (const child of element.querySelectorAll<HTMLElement>(".peek-header,.chat-footer,.messages,.compact-reading-heading"))
      observer.observe(child);
    measure();
    return () => {
      observer.disconnect();
      reporter.dispose();
      if (heightReporter.current === reporter) heightReporter.current = null;
    };
  }, [native, state.ready, view, wide, compactReading, modelMenuOpen, presentationEpoch, driver, report]);

  async function act(name: ActionName, payload: Record<string, unknown> = {}) {
    setBusyAction(name);
    try {
      await driver.action(name, payload);
      if (name === "copy") notify("已复制");
      if (name === "taskSave") notify("已记下 · 可以在 Tasks 中找到");
      return true;
    } catch (error) {
      report(error);
      return false;
    } finally {
      setBusyAction("");
    }
  }
  function present(next: View, nextWide = wide) {
    heightReporter.current?.dispose();
    heightReporter.current = null;
    currentView.current = next;
    presentationIntent.current = { view: next, wide: nextWide };
    const sequence = ++presentationSequence.current;
    setPresentationEpoch(sequence);
    capturePosition();
    setModelMenuOpen(false);
    setView(next);
    setWide(nextWide);
    setVisible(true);
    if (native) void driver.presentation(next, nextWide).then(() => {
      const actual = driver.getSnapshot().hostPresentation;
      if (sequence === presentationSequence.current && actual?.view === next && actual.wide === nextWide)
        presentationIntent.current = null;
    }).catch(error => {
      if (sequence === presentationSequence.current) presentationIntent.current = null;
      report(error);
    });
  }
  function hide() {
    capturePosition();
    setVisible(false);
    setPanel(null);
    setModelMenuOpen(false);
    if (native) void driver.hide().catch(report);
  }
  function openPanel(next: Panel) {
    setModelMenuOpen(false);
    if (next === "history") setCompactTurn(null);
    present("conversation");
    setPanel(next);
    if (next === "history")
      void act("historyLoad", {
        onlyCurrentProject: state.historyOnlyProject || false,
        append: false,
      });
  }
  function closeModelMenu(restoreFocus = false) {
    setModelMenuOpen(false);
    if (restoreFocus) requestAnimationFrame(() => modelTrigger.current?.focus({ preventScroll: true }));
  }
  function collapseConversation() {
    setCompactTurn(null);
    setPanel(null);
    present("composer");
  }
  function returnToTurn() {
    const snapshot = driver.getSnapshot();
    const pending = reconcileCompactTurn(compactTurn, snapshot);
    // A just-submitted message may not yet be acknowledged. Keep that pending
    // boundary instead of replacing it with an older turn during the return.
    setCompactTurn(pending?.userId === null ? pending : recentCompactTurn(snapshot));
    setPanel(null);
    present("composer");
  }
  function attach() {
    void act("chooseImage");
  }
  async function send() {
    if (!state.draft.trim() || !state.canSend) return;
    resetFollow();
    setCompactTurn(startCompactTurn(state));
    setModelMenuOpen(false);
    setPanel(null);
    try {
      await driver.send(state.draft);
    } catch (error) {
      setCompactTurn(current => compactTurnMessages(current, driver.getSnapshot()).length ? current : null);
      report(error);
    }
    textarea.current?.focus({ preventScroll: true });
  }
  async function newConversation() {
    if (!state.actions.newConversation) return;
    try {
      await driver.newConversation();
      setCompactTurn(null);
      setPanel(null);
      resetFollow();
      present("composer");
      requestAnimationFrame(() => textarea.current?.focus({ preventScroll: true }));
    } catch (error) {
      report(error);
    }
  }
  useEffect(() => {
    function key(event: KeyboardEvent) {
      if (
        event.key === "Escape" &&
        !event.isComposing &&
        !event.defaultPrevented &&
        !panel &&
        !modelMenuOpen
      ) {
        event.preventDefault();
        if (native || visible) hide();
      }
    }
    window.addEventListener("keydown", key);
    return () => window.removeEventListener("keydown", key);
  });

  return (
    <MotionConfig
      reducedMotion={reduced ? "always" : "user"}
      transition={{ duration: 0.22, ease: [0.22, 1, 0.36, 1] }}
    >
      <div
        className="app"
        data-theme={theme}
        data-native={native}
        data-reduced={reduced || systemReduced ? "true" : "false"}
        data-view={view}
      >
        <main
          className="desktop-stage"
          aria-label="桌面聊天"
        >
          <AnimatePresence initial={false}>
            {(visible || native) && (
              <motion.div
                className="chat-anchor"
                data-view={view}
                data-compact={compactReading}
                data-model-menu={modelMenuOpen}
                data-wide={view === "conversation" && wide ? "true" : "false"}
                initial={{ opacity: 0, y: 10, scale: 0.985 }}
                animate={{ opacity: 1, y: 0, scale: 1 }}
                exit={{ opacity: 0, y: 8, scale: 0.985 }}
              >
                <motion.section
                  ref={surface}
                  layout={!native ? "size" : false}
                  className="chat-surface"
                  aria-label={`与 ${state.roleName || "Mash"} 对话`}
                  onDragOver={(e) => {
                    if (e.dataTransfer.types.includes("Files"))
                      e.preventDefault();
                  }}
                  onDrop={(e) => {
                    if (e.dataTransfer.files.length) {
                      e.preventDefault();
                      void driver
                        .importImage(e.dataTransfer.files[0])
                        .catch(report);
                    }
                  }}
                >
                  {view === "composer" ? (
                    <header className="peek-header" inert={!!panel}>
                      <span className="peek-avatar">
                        {state.avatarUrl ? <img src={state.avatarUrl} alt="" /> : <span aria-hidden="true">{(state.roleName || "M").slice(0, 1)}</span>}
                      </span>
                      <div className="peek-copy">
                        <strong>
                          {state.roleName || "Mash"}
                          <span>Here with you</span>
                        </strong>
                        <small>
                          {compactReading
                            ? state.approval ? "有一件事想先问问您。" : state.input ? "想听听您的选择。" : isRunning(state.phase) ? status : "可以接着聊，也可以展开看看。"
                            : lastReply?.slice(0, 48) || "今天有什么想和我说的？"}
                        </small>
                      </div>
                      {compactReading ? (
                        <IconButton label="收成输入框" onClick={collapseConversation}>
                          <Minus size={16} />
                        </IconButton>
                      ) : (
                        <IconButton label="展开完整对话" onClick={() => present("conversation")}>
                          <ChevronUp size={17} />
                        </IconButton>
                      )}
                      <IconButton label="隐藏聊天窗口" onClick={hide}>
                        <X size={15} />
                      </IconButton>
                    </header>
                  ) : (
                    <header className="chat-header" inert={!!panel || modelMenuOpen && view === "conversation"}>
                      <div className="chat-identity">
                        <span className="avatar">
                          {state.avatarUrl ? <img src={state.avatarUrl} alt="" /> : <span aria-hidden="true">{(state.roleName || "M").slice(0, 1)}</span>}
                        </span>
                        <div>
                          <h2>
                            {state.roleName || "Mash"}
                            {(!state.roleName || state.roleName === "Mash") && (
                              <span>マシュ</span>
                            )}
                          </h2>
                          <p>
                            <span
                              className={`pet-status ${isRunning(state.phase) ? "active" : ""}`}
                            />
                            {status}
                          </p>
                        </div>
                      </div>
                      <div className="chat-actions">
                        <IconButton
                          label="对话历史"
                          onClick={() => openPanel("history")}
                        >
                          <History size={16} />
                        </IconButton>
                        <IconButton
                          label={wide ? "恢复紧凑宽度" : "展开阅读宽度"}
                          onClick={() => present("conversation", !wide)}
                        >
                          {wide ? (
                            <Minimize2 size={16} />
                          ) : (
                            <Maximize2 size={16} />
                          )}
                        </IconButton>
                        <button type="button" className="chat-return" onClick={returnToTurn}>
                          <ChevronDown size={13} />
                          {currentCompactTurn || state.messages.some(message => message.role === "user") ? "回到本轮" : "轻输入"}
                        </button>
                        <IconButton label="隐藏聊天窗口" onClick={hide}>
                          <X size={16} />
                        </IconButton>
                      </div>
                    </header>
                  )}
                  {readingOpen && (
                    <div className={`conversation-body ${compactReading ? "compact-conversation" : ""}`} hidden={compactReading && modelMenuOpen} inert={!!panel || modelMenuOpen && view === "conversation"}>
                      {compactReading && <div className="compact-reading-heading"><span>本轮对话</span><button type="button" onClick={() => present("conversation")}>展开阅读<ChevronUp size={12} /></button></div>}
                      <div
                        id="message-scroll"
                        className="message-scroll"
                        ref={scroller}
                        tabIndex={0}
                        aria-label={compactReading ? "本轮对话" : "对话阅读区"}
                      >
                        {!compactReading && !state.messages.length &&
                          state.phase === "idle" &&
                          !state.connectionError && (
                            <div className="empty-conversation">
                              <span>✧</span>
                              <h3>从一句话开始。</h3>
                              <p>不必想好话题。今天的小事，也可以。</p>
                              <button
                                onClick={() => {
                                  driver.setDraft(
                                    "今天有点累，陪我聊一会儿吧。",
                                  );
                                  textarea.current?.focus({ preventScroll: true });
                                }}
                              >
                                陪我聊一会儿
                                <ArrowUpRight size={13} />
                              </button>
                            </div>
                          )}
                        <div
                          id="messages"
                          className="messages"
                          ref={messageContent}
                          role="log"
                          aria-label="对话消息"
                          aria-live="off"
                        >
                          {(compactReading ? visibleCompactMessages : state.messages).map((message) => (
                            <MessageItem
                              key={message.id}
                              message={message}
                              roleName={state.roleName}
                              onSpeechSettings={() =>
                                void act("openSpeechSettings")
                              }
                              onCopy={() =>
                                void act("copy", { id: message.id })
                              }
                              onRead={() =>
                                void act("readAloud", { id: message.id })
                              }
                              onWorkspace={() =>
                                void act("openWorkspace", { id: message.id })
                              }
                              onSaveTask={() =>
                                void act("taskSave", { id: message.id })
                              }
                            />
                          ))}
                          {compactReading && !state.error && !isRunning(state.phase) && !compactMessages.length && <RunStatus phase="submitting" status="正在发送这句话" />}
                          {compactReading ? (
                            showRunStatus && <RunStatus phase={state.phase} status={state.status} detail={state.detail} />
                          ) : <AnimatePresence>
                            {showRunStatus && (
                              <RunStatus
                                key="run-status"
                                phase={state.phase}
                                status={state.status}
                                detail={state.detail}
                              />
                            )}
                          </AnimatePresence>}
                          {compactReading && (state.approval || state.input) && <section className="compact-attention"><span>{state.approval ? "有一项操作需要您确认" : "想听听您的选择"}</span><button type="button" onClick={() => present("conversation")}>{state.approval ? "查看并确认" : "展开回答"}<ArrowUpRight size={12} /></button></section>}
                          {view === "conversation" && state.approval && (
                            <ApprovalCard
                              approval={state.approval}
                              busy={!!busyAction || !!state.approval.busy}
                              onReview={() =>
                                native
                                  ? void act("approvalReview", {
                                      ...state.approval,
                                    })
                                  : setPanel("approval-review")
                              }
                              onDeny={() =>
                                void act("approvalDeny", { ...state.approval })
                              }
                              onCancel={() =>
                                void act("approvalCancel", {
                                  ...state.approval,
                                })
                              }
                            />
                          )}
                          {view === "conversation" && state.input && (
                            <QuestionCard
                              key={`${state.input.requestId}-${state.input.revision}`}
                              input={state.input}
                              busy={!!busyAction || !!state.input.busy}
                              onAnswer={(answers) =>
                                void act("inputAnswer", {
                                  runId: state.input!.runId,
                                  requestId: state.input!.requestId,
                                  revision: state.input!.revision,
                                  answers,
                                })
                              }
                              onCancel={() =>
                                void act("inputCancel", { ...state.input })
                              }
                            />
                          )}
                          {(state.error || state.connectionError) && (
                            <section className="error-card" role="alert">
                              <div>
                                <span>!</span>
                                <strong>
                                  {state.connectionError
                                    ? "还没有连上桌面会话"
                                    : "这次没有顺利完成"}
                                </strong>
                              </div>
                              <p>{state.connectionError || state.error}</p>
                              <div className="card-actions">
                                {state.connectionError ? (
                                  <button
                                    className="secondary-button"
                                    onClick={() =>
                                      void driver.connect().catch(report)
                                    }
                                  >
                                    <RotateCcw size={14} />
                                    重新连接
                                  </button>
                                ) : (
                                  <button
                                    className="secondary-button"
                                    onClick={() => {
                                      const last = [...state.messages]
                                        .reverse()
                                        .find((m) => m.role === "user");
                                      if (last && !state.draft)
                                        driver.setDraft(last.text);
                                      textarea.current?.focus({ preventScroll: true });
                                      notify(
                                        "刚才的消息已保留，可以编辑后再发送",
                                      );
                                    }}
                                  >
                                    <RotateCcw size={14} />
                                    回到输入框
                                  </button>
                                )}
                                {state.canRetryCapability && (
                                  <button
                                    className="text-button"
                                    onClick={() => void act("retryCapability")}
                                  >
                                    重试模型能力
                                  </button>
                                )}
                              </div>
                            </section>
                          )}
                          {state.capabilityNotice && (
                            <p className="capability-note">
                              {state.capabilityNotice.text}
                              {state.configurationRequired && (
                                <button
                                  className="text-button"
                                  onClick={() => void act("openModelSettings")}
                                >
                                  打开模型设置
                                </button>
                              )}
                            </p>
                          )}
                          {view === "conversation" && !!state.recalledSources?.length && (
                            <details className="source-list">
                              <summary>
                                参考了 {state.recalledSources.length} 条资料
                              </summary>
                              {state.recalledSources.map((source) => (
                                <button
                                  key={source.id}
                                  onClick={() =>
                                    void act("openSource", { id: source.id })
                                  }
                                >
                                  <Folder size={13} />
                                  <span>
                                    {source.title}
                                    <small>{source.detail}</small>
                                  </span>
                                  <ArrowUpRight size={12} />
                                </button>
                              ))}
                            </details>
                          )}
                        </div>
                      </div>
                      {showLatest && (
                        <button className="scroll-latest" onClick={latest}>
                          <ArrowDown size={13} />
                          回到最新
                        </button>
                      )}
                    </div>
                  )}
                  <footer className="chat-footer" inert={!!panel || modelMenuOpen && view === "conversation"}>
                    {modelMenuOpen && view === "composer" && <ModelMenu
                      id="chat-model-menu" models={state.models} triggerRef={modelTrigger}
                      canManage={!native || state.availableActions?.openModelSettings === true}
                      status={native ? state.providerStatus : undefined}
                      onSelect={id => act("model", { id })} onClose={closeModelMenu}
                      onManage={() => void act("openModelSettings")}
                    />}
                    {speechMessage && <div className="composer-speech">
                      <SpeechNotice message={speechMessage} disabled={busyAction === "readAloud"}
                        onStop={() => void act("readAloud", { id: speechMessage.id })} />
                    </div>}
                    {view === "composer" && !compactReading && !modelMenuOpen && needsAttention(state) && !state.connectionError && <div className="compact-resume"><span>{state.approval ? "有一项操作需要确认" : state.input ? "有一个问题等您回答" : state.error ? "这次回复遇到了问题" : "正在处理这次回复"}</span><button type="button" onClick={() => present("conversation")}>查看<ArrowUpRight size={12} /></button></div>}
                    {view === "composer" && state.connectionError && (
                      <div className="connection-notice" role="alert">
                        <p>{state.connectionError}</p>
                        <button
                          className="text-button"
                          onClick={() => void driver.connect().catch(report)}
                        >
                          <RotateCcw size={13} />
                          重新连接
                        </button>
                      </div>
                    )}
                    {state.focus && (
                      <div className="focus-strip">
                        <Timer size={13} />
                        <button onClick={() => openPanel("focus")}>
                          {state.focus.endAt ? "一起专注中" : "专注已暂停"}
                          <strong>{timerText}</strong>
                        </button>
                        <IconButton
                          label={state.focus.endAt ? "暂停专注" : "继续专注"}
                          onClick={() => void act("focusToggle")}
                        >
                          {state.focus.endAt ? (
                            <Pause size={13} />
                          ) : (
                            <Play size={13} />
                          )}
                        </IconButton>
                        <button onClick={() => void act("focusFinish")}>
                          结束
                        </button>
                      </div>
                    )}
                    {(state.contextChips.length > 0 || state.image) && (
                      <div className="attachment-strip">
                        {state.contextChips.map((chip) => (
                          <span className="context-chip" key={chip.id}>
                            <Folder size={12} />
                            {chip.label}
                            {chip.removable !== false && (
                              <button
                                aria-label={`移除上下文：${chip.label}`}
                                onClick={() =>
                                  void act("removeContextChip", { id: chip.id })
                                }
                              >
                                <X size={12} />
                              </button>
                            )}
                          </span>
                        ))}
                        {state.image && (
                          <span className="image-chip" title={state.imageDisclosure}>
                            {state.image.previewUrl ? (
                              <img
                                src={state.image.previewUrl}
                                alt="已选择的图片"
                              />
                            ) : (
                              <ImagePlus size={14} />
                            )}
                            <span>
                              {state.image.label}
                              {state.image.status && (
                                <small>{state.image.status}</small>
                              )}
                            </span>
                            <IconButton
                              label={
                                state.image.pending
                                  ? "取消图片传输"
                                  : "移除图片"
                              }
                              onClick={() =>
                                void act(
                                  state.image?.pending
                                    ? "cancelImage"
                                    : "clearImage",
                                )
                              }
                            >
                              <X size={12} />
                            </IconButton>
                          </span>
                        )}
                      </div>
                    )}
                    <form
                      className="composer"
                      onSubmit={(e) => {
                        e.preventDefault();
                        void send();
                      }}
                    >
                      <div className="composer-input-wrap">
                        <textarea
                          id="composer"
                          ref={textarea}
                          aria-label="消息内容"
                          placeholder={
                            isRunning(state.phase)
                              ? "也可以先写好下一句…"
                              : "今天有什么想和我说的？"
                          }
                          value={state.draft}
                          maxLength={12000}
                          rows={1}
                          onChange={(e) => driver.setDraft(e.target.value)}
                          onKeyDown={(e) => {
                            if (
                              e.key === "Enter" &&
                              !e.shiftKey &&
                              !e.nativeEvent.isComposing &&
                              e.keyCode !== 229
                            ) {
                              e.preventDefault();
                              void send();
                            }
                          }}
                          onPaste={(e) => {
                            const image = [...e.clipboardData.items]
                              .find(
                                (item) =>
                                  item.type === "image/png" ||
                                  item.type === "image/jpeg",
                              )
                              ?.getAsFile();
                            if (image) {
                              e.preventDefault();
                              void driver.importImage(image).catch(report);
                            }
                          }}
                        />
                      </div>
                      <div className="composer-bottom">
                        <div className="composer-tools">
                          <IconButton
                            label="添加图片与上下文"
                            disabled={!state.ready}
                            onClick={() => openPanel("context")}
                          >
                            <Plus size={18} />
                          </IconButton>
                          <button
                            ref={modelTrigger}
                            type="button"
                            className="model-chip"
                            disabled={!state.actions.model && state.availableActions?.refreshModels !== true && state.availableActions?.openModelSettings !== true}
                            aria-haspopup={view === "conversation" ? "dialog" : "menu"}
                            aria-expanded={modelMenuOpen}
                            aria-controls={modelMenuOpen && view === "composer" ? "chat-model-menu" : undefined}
                            onClick={() => {
                              capturePosition();
                              setModelMenuOpen(open => !open);
                              if (!modelMenuOpen && state.availableActions?.refreshModels === true)
                                void act("refreshModels");
                            }}
                          >
                            {state.modelLabel}
                            <ChevronDown size={12} />
                          </button>
                        </div>
                        <div className="composer-tools tools-right">
                          <IconButton
                            label="打开待办面板"
                            onClick={() => openPanel("tasks")}
                          >
                            <ListTodo size={16} />
                          </IconButton>
                          <IconButton
                            label="打开专注面板"
                            onClick={() => openPanel("focus")}
                          >
                            <Timer size={16} />
                          </IconButton>
                          {state.canStop ||
                          isRunning(state.phase) ||
                          state.approval ||
                          state.input ? (
                            <button
                              type="button"
                              className="send-button stop-button"
                              aria-label="停止回复"
                              disabled={
                                !state.canStop || state.phase === "stopping"
                              }
                              onClick={() => void driver.stop().catch(report)}
                            >
                              <Square size={13} fill="currentColor" />
                            </button>
                          ) : (
                            <button
                              type="submit"
                              className="send-button"
                              aria-label="发送消息"
                              disabled={!state.draft.trim() || !state.canSend}
                            >
                              <ArrowUp size={19} />
                            </button>
                          )}
                        </div>
                      </div>
                    </form>
                    {view === "conversation" && (
                      <div className="composer-meta">
                        <button onClick={() => openPanel("context")}>
                          <Folder size={11} />
                          {state.projectLabel}
                        </button>
                        <span>Shift + Enter 换行</span>
                        <IconButton
                          label="界面与更多设置"
                          onClick={() => openPanel("settings")}
                        >
                          <MoreHorizontal size={16} />
                        </IconButton>
                      </div>
                    )}
                    {state.compatibilityWarning && (
                      <p className="compatibility-note">
                        {state.compatibilityWarning}
                      </p>
                    )}
                  </footer>
                  <AnimatePresence>
                    {modelMenuOpen && view === "conversation" && <Sheet key="model-picker" title="选择对话模型" onClose={() => closeModelMenu(true)}>
                      <div className="choice-list">
                        {state.models.map(model => <button key={model.id} className="choice-option"
                          aria-pressed={!!model.selected} disabled={!!busyAction || model.disabled}
                          onClick={() => { void act("model", { id: model.id }).then(ok => { if (ok) closeModelMenu(true); }); }}>
                          <span>{model.label}</span>
                          {model.selected && <Check size={16} />}
                        </button>)}
                      </div>
                      <button className="text-button" disabled={state.availableActions?.openModelSettings !== true}
                        onClick={() => { closeModelMenu(false); void act("openModelSettings"); }}>管理模型与连接</button>
                      {state.providerStatus && <p className="panel-note">{state.providerStatus}</p>}
                    </Sheet>}
                    <UtilityPanels
                      key={panel}
                      panel={panel}
                      state={state}
                      driver={driver}
                      onClose={() => setPanel(null)}
                      act={act}
                      onNew={() => void newConversation()}
                      theme={theme}
                      onTheme={() =>
                        void act("openPersonalizationSettings")
                      }
                      reduced={reduced}
                      onReduced={() => setReduced(!reduced)}
                      remaining={remaining}
                      notify={notify}
                      onAttach={attach}
                    />
                  </AnimatePresence>
                </motion.section>
              </motion.div>
            )}
          </AnimatePresence>
        </main>
        <Feedback message={toast} />
        <span className="visually-hidden" role="status" aria-live="polite">
          {state.phase === "idle" && state.messages.length > 1
            ? "回复已完成"
            : state.phase === "streaming"
              ? "正在生成回复"
              : state.status}
        </span>
      </div>
    </MotionConfig>
  );
}

class ErrorBoundary extends React.Component<
  { children: React.ReactNode },
  { error: boolean }
> {
  state = { error: false };
  static getDerivedStateFromError() {
    return { error: true };
  }
  render() {
    return this.state.error ? (
      <main className="startup-error">
        <h1>界面暂时没有加载完成。</h1>
        <p>会话仍由桌面宿主管理。可以重新打开聊天窗口。</p>
        <button onClick={() => location.reload()}>重新加载界面</button>
      </main>
    ) : (
      this.props.children
    );
  }
}
const root = createRoot(document.getElementById("root")!);
try {
  const bridge = createNativeBridge();
  if (!bridge) throw new Error("CHAT_BRIDGE_UNAVAILABLE");
  const driver = createNativeDriver(bridge);
  root.render(
    <ErrorBoundary>
      <App driver={driver} />
    </ErrorBoundary>,
  );
  if (import.meta.hot) import.meta.hot.dispose(() => driver.dispose());
} catch {
  root.render(
    <main className="startup-error" role="alert">
      <h1>桌面通信接口尚未就绪。</h1>
      <p>请在桌面应用内重新打开聊天窗口。</p>
      <button onClick={() => location.reload()}>重新加载界面</button>
    </main>,
  );
}
