import assert from "node:assert/strict";
import test, { type TestContext } from "node:test";
import { createNativeBridge } from "../src/nativeBridge.ts";
import { createNativeDriver } from "../src/nativeDriver.ts";
import { MockNativeHost, pause, tick } from "./nativeHostFixture.ts";

function setup(t: TestContext) {
  const host = new MockNativeHost(),
    restore = host.install();
  const bridge = createNativeBridge()!,
    driver = createNativeDriver(bridge);
  t.after(() => {
    driver.dispose();
    restore();
  });
  return { host, driver };
}

test("persisted sticker metadata survives snapshots and is withheld for streaming and user turns", async t => {
  const { host, driver } = setup(t);
  const turn = host.state.conversation.turns[0];
  Object.assign(turn, { stickerId: "mash.happy", stickerPosition: "after", stickerLabel: "开心" });
  await driver.connect();
  assert.equal(driver.getSnapshot().messages[0].stickerId, "mash.happy");
  turn.isStreaming = true;
  host.publish(); await tick();
  assert.equal(driver.getSnapshot().messages[0].stickerId, undefined);
  turn.isStreaming = false; turn.role = "user";
  host.publish(); await tick();
  assert.equal(driver.getSnapshot().messages[0].stickerId, undefined);
  turn.role = "assistant";
  host.publish(); await tick();
  assert.equal(driver.getSnapshot().messages[0].stickerId, "mash.happy");
  assert.equal(driver.getSnapshot().messages[0].stickerPosition, "after");
});

test("explicit expansion supersedes queued stream heights without changing the draft", async t => {
  const { host, driver } = setup(t);
  await driver.connect();
  driver.setDraft("A draft to keep while reading");
  host.hold("chat.host");
  const first = driver.presentation("composer", false, 200);
  await tick();
  const oldHeight = driver.presentation("composer", false, 260);
  const latestHeight = driver.presentation("composer", false, 320);
  const expanded = driver.presentation("conversation", true);
  host.release("chat.host");
  await Promise.all([first, oldHeight, latestHeight, expanded]);
  const modes = host.posts.filter(item => item.type === "chat.host" && item.payload?.action === "setPresentation");
  assert.deepEqual(modes.map(item => [item.payload?.view, item.payload?.composerHeight]), [
    ["composer", 200], ["conversation", undefined],
  ]);
  assert.equal(driver.getSnapshot().hostPresentation?.view, "conversation");
  assert.equal(driver.getSnapshot().draft, "A draft to keep while reading");
});

test("a stale resize during first-conversation admission cannot turn successful generation into failure", async t => {
  const { host, driver } = setup(t);
  host.state.identity.conversationId = "";
  await driver.connect();
  host.hold("chat.host");
  const resize = driver.presentation("composer", false, 240)
    .then(() => undefined, error => error);
  await tick();
  await driver.send("First message");
  host.release("chat.host", "CHAT_IDENTITY_STALE");
  assert.equal(await resize, undefined);
  assert.equal(driver.getSnapshot().conversationId, "admitted-conversation");
  assert.equal(driver.getSnapshot().canStop, true);
  assert.equal(driver.getSnapshot().error, undefined);
  assert.equal(driver.getSnapshot().phase, "waiting");
  Object.assign(host.state.conversation, {
    isStreaming: false, isThinking: false, canStop: false, hasPendingRun: false, errorText: "",
  });
  host.publish();
  assert.equal(driver.getSnapshot().phase, "idle");
  assert.equal(driver.getSnapshot().error, undefined);
  assert.equal(host.posts.filter(item => item.payload?.action === "setPresentation").length, 1);
  assert.equal(host.posts.filter(item => item.type === "chat.send").length, 1);
});

test("a delayed resize rejection from a previous conversation does not enter the current turn", async t => {
  const { host, driver } = setup(t);
  await driver.connect();
  host.hold("chat.host");
  const resize = driver.presentation("composer", false, 240)
    .then(() => undefined, error => error);
  await tick();
  host.state.identity.conversationId = "conversation-b";
  host.publish();
  host.release("chat.host", "CHAT_IDENTITY_STALE");
  assert.equal(await resize, undefined);
  assert.equal(driver.getSnapshot().conversationId, "conversation-b");
  assert.equal(driver.getSnapshot().phase, "idle");
  assert.equal(driver.getSnapshot().error, undefined);
});

test("window adjustment failures cannot replace a native generation error", async t => {
  const { host, driver } = setup(t);
  host.state.conversation.errorText = "Synthetic provider failure";
  await driver.connect();
  for (const code of ["CHAT_IDENTITY_STALE", "CHAT_TIMEOUT"]) {
    host.hold("chat.host");
    const resize = driver.presentation("conversation", true)
      .then(() => undefined, error => error);
    await tick();
    host.release("chat.host", code);
    const error = await resize;
    if (code === "CHAT_TIMEOUT") assert.match(error?.message || "", /CHAT_TIMEOUT/);
    else assert.equal(error, undefined);
    assert.equal(driver.getSnapshot().error, "Synthetic provider failure");
    assert.equal(driver.getSnapshot().phase, "error");
  }
  await driver.presentation("composer", false, 260);
  assert.equal(driver.getSnapshot().error, "Synthetic provider failure");
});

