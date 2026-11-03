import type {
  ChatIdentity,
  ExecuteOptions,
  NativeBridge,
  NativeEvent,
  PresentationView,
  Snapshot,
} from "./hostTypes";
export type * from "./hostTypes";

/** Finite WebView2 adapter. Never creates demo data or substitutes browser APIs
 * for native actions. Verified against fgo_pet@f8b2f362 plus the supplied patch. */
type WebViewPort = {
  postMessage(message: unknown): void;
  addEventListener(
    type: "message",
    listener: (event: MessageEvent) => void,
  ): void;
  removeEventListener(
    type: "message",
    listener: (event: MessageEvent) => void,
  ): void;
};
type Pending = {
  resolve(value: unknown): void;
  reject(error: Error): void;
  timer: ReturnType<typeof setTimeout>;
};
const object = (value: unknown): value is Record<string, unknown> =>
  value !== null && typeof value === "object" && !Array.isArray(value);
export class NativeBridgeError extends Error {
  readonly code: string;
  constructor(code: string) {
    super(code);
    this.code = code;
    this.name = "NativeBridgeError";
  }
}
const failure = (code: string) => new NativeBridgeError(code);
export const sameIdentity = (a: ChatIdentity, b: ChatIdentity) =>
  a.sessionId === b.sessionId &&
  a.servantId === b.servantId &&
  a.conversationId === b.conversationId;

function isSnapshot(value: unknown): value is Snapshot {
  if (!object(value) || !object(value.identity)) return false;
  const id = value.identity;
  return (
    ["sessionId", "servantId", "conversationId"].every(
      (key) => typeof id[key] === "string",
    ) &&
    typeof value.version === "number" &&
    Number.isSafeInteger(value.version) &&
    object(value.draft) &&
    typeof value.draft.text === "string" &&
    Number.isSafeInteger(value.draft.revision) &&
    object(value.presentation) &&
    Array.isArray(value.presentation.models) &&
    Array.isArray(value.presentation.projects) &&
    object(value.conversation) &&
    Array.isArray(value.conversation.turns) &&
    object(value.history) &&
    Array.isArray(value.history.items)
  );
}

const noFields = new Set([
  "chat.get",
  "chat.send",
  "chat.stop",
  "chat.new",
  "chat.retryCapability",
  "chat.image.cancel",
  "chat.history.cancelDelete",
]);
const targetActions = new Set([
  "copyTurn",
  "readTurn",
  "openSource",
  "openWorkspace",
  "selectProject",
  "selectModel",
  "removeContextChip",
]);
const simpleActions = new Set([
  "hide",
  "openFocus",
  "openWorkspaceOverview",
  "newWorkspaceItem",
  "openPersonalizationSettings",
  "openSpeechSettings",
  "openModelSettings",
  "refreshProjects",
  "refreshModels",
  "chooseWorkspace",
  "clearWorkspace",
  "chooseImage",
  "clearImage",
]);
const pendingCommands = new Set([
  "chat.input.answer",
  "chat.input.cancel",
  "chat.approval.review",
  "chat.approval.deny",
  "chat.approval.cancel",
]);

