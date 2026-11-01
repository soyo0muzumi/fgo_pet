import type { Snapshot } from "../src/hostTypes.ts";

type Wire = {
  type: string;
  requestId?: string;
  payload?: Record<string, unknown>;
  [key: string]: unknown;
};
export const tick = () => new Promise<void>((resolve) => setImmediate(resolve));
export const pause = (ms: number) =>
  new Promise<void>((resolve) => setTimeout(resolve, ms));
export function fixture(): Snapshot {
  return {
    identity: {
      sessionId: "native-0",
      servantId: "mash",
      conversationId: "conversation-a",
    },
    version: 1,
    draft: { text: "", revision: 0 },
    presentation: {
      roleName: "Mash",
      avatarPngDataUrl: null,
      projects: [
        {
          id: "project-a",
          label: "Project A",
          selected: true,
          canSelect: true,
        },
      ],
      models: [
        { id: "model-a", label: "Model A", selected: true, canSelect: true },
      ],
      isProjectsLoading: false,
      projectsStatus: "",
      workspaceLabel: null,
      imageLabel: null,
      imageDisclosure: "Share only on Send",
      actions: {
        canOpenFocus: true,
        canOpenWorkspaceOverview: true,
        canCreateWorkspaceItem: true,
        canOpenPersonalizationSettings: true,
        canOpenSpeechSettings: true,
        canOpenModelSettings: true,
        canRefreshProjects: true,
        canChooseWorkspace: true,
        canClearWorkspace: false,
        canChooseImage: true,
        canClearImage: false,
        canConfigureBrowserImages: true,
      },
      window: { view: "composer", wide: false, composerHeight: 160 },
    },
    conversation: {
      turns: [
        {
          id: "reply-a",
          role: "assistant",
          text: "A reply",
          isStreaming: false,
          reasoningText: "",
          reasoningSummary: "",
          reasoningDurationText: "",
          isThinkingActive: false,
          isReasoningExpanded: false,
          canCopy: true,
          canReadAloud: true,
          canOpenWorkspace: false,
          speechBusy: false,
          speechActionText: "Read aloud",
          speechStatusText: "",
          speechNeedsConfiguration: false,
        },
      ],
      isStreaming: false,
      isThinking: false,
      thinkingTimerText: "",
      requestStatusText: "",
      errorText: "",
      configurationRequired: false,
      providerStatusText: "Available",
      modelStatusText: "Model A",
      canOpenModelSettings: true,
      canSend: false,
      canStop: false,
      canRetryCapability: false,
      showReasoning: true,
      capabilityNotice: { text: "", kind: "none" },
      project: { id: "project-a", label: "Project A" },
      contextChips: [],
      recalledSources: [],
      pendingInput: null,
      pendingApproval: null,
      hasPendingRun: false,
    },
    history: {
      items: [
        {
          conversationId: "conversation-a",
          title: "A",
          updatedText: "Today",
          status: "",
        },
        {
          conversationId: "conversation-b",
          title: "B",
          updatedText: "Yesterday",
          status: "",
        },
      ],
      status: "",
      isLoading: false,
      hasMore: false,
      onlyCurrentProject: false,
      pendingDeleteConversationId: null,
      pendingDeleteTitle: null,
      canDelete: true,
    },
  };
}

