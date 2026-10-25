/** Settings-page adapters for fgo_pet@303219fdd686f6321b362e2ed17cd7a79e3f2cdc.
 * Requests belong to the existing settings root, which supplies pageId, routing,
 * timeouts and command.result handling. This file never uses the Chat bridge. */

export type ThemeSelection = "System" | "FgoLight" | "FgoDark";
export type ThemeWebSnapshot = {
  selected: ThemeSelection;
  effective: "FgoLight" | "FgoDark";
  statusText: string;
};
export type ThemeSnapshot = ThemeWebSnapshot;

export type ModelConnectionWebSnapshot = {
  providerId: string;
  providers: Array<{ id: string; displayName: string; defaultBaseUrl: string }>;
  baseUrl: string;
  modelId: string;
  availableModels: Array<{
    id: string;
    displayName: string;
    contextWindowTokens: number | null;
    maxOutputTokens: number | null;
  }>;
  contextWindowOverrideText: string;
  maxOutputTokensText: string;
  contextLimitText: string;
  providerStatusText: string;
  modelStatusText: string;
  keyStateText: string;
  isKeySaved: boolean;
  showReasoning: boolean;
  isBusy: boolean;
  statusText: string;
  errorText: string;
};

export type SettingsThemeEvent = {
  type: "theme.changed";
  version: number;
  variables: Record<string, string>;
};
export interface SettingsModuleContext {
  pageId: string;
  overview: boolean;
  itemId?: string | null;
  state?: unknown;
  signal: AbortSignal;
  request(type: string, payload?: Record<string, unknown>): Promise<unknown>;
  navigate(pageId: string): Promise<boolean>;
  canNavigate(pageId: string): boolean;
  onRouteChanged(): void;
  onThemeChanged(callback: (event: SettingsThemeEvent) => void): () => void;
}
export type NativeSettingsContext = Pick<SettingsModuleContext, "pageId" | "signal" | "request"> &
  Partial<Pick<SettingsModuleContext, "onThemeChanged">>;

export type SettingsActionResult =
  | { ok: true }
  | { ok: false; kind: "canceled" | "host" | "validation"; code: string; message: string };
export type SettingsAction = "load" | "select" | "draft" | "test" | "refreshModels" |
  "save" | "clearKey" | "setShowReasoning";
export type SettingsControllerState<Snapshot> = {
  revision: number;
  ready: boolean;
  host: Snapshot | null;
  busy: boolean;
  activeAction: SettingsAction | null;
  error: string | null;
  errorCode: string | null;
};
export type ThemeControllerState = SettingsControllerState<ThemeWebSnapshot>;
export type ModelConnectionDraft = Pick<ModelConnectionWebSnapshot,
  "providerId" | "baseUrl" | "modelId" | "contextWindowOverrideText" | "maxOutputTokensText">;
export type ModelConnectionDraftField = "providerId" | "baseUrl" | "modelId" |
  "contextWindowOverride" | "maxOutputTokens";
export type ModelConnectionControllerState = SettingsControllerState<ModelConnectionWebSnapshot> & {
  draft: ModelConnectionDraft;
  /** Unacknowledged input, not a claim about the host's saved configuration. */
  draftPending: boolean;
  pendingKey: boolean;
  /** Remount/reset the write-only password input when this changes. */
  keyResetVersion: number;
  refreshingModels: boolean;
};
interface SettingsStore<State> {
  getSnapshot(): State;
  subscribe(listener: () => void): () => void;
  load(): Promise<SettingsActionResult>;
  dispose(): void;
}
export interface NativeThemeController extends SettingsStore<ThemeControllerState> {
  select(theme: ThemeSelection): Promise<SettingsActionResult>;
}
export interface NativeModelConnectionController extends SettingsStore<ModelConnectionControllerState> {
  setDraft(field: ModelConnectionDraftField, value: string): void;
  setApiKey(value: string): void;
  flushDraft(): Promise<SettingsActionResult>;
  test(): Promise<SettingsActionResult>;
  refreshModels(): Promise<SettingsActionResult>;
  save(): Promise<SettingsActionResult>;
  clearKey(): Promise<SettingsActionResult>;
  setShowReasoning(value: boolean): Promise<SettingsActionResult>;
}

