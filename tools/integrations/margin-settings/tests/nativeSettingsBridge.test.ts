import assert from "node:assert/strict";
import test from "node:test";
import { setImmediate as nextTurn } from "node:timers/promises";
import {
  createNativeModelConnectionController,
  createNativeThemeController,
  type ModelConnectionWebSnapshot,
  type NativeSettingsContext,
  type SettingsThemeEvent,
  type ThemeWebSnapshot,
} from "../src/nativeSettingsBridge.ts";

function deferred<T = unknown>() {
  let resolve!: (value: T) => void;
  let reject!: (reason: unknown) => void;
  const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no; });
  return { promise, resolve, reject };
}
type Call = { type: string; payload?: Record<string, unknown> };
function port(pageId: string, respond: (call: Call) => unknown | Promise<unknown>) {
  const calls: Call[] = [];
  const abort = new AbortController();
  const themeListeners = new Set<(event: SettingsThemeEvent) => void>();
  const waiters = new Set<{ type: string; index: number; resolve: (call: Call) => void }>();
  const context: NativeSettingsContext = {
    pageId, signal: abort.signal,
    async request(type, payload) {
      const call = { type, payload };
      calls.push(call);
      for (const waiter of waiters) {
        const match = calls.filter(item => item.type === waiter.type)[waiter.index];
        if (match) { waiters.delete(waiter); waiter.resolve(match); }
      }
      return respond(call);
    },
    onThemeChanged(listener) { themeListeners.add(listener); return () => { themeListeners.delete(listener); }; },
  };
  return {
    context, calls, abort, themeListeners,
    next(type: string, index = 0): Promise<Call> {
      const existing = calls.filter(call => call.type === type)[index];
      if (existing) return Promise.resolve(existing);
      return new Promise(resolve => { waiters.add({ type, index, resolve }); });
    },
  };
}
function snapshot(patch: Partial<ModelConnectionWebSnapshot> = {}): ModelConnectionWebSnapshot {
  return {
    providerId: "openai",
    providers: [
      { id: "openai", displayName: "OpenAI", defaultBaseUrl: "https://api.openai.com/v1" },
      { id: "custom-openai-compatible", displayName: "自定义 OpenAI-compatible", defaultBaseUrl: "http://127.0.0.1:1234/v1" },
    ],
    baseUrl: "https://api.openai.com/v1", modelId: "user-selected-model",
    availableModels: [{ id: "user-selected-model", displayName: "User selected", contextWindowTokens: null, maxOutputTokens: null }],
    contextWindowOverrideText: "", maxOutputTokensText: "4096", contextLimitText: "由模型提供",
    providerStatusText: "当前提供方", modelStatusText: "当前模型", keyStateText: "尚未保存密钥",
    isKeySaved: false, showReasoning: false, isBusy: false, statusText: "", errorText: "",
    ...patch,
  };
}

test("theme selection waits for host acknowledgement, stays ordered, and preserves the owner after failure", { timeout: 3000 }, async t => {
  const initial: ThemeWebSnapshot = { selected: "System", effective: "FgoDark", statusText: "跟随系统" };
  const first = deferred();
  let selections = 0;
  const host = port("Theme", call => {
    if (call.type === "theme.get") return initial;
    assert.equal(call.type, "theme.select");
    selections++;
    if (selections === 1) return first.promise;
    if (selections === 2) return { selected: "FgoDark", effective: "FgoDark", statusText: "已保存" };
    throw new Error("SETTINGS_UNAVAILABLE");
  });
  const controller = createNativeThemeController(host.context);
  t.after(() => controller.dispose());
  assert.equal((await controller.load()).ok, true);
  assert.strictEqual(controller.getSnapshot(), controller.getSnapshot());
  const light = controller.select("FgoLight");
  const dark = controller.select("FgoDark");
  await host.next("theme.select");
  assert.deepEqual(controller.getSnapshot().host, initial);
  assert.equal(selections, 1);
  first.resolve({ selected: "FgoLight", effective: "FgoLight", statusText: "已保存" });
  assert.equal((await light).ok, true);
  assert.equal((await dark).ok, true);
  assert.equal(controller.getSnapshot().host?.selected, "FgoDark");
  assert.deepEqual(host.calls.filter(call => call.type === "theme.select").map(call => call.payload), [
    { theme: "FgoLight" }, { theme: "FgoDark" },
  ]);
  const failed = await controller.select("System");
  assert.equal(failed.ok, false);
  assert.equal(controller.getSnapshot().host?.selected, "FgoDark");
  assert.ok(controller.getSnapshot().error);
  controller.dispose();
  assert.equal(host.themeListeners.size, 0);
});

