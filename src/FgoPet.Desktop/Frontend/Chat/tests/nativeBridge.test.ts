import assert from "node:assert/strict";
import test, { type TestContext } from "node:test";
import { createNativeBridge } from "../src/nativeBridge.ts";
import { MockNativeHost, tick } from "./nativeHostFixture.ts";

function setup(t: TestContext) {
  const host = new MockNativeHost(),
    restore = host.install();
  const bridge = createNativeBridge()!;
  t.after(() => {
    bridge.dispose();
    restore();
  });
  return { host, bridge };
}

test("bridge uses exact get/draft/send schemas and rejects invented commands", async (t) => {
  const { host, bridge } = setup(t);
  await bridge.ready();
  await bridge.sendDraftThenSend("Hello", 1);
  assert.deepEqual(host.posts.find((p) => p.type === "chat.get")?.payload, {});
  const draft = host.posts.find((p) => p.type === "chat.draft")!;
  assert.equal(draft.payload?.text, "Hello");
  assert.equal(draft.payload?.revision, 1);
  assert.deepEqual(
    Object.keys(
      host.posts.find((p) => p.type === "chat.send")!.payload!,
    ).sort(),
    ["conversationId", "servantId", "sessionId"],
  );
  await assert.rejects(
    bridge.execute("chat.approval.allow", {}),
    /CHAT_COMMAND_INVALID/,
  );
  await assert.rejects(
    bridge.execute("chat.host", { action: "resize", width: 500 }),
    /CHAT_COMMAND_INVALID/,
  );
  await assert.rejects(
    bridge.execute("chat.send", { text: "must be in draft" }),
    /CHAT_COMMAND_INVALID/,
  );
});

test("late command results and retired-session events cannot reopen old conversations", async (t) => {
  const { host, bridge } = setup(t);
  await bridge.ready();
  host.hold("chat.host");
  const copy = bridge.execute("chat.host", {
    action: "copyTurn",
    targetId: "reply-a",
  });
  await tick();
  const retired = structuredClone(host.held[0].snapshot);
  await bridge.execute("chat.history.open", {
    targetConversationId: "conversation-b",
  });
  host.release("chat.host");
  assert.equal((await copy).identity.conversationId, "conversation-b");
  retired.version += 100;
  host.emit({ type: "chat.snapshot", payload: retired });
  assert.equal(
    (await bridge.ready()).identity.conversationId,
    "conversation-b",
  );
});

test("timeouts and disposal reject requests and ignore late acknowledgements", async (t) => {
  const { host, bridge } = setup(t);
  await bridge.ready();
  host.hold("chat.stop");
  await assert.rejects(
    bridge.execute("chat.stop", {}, { timeoutMs: 5 }),
    /CHAT_TIMEOUT/,
  );
  host.release("chat.stop");
  host.hold("chat.host");
  const pending = bridge.execute("chat.host", { action: "chooseImage" });
  await tick();
  bridge.dispose();
  await assert.rejects(pending, /CHAT_BRIDGE_DISPOSED/);
  host.release("chat.host");
});

test("presentation capability is explicit; legacy fallback never claims a composer", async (t) => {
  const { host, bridge } = setup(t);
  delete host.state.presentation.window;
  await bridge.ready();
  const legacy = await bridge.resizePresentation("composer", false, 210);
  assert.equal(legacy.compatible, false);
  assert.ok(legacy.warning);
  assert.deepEqual(host.posts.at(-1)?.payload, {
    ...host.state.identity,
    action: "setExpanded",
    expanded: false,
  });
  host.state.presentation.window = {
    view: "composer",
    wide: false,
    composerHeight: 160,
  };
  host.publish();
  const patched = await bridge.resizePresentation("conversation", true, 999);
  assert.equal(patched.compatible, true);
  assert.equal(host.posts.at(-1)?.payload?.action, "setPresentation");
  assert.equal(host.posts.at(-1)?.payload?.composerHeight, 360);
});

test("image transfers preserve byte offsets, sequential chunks and final completion", async (t) => {
  const { host, bridge } = setup(t);
  await bridge.ready();
  await bridge.importImage(
    new File([new Uint8Array(50_000)], "test.png", { type: "image/png" }),
  );
  const chunks = host.posts.filter((p) => p.type === "chat.image");
  assert.deepEqual(
    chunks.map((p) => p.payload?.offset),
    [0, 24_576, 49_152],
  );
  assert.deepEqual(
    chunks.map((p) => p.payload?.complete),
    [false, false, true],
  );
  assert.equal(new Set(chunks.map((p) => p.payload?.transferId)).size, 1);
  assert.match(String(chunks[0].payload?.transferId), /^[0-9a-f]{32}$/);
  assert.equal(
    Buffer.from(String(chunks[0].payload?.data), "base64").length,
    24_576,
  );
  await assert.rejects(
    bridge.importImage(new File(["x"], "test.gif", { type: "image/gif" })),
    /CHAT_IMAGE_INVALID/,
  );
});