const success = (): SettingsActionResult => ({ ok: true });
const record = (value: unknown): value is Record<string, unknown> =>
  value !== null && typeof value === "object" && !Array.isArray(value);
const string = (value: unknown): value is string => typeof value === "string";
const themes = new Set<unknown>(["System", "FgoLight", "FgoDark"]);
const canceledCodes = new Set(["SETTINGS_PAGE_CHANGED", "SETTINGS_CANCELED", "SETTINGS_RESULT_SUPERSEDED"]);
const labels: Record<string, string> = {
  SETTINGS_PAGE_CHANGED: "设置页已切换，本页不再接收这次操作结果。",
  SETTINGS_CANCELED: "设置页已关闭，本页不再接收这次操作结果。",
  SETTINGS_RESULT_SUPERSEDED: "输入已更新，这次较早的结果未用于当前草稿。",
  SETTINGS_TIMEOUT: "桌面宿主尚未返回，暂时无法确认操作结果。请重新读取设置后检查。",
  SETTINGS_INVALID_INPUT: "输入不符合当前设置页的要求，请检查后重试。",
  SETTINGS_INVALID_SNAPSHOT: "桌面返回的设置数据不完整，请重新读取。",
  SETTINGS_PAGE_MISMATCH: "当前设置页不能执行这项操作。",
  SETTINGS_NOT_READY: "请等待桌面设置读取完成。",
  SETTINGS_TEST_FAILED: "连接检查或模型列表读取未完成，请查看宿主返回的信息。",
  SETTINGS_SAVE_FAILED: "连接未能保存，请检查输入后重试。",
  SETTINGS_UNAVAILABLE: "设置操作未完成，请重试。",
};
function fault(code: string): Error {
  return new Error(code);
}
function failure(error: unknown, detail?: string): Exclude<SettingsActionResult, { ok: true }> {
  const candidate = error instanceof Error ? error.message : "";
  const code = /^SETTINGS_[A-Z_]+$/.test(candidate) ? candidate : "SETTINGS_UNAVAILABLE";
  return {
    ok: false,
    kind: canceledCodes.has(code) ? "canceled" :
      ["SETTINGS_INVALID_INPUT", "SETTINGS_PAGE_MISMATCH", "SETTINGS_NOT_READY"].includes(code) ? "validation" : "host",
    code,
    message: detail || labels[code] || labels.SETTINGS_UNAVAILABLE,
  };
}
function errorDetails(error: unknown): unknown {
  return record(error) ? error.details : undefined;
}
function themeSnapshot(value: unknown): ThemeWebSnapshot {
  if (!record(value) || !themes.has(value.selected) ||
    !["FgoLight", "FgoDark"].includes(String(value.effective)) || !string(value.statusText))
    throw fault("SETTINGS_INVALID_SNAPSHOT");
  return { selected: value.selected as ThemeSelection,
    effective: value.effective as ThemeWebSnapshot["effective"], statusText: value.statusText };
}
function modelSnapshot(value: unknown): ModelConnectionWebSnapshot {
  if (record(value) && record(value.snapshot)) value = value.snapshot;
  if (!record(value)) throw fault("SETTINGS_INVALID_SNAPSHOT");
  const textFields = ["providerId", "baseUrl", "modelId", "contextWindowOverrideText", "maxOutputTokensText",
    "contextLimitText", "providerStatusText", "modelStatusText", "keyStateText", "statusText", "errorText"] as const;
  if (!textFields.every(field => string(value[field])) ||
    !["isKeySaved", "showReasoning", "isBusy"].every(field => typeof value[field] === "boolean") ||
    !Array.isArray(value.providers) || !Array.isArray(value.availableModels))
    throw fault("SETTINGS_INVALID_SNAPSHOT");
  const providers = value.providers.map(provider => {
    if (!record(provider) || ![provider.id, provider.displayName, provider.defaultBaseUrl].every(string))
      throw fault("SETTINGS_INVALID_SNAPSHOT");
    return { id: provider.id as string, displayName: provider.displayName as string, defaultBaseUrl: provider.defaultBaseUrl as string };
  });
  const availableModels = value.availableModels.map(model => {
    if (!record(model) || !string(model.id) || !string(model.displayName)) throw fault("SETTINGS_INVALID_SNAPSHOT");
    const numberOrNull = (input: unknown) => {
      if (input === null || input === undefined) return null;
      if (typeof input !== "number" || !Number.isFinite(input)) throw fault("SETTINGS_INVALID_SNAPSHOT");
      return input;
    };
    return { id: model.id, displayName: model.displayName,
      contextWindowTokens: numberOrNull(model.contextWindowTokens), maxOutputTokens: numberOrNull(model.maxOutputTokens) };
  });
  // Select only documented fields. In particular, never retain an unexpected key value.
  return { ...Object.fromEntries(textFields.map(field => [field, value[field]])), providers, availableModels,
    isKeySaved: value.isKeySaved, showReasoning: value.showReasoning, isBusy: value.isBusy } as ModelConnectionWebSnapshot;
}
function tryModelSnapshot(value: unknown): ModelConnectionWebSnapshot | undefined {
  try { return modelSnapshot(value); } catch { return undefined; }
}