test("a newer draft survives an older ACK and save waits for every pending draft ACK", { timeout: 3000 }, async t => {
  const first = deferred(), second = deferred(), saved = deferred();
  let edits = 0;
  const initial = snapshot();
  const finalUrl = "https://new.example.test/v1";
  const host = port("ModelConnection", call => {
    if (call.type === "modelConnection.setDraft") return ++edits === 1 ? first.promise : second.promise;
    if (call.type === "modelConnection.save") return saved.promise;
    return initial;
  });
  const controller = createNativeModelConnectionController(host.context);
  t.after(() => controller.dispose());
  await controller.load();
  controller.setDraft("baseUrl", "https://older.example.test/v1");
  await host.next("modelConnection.setDraft");
  controller.setDraft("baseUrl", finalUrl);
  const save = controller.save();
  assert.equal(controller.getSnapshot().draft.baseUrl, finalUrl);
  assert.equal(host.calls.some(call => call.type === "modelConnection.save"), false);
  first.resolve(snapshot({ baseUrl: "https://older.example.test/v1" }));
  const secondCall = await host.next("modelConnection.setDraft", 1);
  assert.deepEqual(secondCall.payload, { field: "baseUrl", value: finalUrl });
  assert.equal(controller.getSnapshot().draft.baseUrl, finalUrl);
  assert.equal(host.calls.some(call => call.type === "modelConnection.save"), false);
  second.resolve(snapshot({ baseUrl: finalUrl }));
  await host.next("modelConnection.save");
  assert.equal(controller.getSnapshot().busy, true);
  assert.notEqual(controller.getSnapshot().host?.statusText, "已保存");
  saved.resolve(snapshot({ baseUrl: finalUrl, statusText: "已保存" }));
  assert.equal((await save).ok, true);
  assert.equal(controller.getSnapshot().draft.baseUrl, finalUrl);
  assert.equal(controller.getSnapshot().draftPending, false);
});

test("a rejected draft ACK blocks save and keeps the intended input available for retry", { timeout: 3000 }, async t => {
  const initial = snapshot();
  const host = port("ModelConnection", call => {
    if (call.type === "modelConnection.setDraft") throw new Error("SETTINGS_INVALID_INPUT");
    return initial;
  });
  const controller = createNativeModelConnectionController(host.context);
  t.after(() => controller.dispose());
  await controller.load();
  controller.setDraft("contextWindowOverride", "not-a-number");
  assert.equal((await controller.flushDraft()).ok, false);
  const result = await controller.save();
  assert.equal(result.ok, false);
  assert.equal(controller.getSnapshot().draft.contextWindowOverrideText, "not-a-number");
  assert.equal(controller.getSnapshot().draftPending, true);
  assert.equal(host.calls.some(call => call.type === "modelConnection.save"), false);
});

test("switching provider discards unsent old-provider secrets and keeps newer endpoint input", { timeout: 3000 }, async t => {
  const oldEdit = deferred(), providerAck = deferred();
  const nextProvider = "custom-openai-compatible";
  const nextBase = "http://127.0.0.1:4321/v1";
  let edits = 0;
  const host = port("ModelConnection", call => {
    if (call.type !== "modelConnection.setDraft") return snapshot();
    edits++;
    if (edits === 1) return oldEdit.promise;
    if (edits === 2) return providerAck.promise;
    return snapshot({ providerId: nextProvider, baseUrl: nextBase });
  });
  const controller = createNativeModelConnectionController(host.context);
  t.after(() => controller.dispose());
  await controller.load();
  controller.setDraft("baseUrl", "https://old.example.test/v1");
  await host.next("modelConnection.setDraft");
  controller.setApiKey("synthetic-secret-for-old-provider");
  controller.setDraft("providerId", nextProvider);
  controller.setDraft("baseUrl", nextBase);
  assert.equal(controller.getSnapshot().pendingKey, false);
  assert.equal(controller.getSnapshot().keyResetVersion, 1);
  oldEdit.resolve(snapshot({ baseUrl: "https://old.example.test/v1" }));
  assert.deepEqual((await host.next("modelConnection.setDraft", 1)).payload, { field: "providerId", value: nextProvider });
  providerAck.resolve(snapshot({ providerId: nextProvider, baseUrl: "http://127.0.0.1:1234/v1" }));
  await controller.flushDraft();
  assert.equal(controller.getSnapshot().draft.baseUrl, nextBase);
  assert.equal(host.calls.some(call => call.payload?.field === "apiKey"), false);
  assert.equal(controller.getSnapshot().draftPending, false);
});

