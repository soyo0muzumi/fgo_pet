/** Wire contract mirrored from FgoPet.Plugin.Dialogue/Contracts.
 * All business state below belongs to the C# owner. The optional window member
 * allows the bridge to keep working with hosts predating setPresentation.
 */
export type ChatIdentity = {
  sessionId: string;
  servantId: string;
  conversationId: string;
};
export type ChatChoice = {
  id: string;
  label: string;
  selected: boolean;
  canSelect: boolean;
};
export type ChatTurn = {
  stickerId?: string;
  stickerPosition?: "before" | "after";
  stickerLabel?: string;
  id: string;
  role: string;
  text: string;
  isStreaming: boolean;
  reasoningText: string;
  reasoningSummary: string;
  reasoningDurationText: string;
  isThinkingActive: boolean;
  isReasoningExpanded: boolean;
  canCopy: boolean;
  canReadAloud: boolean;
  canOpenWorkspace: boolean;
  speechBusy: boolean;
  speechActionText: string;
  speechStatusText: string;
  speechNeedsConfiguration: boolean;
};
export type PendingRequestIdentity = {
  runId: string;
  requestId: string;
  revision: number;
};
export type ChatAnswer = {
  questionId: string;
  selectedOptionIndices: number[];
  text: string | null;
};
export type ChatUserInput = PendingRequestIdentity & {
  expiresAt: string;
  questions: Array<{
    id: string;
    text: string;
    options: string[];
    allowMultiple: boolean;
  }>;
  isSubmitting: boolean;
  errorText: string;
};
export type ChatApproval = PendingRequestIdentity & {
  expiresAt: string;
  toolName: string;
  plainTextDetails: string;
  actionTitle?: string | null;
  plainTextSummary?: string | null;
  isSubmitting: boolean;
  errorText: string;
};
export type HostAvailability = {
  canOpenFocus: boolean;
  canOpenWorkspaceOverview: boolean;
  canCreateWorkspaceItem: boolean;
  canOpenPersonalizationSettings: boolean;
  canOpenSpeechSettings: boolean;
  canOpenModelSettings: boolean;
  canRefreshProjects: boolean;
  canChooseWorkspace: boolean;
  canClearWorkspace: boolean;
  canChooseImage: boolean;
  canClearImage: boolean;
  canConfigureBrowserImages: boolean;
};
export type PresentationView = "composer" | "conversation";
export type HostWindowPresentation = {
  view: PresentationView;
  wide: boolean;
  /** Web content height in DIP, excluding the native 22 DIP drag strip. */
  composerHeight: number;
};
export type HostPresentation = {
  roleName: string;
  avatarPngDataUrl: string | null;
  projects: ChatChoice[];
  models: ChatChoice[];
  isProjectsLoading: boolean;
  projectsStatus: string;
  actions: HostAvailability | null;
  workspaceLabel: string | null;
  imageLabel: string | null;
  imageDisclosure: string;
  /** Absence means the host supports only legacy window sizes. */
  window?: HostWindowPresentation | null;
};
export type ChatConversation = {
  turns: ChatTurn[];
  isStreaming: boolean;
  isThinking: boolean;
  thinkingTimerText: string;
  requestStatusText: string;
  errorText: string;
  configurationRequired: boolean;
  providerStatusText: string;
  modelStatusText: string;
  canOpenModelSettings: boolean;
  canSend: boolean;
  canStop: boolean;
  canRetryCapability: boolean;
  showReasoning: boolean;
  capabilityNotice: { text: string; kind: string };
  hasStagedMemoryCandidate?: boolean;
  project: { id: string; label: string };
  contextChips: Array<{ id: string; label: string }>;
  recalledSources: Array<{ id: string; title: string; detail: string }>;
  pendingInput: ChatUserInput | null;
  pendingApproval: ChatApproval | null;
  hasPendingRun: boolean;
};
export type ChatHistory = {
  items: Array<{
    conversationId: string;
    title: string;
    updatedText: string;
    status: string;
  }>;
  status: string;
  isLoading: boolean;
  hasMore: boolean;
  onlyCurrentProject: boolean;
  pendingDeleteConversationId: string | null;
  pendingDeleteTitle: string | null;
  canDelete: boolean;
};
export type Snapshot = {
  identity: ChatIdentity;
  version: number;
  draft: { text: string; revision: number };
  presentation: HostPresentation;
  conversation: ChatConversation;
  history: ChatHistory;
};