function baseState<Snapshot>(): SettingsControllerState<Snapshot> {
  return { revision: 0, ready: false, host: null, busy: false, activeAction: null, error: null, errorCode: null };
}
function createStore<State extends { revision: number }>(context: NativeSettingsContext, pageId: string, initial: State) {
  let state = initial;
  let disposed = false;
  const controller = new AbortController();
  const listeners = new Set<() => void>();
  const cleanups = new Set<() => void>();
  const live = () => !disposed && !context.signal.aborted;
  function publish(patch: Partial<State>) {
    if (!live()) return;
    state = { ...state, ...patch, revision: state.revision + 1 };
    for (const listener of listeners) {
      try { listener(); } catch { /* A view listener must not interrupt an acknowledged command. */ }
    }
  }
  function dispose() {
    if (disposed) return;
    disposed = true;
    controller.abort();
    context.signal.removeEventListener("abort", dispose);
    for (const cleanup of cleanups) cleanup();
    cleanups.clear();
    listeners.clear();
  }
  context.signal.addEventListener("abort", dispose, { once: true });
  if (context.signal.aborted) dispose();
  async function request(type: string, payload?: Record<string, unknown>): Promise<unknown> {
    if (!live()) throw fault("SETTINGS_CANCELED");
    if (context.pageId !== pageId) throw fault("SETTINGS_PAGE_MISMATCH");
    // Root normally rejects on route changes too; this race also bounds consumers
    // if cleanup happens while an injected/context request remains unresolved.
    return new Promise((resolve, reject) => {
      let settled = false;
      const finish = (value: unknown, failed: boolean) => {
        if (settled) return;
        settled = true;
        controller.signal.removeEventListener("abort", abort);
        if (failed) reject(value); else resolve(value);
      };
      const abort = () => finish(fault("SETTINGS_CANCELED"), true);
      controller.signal.addEventListener("abort", abort, { once: true });
      Promise.resolve().then(() => {
        if (!live()) throw fault("SETTINGS_CANCELED");
        return context.request(type, payload);
      }).then(value => {
        if (!live()) abort(); else finish(value, false);
      }, error => finish(error, true));
    });
  }
  return {
    getSnapshot: () => state,
    subscribe(listener: () => void) {
      if (!live()) return () => {};
      listeners.add(listener);
      return () => { listeners.delete(listener); };
    },
    publish, live, request, dispose,
    onDispose(cleanup: () => void) { if (disposed) cleanup(); else cleanups.add(cleanup); },
  };
}