test("a real send failure remains visible after a stale window adjustment", async t => {
  const { host, driver } = setup(t);
  await driver.connect();
  host.hold("chat.send");
  const sending = driver.send("A rejected message");
  const rejected = assert.rejects(sending, /CHAT_NOT_AVAILABLE/);
  await tick();
  Object.assign(host.state.conversation, {
    isStreaming: false, isThinking: false, canStop: false, hasPendingRun: false,
  });
  host.publish();
  host.release("chat.send", "CHAT_NOT_AVAILABLE");
  await rejected;
  const sendError = driver.getSnapshot().error;
  assert.ok(sendError);
  assert.equal(driver.getSnapshot().phase, "error");
  host.hold("chat.host");
  const resize = driver.presentation("composer", false, 240)
    .then(() => undefined, error => error);
  await tick();
  host.release("chat.host", "CHAT_IDENTITY_STALE");
  assert.equal(await resize, undefined);
  assert.equal(driver.getSnapshot().error, sendError);
});

test("send keeps the draft until ACK and preserves typing during first-conversation admission", async (t) => {
  const { host, driver } = setup(t);
  host.state.identity.conversationId = "";
  await driver.connect();
  host.hold("chat.send");
  driver.setDraft("First message");
  const sending = driver.send("First message");
  await tick();
  assert.equal(driver.getSnapshot().draft, "First message");
  assert.equal(driver.getSnapshot().conversationId, "admitted-conversation");
  await assert.rejects(driver.send("duplicate"), /CHAT_SEND_BUSY/);
  driver.setDraft("New typing while waiting");
  host.release("chat.send");
  await sending;
  assert.equal(driver.getSnapshot().draft, "New typing while waiting");
  await pause(170);
  assert.equal(host.state.draft.text, "New typing while waiting");
  assert.equal(host.posts.filter((p) => p.type === "chat.send").length, 1);
  await driver.stop();
  assert.equal(driver.getSnapshot().canStop, false);
  assert.equal(driver.getSnapshot().draft, "New typing while waiting");
});

test("a queued send cannot migrate to a different native owner or leave its error there", async (t) => {
  const { host, driver } = setup(t);
  await driver.connect();
  host.hold("chat.draft");
  driver.setDraft("Draft revision one");
  await pause(170);
  assert.equal(host.held.length, 1);
  driver.setDraft("Draft revision two");
  const sending = driver.send("Draft revision two");
  const rejected = assert.rejects(sending, /CHAT_IDENTITY_STALE/);
  host.state.identity = {
    sessionId: "native-1",
    servantId: "another-role",
    conversationId: "conversation-b",
  };
  host.state.draft = { text: "", revision: host.state.draft.revision };
  host.publish();
  host.release("chat.draft");
  await rejected;
  assert.equal(
    host.posts.some((p) => p.type === "chat.send"),
    false,
  );
  assert.equal(driver.getSnapshot().conversationId, "conversation-b");
  assert.equal(driver.getSnapshot().error, undefined);
});

test("history navigation waits for the newest draft typed during an earlier ACK", async (t) => {
  const { host, driver } = setup(t);
  await driver.connect();
  host.hold("chat.draft");
  driver.setDraft("Older draft");
  await pause(170);
  const opening = driver.action("historyOpen", { id: "conversation-b" });
  await tick();
  driver.setDraft("Latest draft before leaving");
  host.release("chat.draft");
  await opening;
  const relevant = host.posts.filter((p) =>
    ["chat.draft", "chat.history.open"].includes(p.type),
  );
  assert.deepEqual(
    relevant.map((p) => p.type),
    ["chat.draft", "chat.draft", "chat.history.open"],
  );
  assert.equal(relevant[1].payload?.text, "Latest draft before leaving");
  assert.equal(relevant[1].payload?.conversationId, "conversation-a");
  assert.equal(driver.getSnapshot().draft, "");
  assert.equal(driver.getSnapshot().conversationId, "conversation-b");
});