export type HostTargetAction =
  | "copyTurn"
  | "readTurn"
  | "openSource"
  | "openWorkspace"
  | "selectProject"
  | "selectModel"
  | "removeContextChip";
export type HostSimpleAction =
  | "hide"
  | "openFocus"
  | "openWorkspaceOverview"
  | "newWorkspaceItem"
  | "openPersonalizationSettings"
  | "openSpeechSettings"
  | "openModelSettings"
  | "refreshProjects"
  | "chooseWorkspace"
  | "clearWorkspace"
  | "chooseImage"
  | "clearImage"
  | "configureBrowserImages";
export type ChatHostFields =
  | { action: HostTargetAction; targetId: string }
  | { action: HostSimpleAction }
  | { action: "setExpanded"; expanded: boolean }
  | {
      action: "setPresentation";
      view: PresentationView;
      wide: boolean;
      composerHeight?: number;
    };
export type ChatCommandFields = {
  "chat.get": Record<string, never>;
  "chat.draft": { text: string; revision: number };
  "chat.send": Record<string, never>;
  "chat.stop": Record<string, never>;
  "chat.new": Record<string, never>;
  "chat.retryCapability": Record<string, never>;
  "chat.history": { onlyCurrentProject: boolean; append: boolean };
  "chat.history.open": { targetConversationId: string };
  "chat.history.requestDelete": { targetConversationId: string };
  "chat.history.confirmDelete": { targetConversationId: string };
  "chat.history.cancelDelete": Record<string, never>;
  "chat.input.answer": PendingRequestIdentity & { answers: ChatAnswer[] };
  "chat.input.cancel": PendingRequestIdentity;
  "chat.approval.review": PendingRequestIdentity;
  "chat.approval.deny": PendingRequestIdentity;
  "chat.approval.cancel": PendingRequestIdentity;
  "chat.host": ChatHostFields;
  "chat.image": {
    transferId: string;
    mimeType: "image/png" | "image/jpeg";
    offset: number;
    data: string;
    complete: boolean;
  };
  "chat.image.cancel": Record<string, never>;
};
export type ChatCommandName = keyof ChatCommandFields;
export type NativeEvent = { type: string; [key: string]: unknown };
export type ExecuteOptions = {
  timeoutMs?: number;
  /** A command tied to a rendered item must not migrate to a newly opened conversation. */
  expectedIdentity?: ChatIdentity;
};
export type PresentationResult = {
  snapshot: Snapshot;
  compatible: boolean;
  warning?: string;
};
export interface NativeBridge {
  ready(): Promise<Snapshot>;
  get(): Promise<Snapshot>;
  subscribe(listener: (event: NativeEvent) => void): () => void;
  onSnapshot(listener: (snapshot: Snapshot) => void): () => void;
  execute<K extends ChatCommandName>(
    type: K,
    fields?: ChatCommandFields[K],
    options?: ExecuteOptions,
  ): Promise<Snapshot>;
  execute(
    type: string,
    fields?: Record<string, unknown>,
    options?: ExecuteOptions,
  ): Promise<Snapshot>;
  sendDraftThenSend(text: string, revision: number): Promise<Snapshot>;
  stop(): Promise<Snapshot>;
  importImage(file: File, signal?: AbortSignal): Promise<Snapshot>;
  resizePresentation(
    view: PresentationView,
    wide: boolean,
    composerHeight?: number,
  ): Promise<PresentationResult>;
  dispose(): void;
}
