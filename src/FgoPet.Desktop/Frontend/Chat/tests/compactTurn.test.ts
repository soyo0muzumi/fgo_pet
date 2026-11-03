import assert from "node:assert/strict";
import test from "node:test";
import {
  startCompactTurn,
  recentCompactTurn,
  reconcileCompactTurn,
  compactTurnMessages,
} from "../src/compactTurn.ts";
import { emptyState, type Message, type UiState } from "../src/domain.ts";
import { createNativeBridge } from "../src/nativeBridge.ts";
import { createNativeDriver } from "../src/nativeDriver.ts";
import { MockNativeHost } from "./nativeHostFixture.ts";

const message = (id: string, role: Message["role"], text = id): Message => ({ id, role, text });
function state(overrides: Partial<UiState> = {}): UiState {
  return { ...emptyState(), ownerKey: "session/role-a", conversationId: "a", ...overrides };
}

test("waiting for the new user message never replays the previous reply", () => {
  const before = state({ messages: [message("old-user", "user"), message("old-reply", "assistant")] });
  const anchor = startCompactTurn(before);
  assert.equal(reconcileCompactTurn(anchor, before), anchor);
  assert.deepEqual(compactTurnMessages(anchor, before), []);
  const waiting = { ...before, messages: [
    message("old-user", "user"), message("old-reply", "assistant", "old reply updated"),
    message("unrelated-assistant", "assistant"),
  ] };
  assert.equal(reconcileCompactTurn(anchor, waiting), anchor);
  assert.deepEqual(compactTurnMessages(anchor, waiting), []);
  assert.deepEqual(anchor.baselineMessageIds, ["old-user", "old-reply"]);
});

test("the first new user establishes the boundary and every current-turn reply remains intact", () => {
  const before = state({ messages: [message("previous", "assistant")] });
  const anchor = startCompactTurn(before);
  const markdown = "```ts\nconst text = '" + "长内容".repeat(100) + "';\n```";
  const next = { ...before, messages: [
    ...before.messages, message("early-status", "assistant"),
    message("sent", "user"), message("reply-one", "assistant", "先看这个。"),
    message("reply-two", "assistant", markdown),
  ] };
  const current = reconcileCompactTurn(anchor, next)!;
  assert.equal(current.userId, "sent");
  assert.equal(reconcileCompactTurn(current, next), current);
  assert.deepEqual(compactTurnMessages(anchor, next).map(item => item.id), ["sent", "reply-one", "reply-two"]);
  assert.equal(compactTurnMessages(current, next).at(-1)?.text, markdown);
  assert.equal(anchor.userId, null, "reconciliation does not mutate the original anchor");
});

test("same-owner first-send admission can precede the user ACK but only happens once", () => {
  const before = state({ conversationId: "", messages: [message("welcome", "assistant")] });
  const anchor = startCompactTurn(before);
  const admitted = { ...before, conversationId: "created-a" };
  const current = reconcileCompactTurn(anchor, admitted)!;
  assert.equal(current.conversationId, "created-a");
  assert.equal(current.canAdmitConversation, false);
  assert.deepEqual(compactTurnMessages(current, admitted), []);
  const acknowledged = { ...admitted, messages: [...before.messages, message("sent", "user")] };
  const locked = reconcileCompactTurn(current, acknowledged)!;
  assert.equal(locked.userId, "sent");
  assert.equal(reconcileCompactTurn(locked, acknowledged), locked);
  assert.equal(reconcileCompactTurn(locked, { ...acknowledged, conversationId: "" }), null);
  assert.equal(reconcileCompactTurn(locked, { ...acknowledged, conversationId: "created-b" }), null);
  assert.deepEqual(compactTurnMessages(locked, { ...acknowledged, conversationId: "created-b" }), []);
});

test("a user ACK arriving before first conversation admission is retained", () => {
  const before = state({ conversationId: "" });
  const waiting = { ...before, messages: [message("sent", "user"), message("reply", "assistant")] };
  const anchored = reconcileCompactTurn(startCompactTurn(before), waiting)!;
  const admitted = { ...waiting, conversationId: "created" };
  const current = reconcileCompactTurn(anchored, admitted)!;
  assert.equal(current.userId, "sent");
  assert.deepEqual(compactTurnMessages(current, admitted).map(item => item.id), ["sent", "reply"]);
});

