export type Phase =
  | "idle"
  | "submitting"
  | "waiting"
  | "tool"
  | "streaming"
  | "approval"
  | "input"
  | "stopping"
  | "error";
export type View = "composer" | "conversation";
export type Scenario =
  | "conversation"
  | "task"
  | "tool"
  | "approval"
  | "question"
  | "error"
  | "long"
  | "speech";
export type Choice = {
  id: string;
  label: string;
  selected?: boolean;
  disabled?: boolean;
};
export type Message = {
  stickerId?: string;
  stickerPosition?: "before" | "after";
  stickerLabel?: string;
  id: string;
  role: "user" | "assistant" | "system";
  text: string;
  streaming?: boolean;
  stopped?: boolean;
  reasoning?: string;
  reasoningSummary?: string;
  reasoningDuration?: string;
  imageUrl?: string;
  imageLabel?: string;
  canCopy?: boolean;
  canReadAloud?: boolean;
  speechBusy?: boolean;
  speechStatus?: string;
  speechAction?: string;
  speechNeedsConfiguration?: boolean;
  thinkingActive?: boolean;
  reasoningExpanded?: boolean;
  canOpenWorkspace?: boolean;
  task?: { title: string; due: "today" | "tomorrow"; saved?: boolean };
};
export type Task = {
  id: string;
  title: string;
  done: boolean;
  due: "today" | "tomorrow" | "overdue" | "none";
  inProgress?: boolean;
  createdToday?: boolean;
  source?: string;
};
export type Focus = {
  minutes: number;
  remaining: number;
  endAt: number | null;
};
export type HistoryItem = {
  id: string;
  title: string;
  updated: string;
  status?: string;
  current?: boolean;
  pendingDelete?: boolean;
  messages?: Message[];
};
export type Approval = {
  runId: string;
  requestId: string;
  revision: number;
  title: string;
  description: string;
  detail?: string;
  detailExpanded?: boolean;
  canReview?: boolean;
  canDeny?: boolean;
  canCancel?: boolean;
  expiresAt?: string;
  busy?: boolean;
  error?: string;
};
export type Question = {
  id: string;
  label: string;
  prompt: string;
  options: Array<{ label: string; description?: string }>;
  multiSelect?: boolean;
  allowText?: boolean;
};
export type PendingInput = {
  runId: string;
  requestId: string;
  revision: number;
  title?: string;
  questions: Question[];
  canSubmit?: boolean;
  canCancel?: boolean;
  expiresAt?: string;
  busy?: boolean;
  error?: string;
};
export type Answer = {
  questionId: string;
  selectedOptionIndices: number[];
  text: string | null;
};
export type ContextChip = { id: string; label: string; removable?: boolean };
export type UiState = {
  revision: number;
  ready: boolean;
  connectionError?: string;
  /** UI-only session/role scope; stays stable when the first conversation ID is admitted. */
  ownerKey?: string;
  conversationId: string;
  roleName: string;
  avatarUrl?: string;
  draft: string;
  messages: Message[];
  phase: Phase;
  status: string;
  detail?: string;
  error?: string;
  canSend: boolean;
  canStop: boolean;
  models: Choice[];
  projects: Choice[];
  modelLabel: string;
  projectLabel: string;
  contextLabel?: string;
  contextChips: ContextChip[];
  recalledSources?: Array<{ id: string; title: string; detail: string }>;
  capabilityNotice?: { text: string; kind: string };
  hasStagedMemoryCandidate?: boolean;
  canRetryCapability?: boolean;
  configurationRequired?: boolean;
  providerStatus?: string;
  projectsLoading?: boolean;
  projectsStatus?: string;
  image?: {
    label: string;
    previewUrl?: string;
    pending?: boolean;
    status?: string;
  };
  imageDisclosure?: string;
  history: HistoryItem[];
  historyLoading?: boolean;
  historyHasMore?: boolean;
  historyOnlyProject?: boolean;
  historyCanDelete?: boolean;
  historyStatus?: string;
  approval?: Approval;
  input?: PendingInput;
  tasks: Task[];
  focus: Focus | null;
  actions: {
    image: boolean;
    model: boolean;
    project: boolean;
    context: boolean;
    workspace: boolean;
    settings: boolean;
    newConversation: boolean;
  };
  visibility?: boolean;
  nativeTheme?: "light" | "dark";
  hostPresentationSupported?: boolean;
  hostPresentation?: { view: View; wide: boolean; composerHeight: number };
  compatibilityWarning?: string;
  availableActions?: Partial<Record<ActionName, boolean>>;
};
export type ActionName =
  | "model"
  | "project"
  | "chooseWorkspace"
  | "clearWorkspace"
  | "removeContextChip"
  | "chooseImage"
  | "clearImage"
  | "cancelImage"
  | "configureBrowserImages"
  | "historyLoad"
  | "historyOpen"
  | "historyRequestDelete"
  | "historyConfirmDelete"
  | "historyCancelDelete"
  | "copy"
  | "readAloud"
  | "openSource"
  | "openWorkspace"
  | "newWorkspaceItem"
  | "openWorkspaceOverview"
  | "openFocus"
  | "openPersonalizationSettings"
  | "openSpeechSettings"
  | "openModelSettings"
  | "refreshProjects"
  | "refreshModels"
  | "approvalReview"
  | "approvalDeny"
  | "approvalCancel"
  | "inputAnswer"
  | "inputCancel"
  | "retryCapability"
  | "taskSave"
  | "taskAdd"
  | "taskToggle"
  | "taskDelete"
  | "focusStart"
  | "focusToggle"
  | "focusFinish";
export interface ChatDriver {
  kind: "demo" | "native";
  getSnapshot(): UiState;
  subscribe(listener: () => void): () => void;
  connect(): Promise<void>;
  setDraft(text: string): void;
  send(text: string): Promise<void>;
  stop(): Promise<void>;
  newConversation(): Promise<void>;
  action(name: ActionName, payload?: Record<string, unknown>): Promise<void>;
  importImage(file: File): Promise<void>;
  presentation(
    view: View,
    wide: boolean,
    composerHeight?: number,
  ): Promise<void>;
  hide(): Promise<void>;
  scenario?(scenario: Scenario): void;
  configureDemoModels?(models: readonly Pick<Choice, "id" | "label">[], selectedId: string): void;
  reset?(): void;
  dispose(): void;
}
export const emptyState = (): UiState => ({
  revision: 0,
  ready: false,
  conversationId: "",
  roleName: "Mash",
  draft: "",
  messages: [],
  phase: "idle",
  status: "在这里，陪着您。",
  canSend: false,
  canStop: false,
  models: [],
  projects: [],
  modelLabel: "模型",
  projectLabel: "当前项目",
  contextChips: [],
  history: [],
  tasks: [],
  focus: null,
  actions: {
    image: false,
    model: false,
    project: false,
    context: false,
    workspace: false,
    settings: false,
    newConversation: false,
  },
});
export const isRunning = (phase: Phase) =>
  ["submitting", "waiting", "tool", "streaming", "stopping"].includes(phase);
export const needsAttention = (state: UiState) =>
  isRunning(state.phase) || !!state.approval || !!state.input || !!state.error;
export const uid = () =>
  globalThis.crypto?.randomUUID?.() ??
  `margin-${Date.now().toString(36)}-${Math.random().toString(36).slice(2)}`;
export const todayTasks = (tasks: Task[]) =>
  tasks.filter(
    (t) =>
      !t.done &&
      (t.inProgress ||
        t.due === "today" ||
        t.due === "overdue" ||
        (t.due === "none" && t.createdToday)),
  );