export function createNativeThemeController(context: NativeSettingsContext): NativeThemeController {
  const store = createStore(context, "Theme", baseState<ThemeWebSnapshot>());
  let tail = Promise.resolve();
  let queued = 0;
  let loadTask: Promise<SettingsActionResult> | undefined;
  function run(action: "load" | "select", command: string, payload?: Record<string, unknown>) {
    queued++;
    store.publish({ busy: true });
    const result = tail.then(async (): Promise<SettingsActionResult> => {
      if (!store.live()) return failure(fault("SETTINGS_CANCELED"));
      store.publish({ activeAction: action, error: null, errorCode: null });
      try {
        const host = themeSnapshot(await store.request(command, payload));
        store.publish({ host, ready: true });
        return success();
      } catch (error) {
        const result = failure(error);
        if (result.kind !== "canceled") store.publish({ error: result.message, errorCode: result.code });
        return result;
      } finally {
        queued--;
        store.publish({ busy: queued > 0, activeAction: null });
      }
    });
    tail = result.then(() => {});
    return result;
  }
  function load() {
    if (loadTask) return loadTask;
    loadTask = run("load", "theme.get").finally(() => { loadTask = undefined; });
    return loadTask;
  }
  if (context.onThemeChanged && store.live()) {
    const unsubscribe = context.onThemeChanged(() => { if (store.live()) void load(); });
    store.onDispose(unsubscribe);
  }
  return {
    getSnapshot: store.getSnapshot, subscribe: store.subscribe, load, dispose: store.dispose,
    select(theme) {
      if (!themes.has(theme)) return Promise.resolve(failure(fault("SETTINGS_INVALID_INPUT")));
      return run("select", "theme.select", { theme });
    },
  };
}

const draftFields: Record<ModelConnectionDraftField, keyof ModelConnectionDraft> = {
  providerId: "providerId", baseUrl: "baseUrl", modelId: "modelId",
  contextWindowOverride: "contextWindowOverrideText", maxOutputTokens: "maxOutputTokensText",
};
const draftKeys = Object.values(draftFields);
type Edit = { field: ModelConnectionDraftField | "apiKey"; value: string; revision: number; providerEpoch: number };