test("approval decisions bind to exact owner requests and review opens only the native action", async (t) => {
  const { host, driver } = setup(t);
  const binding = { runId: "run-a", requestId: "approval-a", revision: 4 };
  host.state.conversation.pendingApproval = {
    ...binding,
    expiresAt: new Date(Date.now() + 60_000).toISOString(),
    toolName: "writeFile",
    plainTextDetails: "Frozen owner details",
    actionTitle: "Write a note",
    plainTextSummary: "A native confirmation is required.",
    isSubmitting: false,
    errorText: "",
  };
  await driver.connect();
  assert.equal(driver.getSnapshot().phase, "approval");
  await assert.rejects(
    driver.action("approvalReview", { ...binding, revision: 3 }),
    /CHAT_APPROVAL_STALE/,
  );
  assert.equal(
    host.posts.some((p) => p.type.startsWith("chat.approval.")),
    false,
  );
  await driver.action("approvalReview", binding);
  assert.equal(
    host.posts.filter((p) => p.type === "chat.approval.review").length,
    1,
  );
  assert.equal(
    host.posts.some((p) => p.type.includes("allow")),
    false,
  );
  assert.deepEqual(driver.getSnapshot().tasks, []);
  assert.equal(driver.getSnapshot().focus, null);
  await assert.rejects(
    driver.action("taskAdd", { title: "No fake native task" }),
    /CHAT_NATIVE_ACTION_UNAVAILABLE/,
  );
  assert.deepEqual(driver.getSnapshot().tasks, []);
});

test("native handshake failure stays native and exposes a connection error", async (t) => {
  const { host, driver } = setup(t);
  host.hold("chat.get");
  const rejected = assert.rejects(driver.connect(), /CHAT_SESSION_CLOSED/);
  host.release("chat.get", "CHAT_SESSION_CLOSED");
  await rejected;
  assert.equal(driver.kind, "native");
  assert.equal(driver.getSnapshot().ready, false);
  assert.ok(driver.getSnapshot().connectionError);
  assert.equal(driver.getSnapshot().canSend, false);
  assert.deepEqual(driver.getSnapshot().tasks, []);
});

test("host theme colors are aliased, acknowledged and reflected without inventing a scheme field", async (t) => {
  const { host, driver } = setup(t);
  await driver.connect();
  host.emit({
    type: "theme.changed",
    version: 1,
    variables: {
      "--paper": "#FBFAF8",
      "--ink": "#221F1B",
      "--accent": "#B2718B",
    },
  });
  assert.equal(driver.getSnapshot().nativeTheme, "light");
  assert.equal(host.styles.get("--host-paper"), "#FBFAF8");
  assert.equal(host.styles.get("--host-accent"), "#B2718B");
  assert.deepEqual(host.posts.at(-1), { type: "theme.ack", version: 1 });
  // A future host can supply a dark palette; the current verified host sends light.
  host.emit({
    type: "theme.changed",
    version: 2,
    variables: { "--paper": "#201e20", "--ink": "#f2ede6" },
  });
  assert.equal(driver.getSnapshot().nativeTheme, "dark");
  host.emit({
    type: "theme.changed",
    version: 1,
    variables: { "--paper": "#FBFAF8" },
  });
  assert.equal(driver.getSnapshot().nativeTheme, "dark");
  assert.equal(host.styles.get("--host-paper"), "#201e20");
});


test("pending native runs retain the folder scope and reject context changes", async t => {
  const { host, driver } = setup(t);
  host.state.conversation.project = { id: "", label: "" };
  host.state.conversation.contextChips = [{ id: "project", label: "Fixture folder" }];
  assert.ok(host.state.presentation.actions);
  host.state.presentation.actions.canClearWorkspace = true;
  await driver.connect();
  assert.equal(driver.getSnapshot().projectLabel, "选择项目文件夹");
  assert.equal(driver.getSnapshot().availableActions?.chooseWorkspace, true);
  host.state.conversation.hasPendingRun = true;
  host.publish();
  const pending = driver.getSnapshot();
  assert.equal(pending.availableActions?.chooseWorkspace, false);
  assert.equal(pending.availableActions?.clearWorkspace, false);
  assert.equal(pending.availableActions?.removeContextChip, false);
  assert.equal(pending.contextChips[0].removable, false);
  const before = host.posts.length;
  await assert.rejects(driver.action("chooseWorkspace"), /CHAT_NOT_AVAILABLE/);
  await assert.rejects(driver.action("clearWorkspace"), /CHAT_NOT_AVAILABLE/);
  await assert.rejects(driver.action("removeContextChip", { id: "project" }), /CHAT_NOT_AVAILABLE/);
  assert.equal(host.posts.length, before);
});


test("model refresh remains available before the host has supplied choices", async t => {
  const { host, driver } = setup(t);
  host.state.presentation.models = [];
  await driver.connect();
  assert.equal(driver.getSnapshot().actions.model, false);
  assert.equal(driver.getSnapshot().availableActions?.refreshModels, true);
  await driver.action("refreshModels");
  assert.equal(host.posts.filter(p => p.type === "chat.host" && p.payload?.action === "refreshModels").length, 1);
  host.state.conversation.hasPendingRun = true;
  host.publish();
  await assert.rejects(driver.action("refreshModels"), /CHAT_NOT_AVAILABLE/);
});
