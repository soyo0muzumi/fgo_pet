import { useState } from "react";
import {
  ArrowUp,
  ArrowUpRight,
  Check,
  ChevronRight,
  Clock3,
  Coffee,
  Download,
  Folder,
  FolderOpen,
  ImagePlus,
  MessageCircle,
  Moon,
  Pause,
  Play,
  Plus,
  Search,
  Settings2,
  ShieldCheck,
  Sun,
  Timer,
  Trash2,
  Volume2,
  X,
} from "lucide-react";
import type { ActionName, ChatDriver, UiState } from "./domain";
import { todayTasks } from "./domain";
import { IconButton, Sheet } from "./components";

export type Panel =
  | "tasks"
  | "focus"
  | "history"
  | "settings"
  | "context"
  | "about"
  | "approval-review"
  | null;
type Props = {
  panel: Panel;
  state: UiState;
  driver: ChatDriver;
  onClose: () => void;
  act: (name: ActionName, payload?: Record<string, unknown>) => Promise<boolean>;
  onNew: () => void;
  theme: "light" | "dark";
  onTheme: () => void;
  reduced: boolean;
  onReduced: () => void;
  remaining: number;
  notify: (s: string) => void;
  onAttach: () => void;
};
const time = (seconds: number) =>
  `${String(Math.floor(seconds / 60)).padStart(2, "0")}:${String(seconds % 60).padStart(2, "0")}`;