/** Protocol fixture, not a model/kernel simulator. All replies are deterministic acknowledgements. */
export class MockNativeHost {
  state = fixture();
  posts: Wire[] = [];
  styles = new Map<string, string>();
  held: Array<{ message: Wire; snapshot: Snapshot }> = [];
  private holds = new Map<string, number>();
  private listeners = new Set<(event: MessageEvent) => void>();
  private originals: Array<[string, PropertyDescriptor | undefined]> = [];
  install() {
    this.originals = ["window", "document"].map((key) => [
      key,
      Object.getOwnPropertyDescriptor(globalThis, key),
    ]);
    Object.defineProperty(globalThis, "window", {
      configurable: true,
      value: {
        chrome: {
          webview: {
            postMessage: (message: Wire) => this.post(message),
            addEventListener: (
              _: string,
              listener: (event: MessageEvent) => void,
            ) => this.listeners.add(listener),
            removeEventListener: (
              _: string,
              listener: (event: MessageEvent) => void,
            ) => this.listeners.delete(listener),
          },
        },
      },
    });
    Object.defineProperty(globalThis, "document", {
      configurable: true,
      value: {
        documentElement: {
          style: {
            setProperty: (name: string, value: string) =>
              this.styles.set(name, value),
          },
        },
      },
    });
    return () => {
      for (const [key, descriptor] of this.originals) {
        if (descriptor) Object.defineProperty(globalThis, key, descriptor);
        else Reflect.deleteProperty(globalThis, key);
      }
    };
  }
  hold(type: string, count = 1) {
    this.holds.set(type, count);
  }
  emit(message: unknown) {
    for (const listener of this.listeners)
      listener({ data: structuredClone(message) } as MessageEvent);
  }
  publish() {
    this.state.version++;
    this.emit({ type: "chat.snapshot", payload: this.state });
  }
  release(type: string, errorCode?: string) {
    const index = this.held.findIndex((item) => item.message.type === type);
    if (index < 0) throw new Error("No held command: " + type);
    const item = this.held.splice(index, 1)[0];
    this.reply(item.message, item.snapshot, errorCode);
  }
  private reply(message: Wire, snapshot: Snapshot, errorCode?: string) {
    this.emit({
      type: "command.result",
      requestId: message.requestId,
      success: !errorCode,
      ...(errorCode ? { errorCode } : { payload: { snapshot } }),
    });
  }
  private post(message: Wire) {
    this.posts.push(structuredClone(message));
    if (message.type === "ready") {
      this.emit({ type: "chat.snapshot", payload: this.state });
      return;
    }
    if (message.type === "theme.ack") return;
    const p = message.payload || {};
    switch (message.type) {
      case "chat.draft":
        if (Number(p.revision) <= this.state.draft.revision) {
          this.reply(message, this.state, "CHAT_DRAFT_STALE");
          return;
        }
        this.state.draft = {
          text: String(p.text),
          revision: Number(p.revision),
        };
        this.state.conversation.canSend = !!String(p.text).trim();
        break;
      case "chat.send":
        this.state.identity.conversationId ||= "admitted-conversation";
        this.state.draft.text = "";
        this.state.conversation.isStreaming = true;
        this.state.conversation.isThinking = true;
        this.state.conversation.canSend = false;
        this.state.conversation.canStop = true;
        break;
      case "chat.stop":
        this.state.conversation.isStreaming = false;
        this.state.conversation.isThinking = false;
        this.state.conversation.canStop = false;
        this.state.conversation.hasPendingRun = false;
        for (const turn of this.state.conversation.turns)
          turn.isStreaming = false;
        break;
      case "chat.history.open":
        this.state.identity = {
          ...this.state.identity,
          sessionId: "native-1",
          conversationId: String(p.targetConversationId),
        };
        this.state.draft = { text: "", revision: this.state.draft.revision };
        this.state.conversation.turns = [];
        break;
      case "chat.approval.review":
      case "chat.approval.deny":
      case "chat.approval.cancel":
        this.state.conversation.pendingApproval = null;
        break;
      case "chat.image":
        if (p.complete) {
          this.state.presentation.imageLabel = "native-image.png";
          this.state.presentation.actions!.canClearImage = true;
        }
        break;
      case "chat.host":
        if (p.action === "setPresentation")
          this.state.presentation.window = {
            view: p.view as "composer" | "conversation",
            wide: p.wide === true,
            composerHeight: Number(
              p.composerHeight ??
                this.state.presentation.window?.composerHeight ??
                160,
            ),
          };
        break;
    }
    this.state.version++;
    const snapshot = structuredClone(this.state);
    this.emit({ type: "chat.snapshot", payload: snapshot });
    const remaining = this.holds.get(message.type) || 0;
    if (remaining > 0) {
      this.holds.set(message.type, remaining - 1);
      this.held.push({ message, snapshot });
    } else this.reply(message, snapshot);
  }
}