/** Reject unknown fields before posting; the native reader remains authoritative. */
function validateFields(type: string, fields: Record<string, unknown>) {
  let keys: string[];
  if (noFields.has(type)) keys = [];
  else if (type === "chat.draft") {
    keys = ["text", "revision"];
    if (typeof fields.text !== "string" || fields.text.length > 12_000)
      throw failure("CHAT_DRAFT_TOO_LONG");
    if (!Number.isSafeInteger(fields.revision) || Number(fields.revision) <= 0)
      throw failure("CHAT_COMMAND_INVALID");
  } else if (type === "chat.history") keys = ["onlyCurrentProject", "append"];
  else if (
    [
      "chat.history.open",
      "chat.history.requestDelete",
      "chat.history.confirmDelete",
    ].includes(type)
  )
    keys = ["targetConversationId"];
  else if (pendingCommands.has(type))
    keys = [
      "runId",
      "requestId",
      "revision",
      ...(type === "chat.input.answer" ? ["answers"] : []),
    ];
  else if (type === "chat.image")
    keys = ["transferId", "mimeType", "offset", "data", "complete"];
  else if (type === "chat.host") {
    const action = fields.action;
    if (typeof action !== "string") throw failure("CHAT_COMMAND_INVALID");
    if (targetActions.has(action)) keys = ["action", "targetId"];
    else if (simpleActions.has(action)) keys = ["action"];
    else if (action === "setExpanded") keys = ["action", "expanded"];
    else if (action === "setPresentation") {
      keys = [
        "action",
        "view",
        "wide",
        ...(fields.composerHeight === undefined ? [] : ["composerHeight"]),
      ];
      if (
        !["composer", "conversation"].includes(String(fields.view)) ||
        typeof fields.wide !== "boolean"
      )
        throw failure("CHAT_COMMAND_INVALID");
      if (
        fields.composerHeight !== undefined &&
        (typeof fields.composerHeight !== "number" ||
          !Number.isFinite(fields.composerHeight) ||
          fields.composerHeight < 160 ||
          fields.composerHeight > 360)
      )
        throw failure("CHAT_COMMAND_INVALID");
    } else throw failure("CHAT_COMMAND_INVALID");
  } else throw failure("CHAT_COMMAND_INVALID");
  if (
    Object.keys(fields).length !== keys.length ||
    keys.some((key) => !Object.hasOwn(fields, key))
  )
    throw failure("CHAT_COMMAND_INVALID");
}
function timeoutFor(type: string, fields: Record<string, unknown>): number {
  if (type === "chat.approval.review") return 960_000;
  if (
    type === "chat.host" &&
    ["chooseWorkspace", "chooseImage"].includes(
      String(fields.action),
    )
  )
    return 600_000;
  return 20_000;
}