export function UtilityPanels({
  panel,
  state,
  driver,
  onClose,
  act,
  onNew,
  theme,
  onTheme,
  reduced,
  onReduced,
  remaining,
  notify,
  onAttach,
}: Props) {
  const [taskTab, setTaskTab] = useState<"today" | "all" | "done">("today");
  const [taskDraft, setTaskDraft] = useState("");
  const [search, setSearch] = useState("");
  const [minutes, setMinutes] = useState(25);
  const native = driver.kind === "native";
  const available = (name: ActionName) =>
    native
      ? state.availableActions?.[name] === true
      : state.availableActions?.[name] !== false;
  if (!panel) return null;
  if (panel === "tasks") {
    const tasks =
      taskTab === "today"
        ? todayTasks(state.tasks)
        : state.tasks.filter((t) => (taskTab === "done" ? t.done : !t.done));
    return (
      <Sheet title="Little things." kicker="TASKS" onClose={onClose}>
        {native ? (
          <>
            <p className="panel-note">
              待办由桌面内核管理。您可以在对话里查看和整理，也可以打开工作区。
            </p>
            <button
              className="primary-button full-width"
              disabled={!available("openWorkspaceOverview")}
              onClick={() => {
                void act("openWorkspaceOverview");
                onClose();
              }}
            >
              <FolderOpen size={16} />
              打开工作区
              <ArrowUpRight size={15} />
            </button>
            <button
              className="secondary-button full-width"
              disabled={!available("newWorkspaceItem")}
              onClick={() => {
                void act("newWorkspaceItem");
                onClose();
              }}
            >
              <Plus size={16} />
              新建工作项
              <ArrowUpRight size={15} />
            </button>
            <button
              className="secondary-button full-width"
              onClick={() => {
                driver.setDraft("帮我看看今天有哪些待办。");
                onClose();
              }}
            >
              在对话里查看今天的待办
              <MessageCircle size={15} />
            </button>
          </>
        ) : (
          <>
            <p className="panel-note">记在这里，就不必一直放在心上。</p>
            <div className="task-tabs" role="tablist" aria-label="待办范围">
              {(
                [
                  ["today", "Today"],
                  ["all", "All"],
                  ["done", "Done"],
                ] as const
              ).map(([key, label]) => (
                <button
                  key={key}
                  role="tab"
                  aria-selected={taskTab === key}
                  className={taskTab === key ? "selected" : ""}
                  onClick={() => setTaskTab(key)}
                >
                  {label}
                  {key === "today" && (
                    <span>{todayTasks(state.tasks).length}</span>
                  )}
                </button>
              ))}
            </div>
            <div className="task-list">
              {tasks.map((task) => (
                <div
                  className={`task-row ${task.done ? "done" : ""}`}
                  key={task.id}
                >
                  <button
                    className="task-check"
                    aria-label={`${task.done ? "恢复" : "完成"}待办：${task.title}`}
                    aria-pressed={task.done}
                    onClick={() => void act("taskToggle", { id: task.id })}
                  >
                    {task.done && <Check size={13} />}
                  </button>
                  <div className="task-info">
                    <strong>{task.title}</strong>
                    <small>
                      {task.inProgress ? "进行中 · " : ""}
                      {task.due === "today"
                        ? "今天到期"
                        : task.due === "tomorrow"
                          ? "明天到期"
                          : task.due === "overdue"
                            ? "已逾期"
                            : task.createdToday
                              ? "今天记下"
                              : "未设日期"}
                    </small>
                  </div>
                  <IconButton
                    label={`删除待办：${task.title}`}
                    onClick={() => void act("taskDelete", { id: task.id })}
                  >
                    <Trash2 size={14} />
                  </IconButton>
                </div>
              ))}
              {!tasks.length && (
                <div className="empty-state">
                  <Check size={26} />
                  <p>这一栏暂时没有待办。</p>
                  <span>也可以在聊天时告诉 Mash。</span>
                </div>
              )}
            </div>
            {taskTab === "today" && (
              <p className="rule-note">
                Today 包含进行中、今天到期、逾期，以及今天新建的无日期待办。
              </p>
            )}
            <form
              className="task-add"
              onSubmit={(e) => {
                e.preventDefault();
                if (taskDraft.trim()) {
                  void act("taskAdd", { title: taskDraft }).then((ok) => {
                    if (ok) {
                      setTaskDraft("");
                      notify("已经记下了");
                    }
                  });
                }
              }}
            >
              <Plus size={16} />
              <input
                aria-label="新的待办"
                placeholder="再记下一件小事…"
                value={taskDraft}
                maxLength={180}
                onChange={(e) => setTaskDraft(e.target.value)}
              />
              <button aria-label="添加待办" disabled={!taskDraft.trim()}>
                <ArrowUp size={16} />
              </button>
            </form>
          </>
        )}
      </Sheet>
    );
  }
  if (panel === "focus")
    return (
      <Sheet title="One small step." kicker="FOCUS TOGETHER" onClose={onClose}>
        <p className="panel-note">先留一小段时间，我陪您。</p>
        {native ? (
          <>
            <div className="focus-hero">
              <Timer size={32} />
              <span>Stay with one thing.</span>
            </div>
            <button
              className="primary-button full-width"
              disabled={!available("openFocus")}
              onClick={() => {
                void act("openFocus");
                onClose();
              }}
            >
              打开专注计时
              <Timer size={16} />
            </button>
            <p className="rule-note">计时与记录继续由桌面专注模块保存。</p>
          </>
        ) : (
          <>
            <div className="focus-hero">
              <span className="focus-orbit" />
              <Timer size={30} strokeWidth={1.3} />
              <span>✧</span>
            </div>
            {state.focus ? (
              <>
                <div className="timer-value">{time(remaining)}</div>
                <p className="timer-caption">
                  {state.focus.endAt ? "正在一起专注" : "停一停，也没关系"}
                </p>
                <div className="focus-actions">
                  <button
                    className="secondary-button"
                    onClick={() => void act("focusToggle")}
                  >
                    {state.focus.endAt ? (
                      <Pause size={15} />
                    ) : (
                      <Play size={15} />
                    )}
                    {state.focus.endAt ? "暂停一下" : "继续专注"}
                  </button>
                  <button
                    className="primary-button"
                    onClick={() => {
                      void act("focusFinish");
                      onClose();
                    }}
                  >
                    完成这一段
                    <Check size={15} />
                  </button>
                </div>
              </>
            ) : (
              <>
                <div className="duration-options" aria-label="专注时长">
                  {[10, 25, 50].map((value) => (
                    <button
                      key={value}
                      aria-pressed={minutes === value}
                      className={minutes === value ? "selected" : ""}
                      onClick={() => setMinutes(value)}
                    >
                      <strong>{value}</strong>
                      <span>MINUTES</span>
                    </button>
                  ))}
                </div>
                <button
                  className="primary-button full-width"
                  onClick={() => {
                    void act("focusStart", { minutes });
                    onClose();
                    notify(`${minutes} 分钟，从这一小步开始`);
                  }}
                >
                  <Play size={14} fill="currentColor" />
                  开始这段专注
                </button>
              </>
            )}
            <p className="focus-footnote">
              <Coffee size={13} />
              结束后，再一起休息一下。
            </p>
          </>
        )}
      </Sheet>
    );
  if (panel === "history")
    return (
      <Sheet title="Our conversations." kicker="HISTORY" onClose={onClose}>
        <button
          className="new-conversation-button"
          disabled={!state.actions.newConversation}
          onClick={onNew}
        >
          <Plus size={17} />
          开始一段新对话
          <ArrowUpRight size={15} />
        </button>
        <label className="search-field">
          <Search size={15} />
          <input
            aria-label="搜索对话"
            placeholder="找找之前聊过的事…"
            value={search}
            onChange={(e) => setSearch(e.target.value)}
          />
        </label>
        {native && (
          <label className="checkbox-field">
            <input
              type="checkbox"
              checked={!!state.historyOnlyProject}
              onChange={(e) =>
                void act("historyLoad", {
                  onlyCurrentProject: e.target.checked,
                  append: false,
                })
              }
            />
            仅当前项目
          </label>
        )}
        <div className="history-list">
          <div className="history-current">
            <MessageCircle size={16} />
            <span>
              {state.messages
                .find((m) => m.role === "user")
                ?.text.slice(0, 32) || "A quiet moment"}
            </span>
            <small>当前</small>
          </div>
          {state.history
            .filter(
              (h) =>
                h.id !== state.conversationId &&
                h.title.toLowerCase().includes(search.toLowerCase()),
            )
            .map((h) => (
              <div className="history-item" key={h.id}>
                <button
                  onClick={() => {
                    void act("historyOpen", { id: h.id });
                    onClose();
                  }}
                >
                  <Clock3 size={16} />
                  <span>
                    <strong>{h.title}</strong>
                    <small>{h.updated}</small>
                  </span>
                  <ChevronRight size={15} />
                </button>
                {h.pendingDelete ? (
                  <div className="delete-confirm">
                    <span>删除这段对话？</span>
                    <button
                      onClick={() =>
                        void act("historyConfirmDelete", { id: h.id })
                      }
                    >
                      删除
                    </button>
                    <button onClick={() => void act("historyCancelDelete")}>
                      保留
                    </button>
                  </div>
                ) : (
                  state.historyCanDelete !== false && (
                    <IconButton
                      label={`删除对话：${h.title}`}
                      onClick={() =>
                        void act("historyRequestDelete", { id: h.id })
                      }
                    >
                      <Trash2 size={13} />
                    </IconButton>
                  )
                )}
              </div>
            ))}
          {state.historyLoading && (
            <p className="panel-note" role="status">
              正在加载对话…
            </p>
          )}
          {!state.historyLoading &&
            !state.history.filter((h) => h.id !== state.conversationId)
              .length && (
              <p className="empty-note">开始新对话后，这一段会留在这里。</p>
            )}
          {state.historyHasMore && (
            <button
              className="text-button"
              onClick={() =>
                void act("historyLoad", {
                  onlyCurrentProject: state.historyOnlyProject,
                  append: true,
                })
              }
            >
              加载更多
            </button>
          )}
        </div>
      </Sheet>
    );
  if (panel === "context")
    return (
      <Sheet
        title="图片与上下文"
        kicker="BRING IT ALONG"
        className="context-sheet"
        onClose={onClose}
      >
        <div className="context-actions">
          <button
            disabled={!state.actions.image}
            onClick={() => {
              onClose();
              onAttach();
            }}
          >
            <ImagePlus size={18} />
            <span>
              <strong>添加图片</strong>
              <small>PNG / JPEG · 最大 8 MB</small>
            </span>
            <Plus size={16} />
          </button>
          <button
            aria-label={state.contextLabel ? "更换项目文件夹" : "选择项目文件夹"}
            disabled={!state.actions.context || !available("chooseWorkspace")}
            onClick={() => void act("chooseWorkspace")}
          >
            <Folder size={18} />
            <span>
              <strong>{state.contextLabel ? `项目文件夹 · ${state.contextLabel}` : "选择项目文件夹"}</strong>
              <small>{state.contextLabel ? "当前对话按需读取此文件夹 · 点击更换" : "让当前对话按需读取所选文件夹中的资料"}</small>
            </span>
            <ChevronRight size={16} />
          </button>
        </div>
        {state.imageDisclosure && (
          <details className="image-disclosure">
            <summary tabIndex={0}>图片会随消息发送 · 查看说明</summary>
            <p className="rule-note">{state.imageDisclosure}</p>
          </details>
        )}
        {state.contextChips.length > 0 && (
          <div className="context-chip-list">
            {state.contextChips.map((chip) => (
              <span key={chip.id}>
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
          </div>
        )}
        {native && (
          <div className="native-links">
            <button
              className="text-button"
              disabled={!available("clearWorkspace")}
              onClick={() => void act("clearWorkspace")}
            >
              移除项目文件夹
            </button>
          </div>
        )}
      </Sheet>
    );
  if (panel === "settings")
    return (
      <Sheet
        title="Make yourself at home."
        kicker="PREFERENCES"
        onClose={onClose}
      >
        <div className="settings-list">
          <div className="setting-row">
            <span>
              {theme === "dark" ? <Moon size={18} /> : <Sun size={18} />}
              <span>
                Night mode
                <small>
                  {native ? "跟随宿主提供的配色" : "给夜晚留一点柔和的光"}
                </small>
              </span>
            </span>
            <button
              role="switch"
              aria-label="夜间主题"
              aria-checked={theme === "dark"}
              disabled={native}
              className={`toggle ${theme === "dark" ? "on" : ""}`}
              onClick={onTheme}
            >
              <i />
            </button>
          </div>
          <div className="setting-row">
            <span>
              <Settings2 size={18} />
              <span>
                Less motion<small>保留反馈，减少位移与循环动画</small>
              </span>
            </span>
            <button
              role="switch"
              aria-label="减少动效"
              aria-checked={reduced}
              className={`toggle ${reduced ? "on" : ""}`}
              onClick={onReduced}
            >
              <i />
            </button>
          </div>
        </div>
        {native ? (
          <div className="context-actions">
            <button
              disabled={!available("openPersonalizationSettings")}
              onClick={() => void act("openPersonalizationSettings")}
            >
              <Settings2 size={18} />
              <span>
                <strong>个性化设置</strong>
                <small>角色、外观与陪伴偏好</small>
              </span>
              <ArrowUpRight size={15} />
            </button>
            <button
              disabled={!available("openSpeechSettings")}
              onClick={() => void act("openSpeechSettings")}
            >
              <Volume2 size={18} />
              <span>
                <strong>语音设置</strong>
                <small>朗读与声音</small>
              </span>
              <ArrowUpRight size={15} />
            </button>
            <button
              disabled={!available("openModelSettings")}
              onClick={() => void act("openModelSettings")}
            >
              <MessageCircle size={18} />
              <span>
                <strong>模型设置</strong>
                <small>连接与模型能力</small>
              </span>
              <ArrowUpRight size={15} />
            </button>
          </div>
        ) : (
          <p className="rule-note">
            偏好与示例对话保存在这个浏览器中。桌面版本使用宿主自己的主题与会话。
          </p>
        )}
      </Sheet>
    );
  if (panel === "approval-review" && state.approval)
    return (
      <Sheet
        title="Your say matters."
        kicker="操作确认 · 示例"
        onClose={onClose}
      >
        <div className="review-icon">
          <ShieldCheck size={28} />
        </div>
        <h3>{state.approval.title}</h3>
        <p className="panel-note">{state.approval.description}</p>
        <pre className="review-details">{state.approval.detail}</pre>
        <p className="rule-note">
          这里演示确认流程；桌面版本会打开现有的原生确认窗口。
        </p>
        <div className="card-actions">
          <button
            className="primary-button"
            onClick={() => {
              void act("approvalReview", { ...state.approval });
              onClose();
            }}
          >
            允许这一次
            <Check size={14} />
          </button>
          <button
            className="secondary-button"
            onClick={() => {
              void act("approvalDeny", { ...state.approval });
              onClose();
            }}
          >
            不允许
          </button>
        </div>
      </Sheet>
    );
  if (panel === "about")
    return (
      <Sheet
        title="A little closer."
        kicker="MARGIN · INTERACTION SAMPLE"
        onClose={onClose}
      >
        <p className="panel-note">从一句话开始，慢慢展开一段对话。</p>
        <div className="about-list">
          <p>
            <MessageCircle size={18} />
            <span>
              浏览器使用示例回复。顶部“体验场景”可以查看等待、工具、确认、追问和中断。
            </span>
          </p>
          <p>
            <Timer size={18} />
            <span>
              待办与专注计时可以操作。角色采用现有静态立绘，用于体验窗口关系。
            </span>
          </p>
          <p>
            <FolderOpen size={18} />
            <span>
              同一套界面接入 WebView2
              后使用真实宿主快照；迁移包包含源码、构建资产和宿主适配。
            </span>
          </p>
        </div>
        <a
          className="primary-button full-width"
          href={`${import.meta.env.BASE_URL}downloads/margin-ui-migration.zip`}
          download
        >
          <Download size={16} />
          下载可迁移交互样例
        </a>
        <button
          className="secondary-button full-width"
          onClick={() => {
            driver.reset?.();
            onClose();
            notify("示例已恢复到最初的样子");
          }}
        >
          重新体验初始场景
        </button>
      </Sheet>
    );
  return null;
}