export function createNativeModelConnectionController(context: NativeSettingsContext): NativeModelConnectionController {
  const store = createStore(context, "ModelConnection", {
    ...baseState<ModelConnectionWebSnapshot>(),
    draft: { providerId: "", baseUrl: "", modelId: "", contextWindowOverrideText: "", maxOutputTokensText: "" },
    draftPending: false, pendingKey: false, keyResetVersion: 0, refreshingModels: false,
  } as ModelConnectionControllerState);
  let tail = Promise.resolve();
  let explicitQueued = 0;
  let revision = 0;
  let providerEpoch = 0;
  let keyRevision = 0;
  const fieldRevision = Object.fromEntries(draftKeys.map(field => [field, 0])) as Record<keyof ModelConnectionDraft, number>;
  // Secret values exist only in this private pending queue and the direct request.
  const pending = new Map<Edit["field"], Edit>();
  let activeEdit: Edit | undefined;
  let flushTask: Promise<SettingsActionResult> | undefined;
  let loadTask: Promise<SettingsActionResult> | undefined;
  type ModelsFlight = { promise: Promise<SettingsActionResult> };
  let modelsFlight: ModelsFlight | undefined;

  function invalidateModels() {
    modelsFlight = undefined;
    store.publish({ refreshingModels: false });
  }
  function pendingChanged() { store.publish({ draftPending: pending.size > 0 || !!activeEdit }); }
  function apply(host: ModelConnectionWebSnapshot, acknowledgedRevision: number, epoch: number) {
    if (!store.live() || epoch !== providerEpoch) return;
    const draft = { ...store.getSnapshot().draft };
    for (const field of draftKeys) {
      if (fieldRevision[field] <= acknowledgedRevision) draft[field] = host[field];
    }
    store.publish({ host, draft, ready: true, error: host.errorText || null, errorCode: null });
  }
  function run(action: SettingsAction, task: () => Promise<void>, explicit = true): Promise<SettingsActionResult> {
    if (explicit) explicitQueued++;
    store.publish({ busy: explicitQueued > 0 });
    const result = tail.then(async (): Promise<SettingsActionResult> => {
      if (!store.live()) return failure(fault("SETTINGS_CANCELED"));
      store.publish({ activeAction: action, error: null, errorCode: null });
      try {
        await task();
        if (!store.live()) return failure(fault("SETTINGS_CANCELED"));
        return success();
      } catch (error) {
        const failedHost = tryModelSnapshot(errorDetails(error));
        const result = failure(error, failedHost?.errorText || undefined);
        if (result.kind !== "canceled") store.publish({ error: result.message, errorCode: result.code });
        return result;
      } finally {
        if (explicit) explicitQueued--;
        store.publish({ busy: explicitQueued > 0, activeAction: null });
      }
    });
    tail = result.then(() => {});
    return result;
  }
  async function read(command: string, payload?: Record<string, unknown>) {
    const requestRevision = revision;
    const epoch = providerEpoch;
    try {
      const host = modelSnapshot(await store.request(command, payload));
      apply(host, requestRevision, epoch);
      return { host, revision: requestRevision, providerEpoch: epoch };
    } catch (error) {
      const host = tryModelSnapshot(errorDetails(error));
      if (host) apply(host, requestRevision, epoch);
      throw error;
    }
  }
  function requireReady() {
    if (!store.getSnapshot().ready) throw fault("SETTINGS_NOT_READY");
  }
  async function drain() {
    requireReady();
    while (pending.size > 0) {
      const edit = pending.values().next().value!;
      pending.delete(edit.field);
      activeEdit = edit;
      pendingChanged();
      try {
        const host = modelSnapshot(await store.request("modelConnection.setDraft", { field: edit.field, value: edit.value }));
        apply(host, edit.revision, edit.providerEpoch);
      } catch (error) {
        if (!store.live()) throw error;
        const latest = edit.field === "apiKey" ? keyRevision : fieldRevision[draftFields[edit.field]];
        // A newer user edit supersedes a failed earlier value too. Retry only
        // the latest intended input, never an old provider's secret or endpoint.
        if (edit.providerEpoch !== providerEpoch || latest > edit.revision) continue;
        if (!pending.has(edit.field)) pending.set(edit.field, edit);
        throw error;
      } finally {
        activeEdit = undefined;
        pendingChanged();
      }
    }
  }
  function flushDraft(): Promise<SettingsActionResult> {
    if (!store.live()) return Promise.resolve(failure(fault("SETTINGS_CANCELED")));
    if (flushTask) return flushTask;
    if (!pending.size && !activeEdit) return Promise.resolve(success());
    const task = run("draft", drain, false);
    flushTask = task.then(result => {
      flushTask = undefined;
      if (result.ok && pending.size && store.live()) void flushDraft();
      return result;
    });
    return flushTask;
  }
  function input(field: Edit["field"], value: string) {
    if (!store.live() || !store.getSnapshot().ready) return;
    if (typeof value !== "string" || (field !== "apiKey" && !Object.hasOwn(draftFields, field))) return;
    invalidateModels();
    revision++;
    if (field === "providerId") {
      providerEpoch++;
      pending.clear();
      keyRevision = revision;
      for (const key of draftKeys) fieldRevision[key] = revision;
      store.publish({ pendingKey: false, keyResetVersion: store.getSnapshot().keyResetVersion + 1 });
    }
    if (field === "apiKey") {
      keyRevision = revision;
      store.publish({ pendingKey: value.length > 0 });
    } else {
      const key = draftFields[field];
      fieldRevision[key] = revision;
      store.publish({ draft: { ...store.getSnapshot().draft, [key]: value } });
    }
    pending.delete(field);
    pending.set(field, { field, value, revision, providerEpoch });
    pendingChanged();
    void flushDraft();
  }
  function action(name: "test" | "save" | "clearKey" | "setShowReasoning", payload?: Record<string, unknown>) {
    invalidateModels();
    return run(name, async () => {
      await drain();
      const result = await read(`modelConnection.${name}`, payload);
      if ((name === "save" || name === "clearKey") && result.providerEpoch === providerEpoch && keyRevision <= result.revision) {
        store.publish({ pendingKey: false, keyResetVersion: store.getSnapshot().keyResetVersion + 1 });
      }
    });
  }
  function load(): Promise<SettingsActionResult> {
    if (loadTask) return loadTask;
    invalidateModels();
    loadTask = run("load", async () => {
      if (store.getSnapshot().ready) await drain();
      await read("modelConnection.get");
    }).then(result => {
      loadTask = undefined;
      if (result.ok && store.live()) void refreshModels();
      return result;
    });
    return loadTask;
  }
  function refreshModels(): Promise<SettingsActionResult> {
    if (modelsFlight) return modelsFlight.promise;
    if (!store.live()) return Promise.resolve(failure(fault("SETTINGS_CANCELED")));
    const flight: ModelsFlight = { promise: Promise.resolve(success()) };
    modelsFlight = flight;
    store.publish({ refreshingModels: true });
    const current = () => store.live() && modelsFlight === flight;
    flight.promise = (async (): Promise<SettingsActionResult> => {
      const flushed = await flushDraft();
      if (!flushed.ok) return flushed;
      await tail;
      if (!current()) return failure(fault("SETTINGS_RESULT_SUPERSEDED"));
      const owner = store.getSnapshot().host;
      if (!owner) return failure(fault("SETTINGS_NOT_READY"));
      const requestRevision = revision;
      const matches = (snapshot: ModelConnectionWebSnapshot) => current() && revision === requestRevision &&
        snapshot.providerId === owner.providerId && snapshot.baseUrl === owner.baseUrl;
      const merge = (snapshot: ModelConnectionWebSnapshot) => {
        if (!matches(snapshot)) return false;
        const host = store.getSnapshot().host!;
        store.publish({ host: { ...host, availableModels: snapshot.availableModels,
          contextLimitText: snapshot.contextLimitText, statusText: snapshot.statusText, errorText: snapshot.errorText },
          error: snapshot.errorText || null, errorCode: null });
        return true;
      };
      try {
        const updated = modelSnapshot(await store.request("modelConnection.refreshModels"));
        if (!merge(updated)) return failure(fault("SETTINGS_RESULT_SUPERSEDED"));
        return success();
      } catch (error) {
        if (!current() || revision !== requestRevision) return failure(fault("SETTINGS_RESULT_SUPERSEDED"));
        const updated = tryModelSnapshot(errorDetails(error));
        if (updated && !matches(updated)) return failure(fault("SETTINGS_RESULT_SUPERSEDED"));
        if (updated) merge(updated);
        const result = failure(error, updated?.errorText || undefined);
        if (result.kind !== "canceled") store.publish({ error: result.message, errorCode: result.code });
        return result;
      }
    })().finally(() => {
      if (modelsFlight === flight) {
        modelsFlight = undefined;
        store.publish({ refreshingModels: false });
      }
    });
    return flight.promise;
  }
  store.onDispose(() => {
    pending.clear();
    activeEdit = undefined;
    modelsFlight = undefined;
  });
  return {
    getSnapshot: store.getSnapshot, subscribe: store.subscribe, load, dispose: store.dispose,
    setDraft: input, setApiKey: value => input("apiKey", value), flushDraft,
    test: () => action("test"), refreshModels,
    save: () => action("save"), clearKey: () => action("clearKey"),
    setShowReasoning: value => {
      if (typeof value !== "boolean") return Promise.resolve(failure(fault("SETTINGS_INVALID_INPUT")));
      return action("setShowReasoning", { value });
    },
  };
}