test("keys are write-only and a failed save snapshot never becomes successful feedback", { timeout: 3000 }, async t => {
  const secret = "synthetic-api-key-not-a-real-credential";
  let saves = 0;
  const host = port("ModelConnection", call => {
    if (call.type === "modelConnection.save") {
      saves++;
      if (saves === 1) throw Object.assign(new Error("SETTINGS_SAVE_FAILED"), {
        details: { snapshot: snapshot({ errorText: "示例：这次保存没有完成。" }) },
      });
      return snapshot({ isKeySaved: true, statusText: "已保存", keyStateText: "已保存密钥" });
    }
    return snapshot();
  });
  const controller = createNativeModelConnectionController(host.context);
  t.after(() => controller.dispose());
  await controller.load();
  controller.setApiKey(secret);
  await controller.flushDraft();
  assert.equal(JSON.stringify(controller.getSnapshot()).includes(secret), false);
  assert.deepEqual(host.calls.find(call => call.payload?.field === "apiKey")?.payload, { field: "apiKey", value: secret });
  const failed = await controller.save();
  assert.equal(failed.ok, false);
  if (!failed.ok) assert.equal(failed.message, "示例：这次保存没有完成。");
  assert.equal(controller.getSnapshot().host?.isKeySaved, false);
  assert.equal(controller.getSnapshot().pendingKey, true);
  assert.equal(controller.getSnapshot().keyResetVersion, 0);
  assert.equal(controller.getSnapshot().error, "示例：这次保存没有完成。");
  assert.equal((await controller.save()).ok, true);
  assert.equal(controller.getSnapshot().pendingKey, false);
  assert.equal(controller.getSnapshot().keyResetVersion, 1);
  assert.equal(JSON.stringify(controller.getSnapshot()).includes(secret), false);
});

test("a stale background model list cannot replace a newer endpoint or its selected model", { timeout: 3000 }, async t => {
  const older = deferred(), newer = deferred();
  const nextBase = "https://new.example.test/v1";
  let refreshes = 0;
  const host = port("ModelConnection", call => {
    if (call.type === "modelConnection.refreshModels") return ++refreshes === 1 ? older.promise : newer.promise;
    if (call.type === "modelConnection.setDraft") return snapshot({ baseUrl: nextBase });
    return snapshot();
  });
  const controller = createNativeModelConnectionController(host.context);
  t.after(() => controller.dispose());
  await controller.load();
  await host.next("modelConnection.refreshModels");
  controller.setDraft("baseUrl", nextBase);
  await controller.flushDraft();
  const refresh = controller.refreshModels();
  await host.next("modelConnection.refreshModels", 1);
  older.resolve(snapshot({ availableModels: [{ id: "stale-model", displayName: "Old", contextWindowTokens: null, maxOutputTokens: null }] }));
  await nextTurn();
  assert.equal(controller.getSnapshot().host?.availableModels.some(model => model.id === "stale-model"), false);
  newer.resolve(snapshot({ baseUrl: nextBase, modelId: "server-suggested-model",
    availableModels: [{ id: "fresh-model", displayName: "Fresh", contextWindowTokens: 32000, maxOutputTokens: null }],
    contextLimitText: "32,000", statusText: "已读取模型列表" }));
  assert.equal((await refresh).ok, true);
  assert.equal(controller.getSnapshot().draft.baseUrl, nextBase);
  assert.equal(controller.getSnapshot().draft.modelId, "user-selected-model");
  assert.equal(controller.getSnapshot().host?.modelId, "user-selected-model");
  assert.deepEqual(controller.getSnapshot().host?.availableModels.map(model => model.id), ["fresh-model"]);
});

test("an explicit connection test supersedes an older background refresh, including its late failure", { timeout: 3000 }, async t => {
  const older = deferred();
  const host = port("ModelConnection", call => {
    if (call.type === "modelConnection.refreshModels") return older.promise;
    if (call.type === "modelConnection.test") return snapshot({ statusText: "宿主：本次检查已完成" });
    return snapshot();
  });
  const controller = createNativeModelConnectionController(host.context);
  t.after(() => controller.dispose());
  await controller.load();
  await host.next("modelConnection.refreshModels");
  assert.equal((await controller.test()).ok, true);
  older.reject(Object.assign(new Error("SETTINGS_TEST_FAILED"), { details: { snapshot: snapshot({ errorText: "旧请求失败" }) } }));
  await nextTurn();
  assert.equal(controller.getSnapshot().host?.statusText, "宿主：本次检查已完成");
  assert.equal(controller.getSnapshot().error, null);
});

test("page abort cancels queued save and a late ACK cannot change either page controller", { timeout: 3000 }, async t => {
  const late = deferred();
  const firstHost = port("ModelConnection", call => call.type === "modelConnection.setDraft" ? late.promise : snapshot());
  const first = createNativeModelConnectionController(firstHost.context);
  t.after(() => first.dispose());
  await first.load();
  first.setDraft("modelId", "old-page-edit");
  await firstHost.next("modelConnection.setDraft");
  const saved = first.save();
  firstHost.abort.abort();
  const result = await saved;
  assert.equal(result.ok, false);
  if (!result.ok) assert.equal(result.kind, "canceled");
  const previous = first.getSnapshot();
  const secondHost = port("ModelConnection", () => snapshot({ modelId: "new-page-owner" }));
  const second = createNativeModelConnectionController(secondHost.context);
  t.after(() => second.dispose());
  await second.load();
  late.resolve(snapshot({ modelId: "late-old-page-value", statusText: "旧结果" }));
  await nextTurn();
  assert.strictEqual(first.getSnapshot(), previous);
  assert.equal(second.getSnapshot().draft.modelId, "new-page-owner");
  assert.equal(firstHost.calls.some(call => call.type === "modelConnection.save"), false);
});