test("owner and established conversation changes invalidate the anchor immediately", () => {
  const before = state();
  const anchor = startCompactTurn(before);
  const later = { ...before, messages: [message("new-user", "user"), message("reply", "assistant")] };
  for (const changed of [
    { ...later, ownerKey: "other-session/role-a" },
    { ...later, ownerKey: "session/role-b" },
    { ...later, ownerKey: undefined },
    { ...later, conversationId: "other-conversation" },
    { ...later, conversationId: "" },
  ]) {
    assert.equal(reconcileCompactTurn(anchor, changed), null);
    assert.deepEqual(compactTurnMessages(anchor, changed), []);
  }
  const blank = startCompactTurn({ ...before, conversationId: "" });
  assert.equal(reconcileCompactTurn(blank, { ...later, ownerKey: "another-role", conversationId: "created" }), null);
  assert.equal(reconcileCompactTurn(null, later), null);
});

test("a later send starts its own baseline and a missing locked user cannot select replacement history", () => {
  const initial = state({ ownerKey: undefined });
  const first = { ...initial, messages: [message("user-one", "user"), message("reply-one", "assistant")] };
  const firstAnchor = reconcileCompactTurn(startCompactTurn(initial), first)!;
  const secondAnchor = startCompactTurn(first);
  assert.deepEqual(compactTurnMessages(secondAnchor, first), []);
  const second = { ...first, messages: [...first.messages, message("user-two", "user"), message("reply-two", "assistant")] };
  assert.deepEqual(compactTurnMessages(secondAnchor, second).map(item => item.id), ["user-two", "reply-two"]);
  const replaced = { ...first, messages: [message("different-user", "user"), message("different-reply", "assistant")] };
  assert.equal(reconcileCompactTurn(firstAnchor, replaced), firstAnchor);
  assert.deepEqual(compactTurnMessages(firstAnchor, replaced), []);
});

test("returning from full reading selects the last user and keeps its complete replies", () => {
  const markdown = "## 这一次的回复\n\n```ts\nconst answer = 42;\n```";
  const current = state({ messages: [
    message("first-user", "user"), message("first-reply", "assistant"),
    message("latest-user", "user"), message("latest-reply", "assistant", markdown),
    message("latest-status", "system"), message("latest-continuation", "assistant"),
  ] });
  const anchor = recentCompactTurn(current)!;
  assert.equal(anchor.userId, "latest-user");
  assert.equal(reconcileCompactTurn(anchor, current), anchor);
  assert.deepEqual(compactTurnMessages(anchor, current), current.messages.slice(2));
  assert.equal(compactTurnMessages(anchor, current)[1].text, markdown);
  const continued = { ...current, messages: [...current.messages, message("additional-reply", "assistant")] };
  assert.equal(compactTurnMessages(anchor, continued).at(-1)?.id, "additional-reply");
});

test("returning without a user message does not invent a current turn", () => {
  for (const messages of [[], [message("system-note", "system")], [message("welcome", "assistant")]]) {
    const current = state({ messages });
    assert.equal(recentCompactTurn(current), null);
    assert.deepEqual(compactTurnMessages(recentCompactTurn(current), current), []);
  }
});

test("a returned turn stays bound to its conversation and owner", () => {
  const current = state({ messages: [message("sent", "user"), message("reply", "assistant")] });
  const anchor = recentCompactTurn(current)!;
  for (const changed of [
    { ...current, conversationId: "history-conversation" },
    { ...current, ownerKey: "session/another-role" },
    { ...current, ownerKey: "another-session/role-a" },
  ]) {
    assert.equal(reconcileCompactTurn(anchor, changed), null);
    assert.deepEqual(compactTurnMessages(anchor, changed), []);
  }
});

test("native UI ownership stays stable during first send and changes with the native role", async t => {
  const host = new MockNativeHost();
  host.state.identity.conversationId = "";
  const restore = host.install();
  const driver = createNativeDriver(createNativeBridge()!);
  t.after(() => { driver.dispose(); restore(); });
  await driver.connect();
  const before = driver.getSnapshot();
  assert.equal(before.ownerKey, JSON.stringify([host.state.identity.sessionId, host.state.identity.servantId]));
  const anchor = startCompactTurn(before);
  await driver.send("First message");
  const admitted = driver.getSnapshot();
  assert.equal(admitted.ownerKey, before.ownerKey);
  assert.equal(admitted.conversationId, "admitted-conversation");
  const current = reconcileCompactTurn(anchor, admitted)!;
  assert.ok(current);
  assert.deepEqual(compactTurnMessages(current, admitted), []);
  host.state.identity.servantId = "other-role";
  host.publish();
  assert.notEqual(driver.getSnapshot().ownerKey, before.ownerKey);
  assert.equal(reconcileCompactTurn(current, driver.getSnapshot()), null);
});