/** Null means an ordinary browser. A present but broken native port fails closed. */
export function createNativeBridge(): NativeBridge | null {
  if (typeof window === "undefined") return null;
  const port = (window as Window & { chrome?: { webview?: WebViewPort } })
    .chrome?.webview;
  if (!port) return null;
  if (
    typeof port.postMessage !== "function" ||
    typeof port.addEventListener !== "function" ||
    typeof port.removeEventListener !== "function"
  )
    throw failure("CHAT_BRIDGE_UNAVAILABLE");
  const nativePort = port;
  const pending = new Map<string, Pending>();
  const events = new Set<(event: NativeEvent) => void>();
  const snapshots = new Set<(snapshot: Snapshot) => void>();
  const retiredSessions = new Set<string>();
  const prefix =
    "margin_" +
    Date.now().toString(36) +
    "_" +
    Math.random().toString(36).slice(2, 8);
  let serial = 0,
    disposed = false,
    announced = false,
    sending = false,
    importing = false;
  let themeVersion = -1;
  let current: Snapshot | undefined;
  let readyTask: Promise<Snapshot> | undefined;
  let imageController: AbortController | undefined;

  function notify<T>(listeners: Set<(value: T) => void>, value: T) {
    for (const listener of listeners) {
      try {
        listener(value);
      } catch {
        console.warn("Native bridge subscriber failed.");
      }
    }
  }
  function accept(value: Snapshot): Snapshot {
    if (retiredSessions.has(value.identity.sessionId)) return current ?? value;
    if (
      current?.identity.sessionId === value.identity.sessionId &&
      value.version <= current.version
    )
      return current;
    if (current && current.identity.sessionId !== value.identity.sessionId)
      retiredSessions.add(current.identity.sessionId);
    current = value;
    notify(snapshots, value);
    return value;
  }
  function receive(event: MessageEvent) {
    if (disposed || !object(event.data) || typeof event.data.type !== "string")
      return;
    const message = event.data as NativeEvent;
    if (
      message.type === "command.result" &&
      typeof message.requestId === "string"
    ) {
      const request = pending.get(message.requestId);
      if (!request) return;
      pending.delete(message.requestId);
      clearTimeout(request.timer);
      if (message.success === true) request.resolve(message.payload ?? {});
      else
        request.reject(
          failure(
            typeof message.errorCode === "string"
              ? message.errorCode
              : "CHAT_COMMAND_INVALID",
          ),
        );
    } else if (
      message.type === "chat.snapshot" &&
      isSnapshot(message.payload)
    ) {
      accept(message.payload);
    } else if (message.type === "theme.changed") {
      const version =
        typeof message.version === "number" && Number.isFinite(message.version)
          ? message.version
          : undefined;
      if (
        version !== undefined &&
        version >= themeVersion &&
        object(message.variables)
      ) {
        themeVersion = version;
        for (const [name, value] of Object.entries(message.variables)) {
          if (/^--[a-zA-Z0-9-]+$/.test(name) && typeof value === "string") {
            document.documentElement.style.setProperty(name, value);
            // A distinct inherited alias lets .app map its own tokens without
            // a self-referential --paper:var(--paper) declaration.
            document.documentElement.style.setProperty(
              "--host-" + name.slice(2),
              value,
            );
          }
        }
      }
      if (version !== undefined) {
        try {
          nativePort.postMessage({ type: "theme.ack", version });
        } catch {
          notify(events, {
            type: "bridge.error",
            errorCode: "CHAT_BRIDGE_UNAVAILABLE",
          });
        }
      }
    }
    notify(events, message);
  }
  function request(
    type: string,
    payload: Record<string, unknown>,
    timeoutMs: number,
  ): Promise<unknown> {
    if (disposed) return Promise.reject(failure("CHAT_BRIDGE_DISPOSED"));
    const requestId = prefix + "_" + ++serial;
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        pending.delete(requestId);
        reject(failure("CHAT_TIMEOUT"));
      }, timeoutMs);
      pending.set(requestId, { resolve, reject, timer });
      try {
        nativePort.postMessage({ type, requestId, payload });
      } catch {
        clearTimeout(timer);
        pending.delete(requestId);
        reject(failure("CHAT_BRIDGE_UNAVAILABLE"));
      }
    });
  }
  async function command(
    type: string,
    fields: Record<string, unknown>,
    expected?: ChatIdentity,
    options?: ExecuteOptions,
  ) {
    validateFields(type, fields);
    if (expected && (!current || !sameIdentity(expected, current.identity)))
      throw failure("CHAT_IDENTITY_STALE");
    const duration = options?.timeoutMs ?? timeoutFor(type, fields);
    if (!Number.isFinite(duration) || duration <= 0)
      throw failure("CHAT_COMMAND_INVALID");
    const payload = type === "chat.get" ? {} : { ...fields, ...expected };
    const result = await request(type, payload, duration);
    if (!object(result) || !isSnapshot(result.snapshot))
      throw failure("CHAT_SNAPSHOT_INVALID");
    const next = result.snapshot;
    // An old command completion must never reopen a conversation the user left.
    if (
      current &&
      expected &&
      !sameIdentity(current.identity, expected) &&
      !sameIdentity(next.identity, current.identity)
    )
      return current;
    return accept(next);
  }
  async function get() {
    if (disposed) throw failure("CHAT_BRIDGE_DISPOSED");
    if (!announced) {
      try {
        nativePort.postMessage({ type: "ready" });
        announced = true;
      } catch {
        throw failure("CHAT_BRIDGE_UNAVAILABLE");
      }
    }
    return command(
      "chat.get",
      {},
      current ? { ...current.identity } : undefined,
    );
  }
  function ready() {
    if (disposed) return Promise.reject(failure("CHAT_BRIDGE_DISPOSED"));
    readyTask ??= get().catch((error) => {
      readyTask = undefined;
      throw error;
    });
    return readyTask.then((snapshot) => current ?? snapshot);
  }
  async function execute(
    type: string,
    fields: Record<string, unknown> = {},
    options?: ExecuteOptions,
  ) {
    validateFields(type, fields);
    if (type === "chat.get") return get();
    const captured =
      options?.expectedIdentity ??
      (current ? { ...current.identity } : undefined);
    await ready();
    const expected = captured ?? { ...current!.identity };
    return command(type, fields, expected, options);
  }
  async function sendDraftThenSend(text: string, revision: number) {
    if (sending) throw failure("CHAT_SEND_BUSY");
    if (importing) throw failure("CHAT_IMAGE_BUSY");
    validateFields("chat.draft", { text, revision });
    sending = true;
    const captured = current ? { ...current.identity } : undefined;
    try {
      await ready();
      const expected = captured ?? { ...current!.identity };
      if (!current || !sameIdentity(expected, current.identity))
        throw failure("CHAT_IDENTITY_STALE");
      if (current.draft.revision !== revision || current.draft.text !== text) {
        await command("chat.draft", { text, revision }, expected);
      }
      if (!current || !sameIdentity(expected, current.identity))
        throw failure("CHAT_IDENTITY_STALE");
      if (current.draft.text !== text || current.draft.revision !== revision)
        throw failure("CHAT_DRAFT_NOT_ACCEPTED");
      // chat.send has a closed identity-only schema. Never include text here.
      return await command("chat.send", {}, expected);
    } finally {
      sending = false;
    }
  }
  async function importImage(file: File, signal?: AbortSignal) {
    if (importing || sending) throw failure("CHAT_IMAGE_BUSY");
    const mime =
      file.type ||
      (/\.png$/i.test(file.name)
        ? "image/png"
        : /\.jpe?g$/i.test(file.name)
          ? "image/jpeg"
          : "");
    if (
      !["image/png", "image/jpeg"].includes(mime) ||
      file.size < 1 ||
      file.size > 8 * 1024 * 1024
    )
      throw failure("CHAT_IMAGE_INVALID");
    const captured = current ? { ...current.identity } : undefined;
    importing = true;
    imageController = new AbortController();
    const abort = () => imageController?.abort();
    signal?.addEventListener("abort", abort, { once: true });
    if (signal?.aborted) abort();
    let expected: ChatIdentity | undefined,
      complete = false;
    try {
      await ready();
      expected = captured ?? { ...current!.identity };
      if (
        current!.presentation.actions?.canChooseImage !== true ||
        current!.conversation.isStreaming ||
        current!.conversation.hasPendingRun
      )
        throw failure("CHAT_NOT_AVAILABLE");
      const transferId = crypto.randomUUID().replaceAll("-", "");
      let snapshot = current!;
      for (let offset = 0; offset < file.size; offset += 24_576) {
        if (imageController.signal.aborted)
          throw failure("CHAT_IMAGE_CANCELED");
        const bytes = new Uint8Array(
          await file.slice(offset, offset + 24_576).arrayBuffer(),
        );
        if (imageController.signal.aborted)
          throw failure("CHAT_IMAGE_CANCELED");
        snapshot = await command(
          "chat.image",
          {
            transferId,
            mimeType: mime,
            offset,
            data: btoa(String.fromCharCode(...bytes)),
            complete: offset + bytes.length === file.size,
          },
          expected,
        );
      }
      complete = true;
      return snapshot;
    } finally {
      if (
        !complete &&
        !disposed &&
        expected &&
        current &&
        sameIdentity(expected, current.identity)
      ) {
        try {
          await command("chat.image.cancel", {}, expected);
        } catch {
          /* The host also expires incomplete transfers. */
        }
      }
      signal?.removeEventListener("abort", abort);
      imageController = undefined;
      importing = false;
    }
  }
  async function resizePresentation(
    view: PresentationView,
    wide: boolean,
    composerHeight?: number,
  ) {
    await ready();
    const height =
      composerHeight === undefined
        ? undefined
        : Math.max(160, Math.min(360, Math.ceil(composerHeight)));
    if (current?.presentation.window) {
      const fields: Record<string, unknown> = {
        action: "setPresentation",
        view,
        wide,
      };
      if (height !== undefined) fields.composerHeight = height;
      return { snapshot: await execute("chat.host", fields), compatible: true };
    }
    const snapshot = await execute("chat.host", {
      action: "setExpanded",
      expanded: view === "conversation" && wide,
    });
    return {
      snapshot,
      compatible: false,
      warning:
        "当前桌面宿主仍使用旧窗口尺寸。请安装 native-presentation.patch；仅更新网页无法收起为输入条。",
    };
  }

  nativePort.addEventListener("message", receive);
  return {
    ready,
    get,
    execute,
    sendDraftThenSend,
    importImage,
    resizePresentation,
    stop: () => execute("chat.stop"),
    subscribe(listener) {
      if (disposed) throw failure("CHAT_BRIDGE_DISPOSED");
      events.add(listener);
      return () => {
        events.delete(listener);
      };
    },
    onSnapshot(listener) {
      if (disposed) throw failure("CHAT_BRIDGE_DISPOSED");
      snapshots.add(listener);
      if (current) notify(new Set([listener]), current);
      return () => {
        snapshots.delete(listener);
      };
    },
    dispose() {
      if (disposed) return;
      disposed = true;
      imageController?.abort();
      nativePort.removeEventListener("message", receive);
      for (const request of pending.values()) {
        clearTimeout(request.timer);
        request.reject(failure("CHAT_BRIDGE_DISPOSED"));
      }
      pending.clear();
      events.clear();
      snapshots.clear();
      retiredSessions.clear();
      current = undefined;
    },
  };
}
